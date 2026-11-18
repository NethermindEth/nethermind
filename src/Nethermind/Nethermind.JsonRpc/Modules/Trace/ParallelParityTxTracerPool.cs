// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>Per-transaction tracers for one block traced in parallel and streamed. Each hands its transaction tracer
/// back once its trace has been written, so the block needs about as many transaction tracers as there are workers.</summary>
/// <remarks>
/// <para>The transaction tracers have no <c>vmTrace</c> slot to fill, so they never write to
/// <paramref name="writer"/>: they buffer the trace like <see cref="ParityLikeTxTracer"/> but pool what it holds.</para>
/// <para>Each transaction tracer charges its own budget of <paramref name="maxRetainedBytes"/>, and fails exactly where
/// a transaction traced on its own would. The pool bounds what they keep together by the same limit. When each worker
/// serializes and releases its own traces, a charge that does not fit waits until the traces of earlier transactions
/// are released, rather than failing a block whose transactions each fit. The earliest transaction whose trace is not
/// released never waits, as nothing before it can make room; its own budget bounds it. Together the workers thus keep
/// at most twice the limit.</para>
/// <para>When traces are released only as the response is written, the room a charge waits for may never come back
/// while a client does not read, and a worker waits inside a trace, holding one of the node's processing permits. A
/// charge that does not fit then fails instead, but for the earliest transaction's.</para>
/// </remarks>
/// <param name="maxRetainedBytes">The most bytes the trace of one transaction may keep, and the transaction tracers
/// together, but for the earliest one.</param>
/// <param name="transactions">The number of transactions of the block.</param>
internal sealed class ParallelParityTxTracerPool(Utf8JsonWriter writer, CancellationToken cancellationToken, ParityTraceTypes types, IReleaseSpec? spec,
    long maxRetainedBytes, int transactions) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Stack<Lease> _idle = new();
    private readonly Stack<TxTracer> _idleResults = new();
    private bool _disposed;
    // Set while an idle transaction tracer may keep pooled inputs charged.
    private bool _idleKeepInputs;

    // Monitor for the charges that wait for room; _lock is never taken while holding it.
    private readonly object _room = new();
    private long _used;
    private int _waiting;
    private bool[]? _givenBack;
    // The earliest transaction whose trace is not given back yet.
    private int _earliest;
    private CancellationToken? _stop;
    private bool _waitForRoom;

    /// <summary>Stops the charges that wait for room. The request's token until the block's workers start, and then theirs,
    /// which is cancelled also when one of them fails: the trace a charge waits for may then never be given back.</summary>
    private CancellationToken Stop => _stop ?? cancellationToken;

    /// <summary>Called before the block's transactions are traced.</summary>
    /// <param name="stop">The token that stops the block's workers.</param>
    /// <param name="waitForRoom">Whether a charge that does not fit may wait for earlier traces to be released: only
    /// when each worker releases its own traces, never when they are released as the response is written.</param>
    public void OnBlockStart(CancellationToken stop, bool waitForRoom)
    {
        _stop = stop;
        _waitForRoom = waitForRoom;
    }

    /// <summary>The bytes the transaction tracers keep together.</summary>
    public long RetainedBytes => Interlocked.Read(ref _used);

    /// <summary>Traces the transaction <paramref name="txHash"/>; its result returns the transaction tracer to the pool when disposed.</summary>
    public IBlockTracer<ParityLikeTxTrace> For(Hash256 txHash)
    {
        TxTracer? result;
        lock (_lock) _idleResults.TryPop(out result);
        result ??= new TxTracer(this);
        result.Start(txHash);
        return result;
    }

    private Lease Rent(Block block, Transaction tx)
    {
        Lease? lease;
        lock (_lock) _idle.TryPop(out lease);
        if (lease is null)
        {
            lease = new Lease(this);
            lease.Tracer = new StreamingParityLikeTxTracer(block, tx, types, writer, pipeWriter: null, cancellationToken, fillVmTraceSlot: false,
                StreamingParityLikeTxTracer.FlushThreshold.Default, spec)
            {
                DataBudget = new TraceDataBudget(maxRetainedBytes, lease)
            };
        }
        else
        {
            lease.Tracer.ResetForNextTx(block, tx);
        }

        lease.Position = lease.Tracer.TransactionPosition ?? -1;
        return lease;
    }

    private void Return(TxTracer result)
    {
        lock (_lock)
        {
            if (!_disposed) _idleResults.Push(result);
        }
    }

    private void Return(Lease lease, bool completed)
    {
        int position = lease.Position;
        lease.Tracer.ReleaseRetainedData();
        // A charge waiting for room comes before inputs kept only for reuse, which giving the trace back keeps charged.
        bool keepInputs = Volatile.Read(ref _waiting) == 0;
        if (!keepInputs) lease.Tracer.DropPooledInputs();
        lease.Position = -1;
        bool pooled;
        lock (_lock)
        {
            pooled = completed && !_disposed;
            if (pooled)
            {
                _idle.Push(lease);
                if (keepInputs) Volatile.Write(ref _idleKeepInputs, true);
            }
        }

        if (!pooled) lease.Tracer.ReleaseResources();
        GiveBack(position);
    }

    /// <summary>Marks the trace of the transaction at <paramref name="position"/> as given back, so the next one may become the earliest.</summary>
    private void GiveBack(int position)
    {
        lock (_room)
        {
            if (position < _earliest || position >= transactions) return;

            bool[] givenBack = _givenBack ??= RentGivenBack();
            givenBack[position] = true;
            int earliest = _earliest;
            while (earliest < transactions && givenBack[earliest]) earliest++;
            Volatile.Write(ref _earliest, earliest);
            if (_waiting != 0) Monitor.PulseAll(_room);
        }
    }

    private bool[] RentGivenBack()
    {
        bool[] givenBack = ArrayPool<bool>.Shared.Rent(transactions);
        Array.Clear(givenBack, 0, transactions);
        return givenBack;
    }

    private void Charge(int position, long bytes, bool mayWait)
    {
        // A charge that fails anyway, as it passes the tracer's own budget, waits for nothing.
        if (!mayWait)
        {
            Interlocked.Add(ref _used, bytes);
            return;
        }

        if (TryCharge(bytes)) return;

        // Inputs kept only for reuse go before anything else.
        DropIdleInputs();
        if (TryCharge(bytes)) return;

        // Nothing before the earliest transaction can make room for it.
        if (position == Volatile.Read(ref _earliest)) Interlocked.Add(ref _used, bytes);
        else if (_waitForRoom) WaitAndCharge(position, bytes);
        else
        {
            // Counted also when failing: the tracer gives back every byte it charged.
            Interlocked.Add(ref _used, bytes);
            throw new TraceDataLimitExceededException(maxRetainedBytes);
        }
    }

    private void WaitAndCharge(int position, long bytes)
    {
        CancellationToken stop = Stop;
        using CancellationTokenRegistration wake = stop.UnsafeRegister(static pool => ((ParallelParityTxTracerPool)pool!).Wake(), this);
        lock (_room)
        {
            // Counted before the room is checked, and read after every release gives bytes back: a release either sees
            // this charge waiting and wakes it, or the check below sees the bytes it gave back.
            Interlocked.Increment(ref _waiting);
            try
            {
                while (!TryCharge(bytes))
                {
                    if (position == _earliest || stop.IsCancellationRequested)
                    {
                        // Counted also when cancelled: the tracer gives back every byte it charged.
                        Interlocked.Add(ref _used, bytes);
                        stop.ThrowIfCancellationRequested();
                        return;
                    }

                    Monitor.Wait(_room);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }
    }

    private bool TryCharge(long bytes)
    {
        long used = Volatile.Read(ref _used);
        while (true)
        {
            if (used + bytes > maxRetainedBytes) return false;
            long seen = Interlocked.CompareExchange(ref _used, used + bytes, used);
            if (seen == used) return true;
            used = seen;
        }
    }

    private void Release(long bytes)
    {
        Interlocked.Add(ref _used, -bytes);
        if (Volatile.Read(ref _waiting) != 0) Wake();
    }

    private void Wake()
    {
        lock (_room) Monitor.PulseAll(_room);
    }

    private void DropIdleInputs()
    {
        if (!Volatile.Read(ref _idleKeepInputs)) return;

        lock (_lock)
        {
            _idleKeepInputs = false;
            foreach (Lease lease in _idle) lease.Tracer.DropPooledInputs();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            while (_idle.TryPop(out Lease? lease)) lease.Tracer.ReleaseResources();
            _idleResults.Clear();
        }

        lock (_room)
        {
            if (_givenBack is not null) ArrayPool<bool>.Shared.Return(_givenBack);
            _givenBack = null;
        }
    }

    /// <summary>A transaction tracer of the pool, and the share of the pool's limit its budget charges for the transaction it traces.</summary>
    private sealed class Lease(ParallelParityTxTracerPool pool) : ITraceDataGate
    {
        public StreamingParityLikeTxTracer Tracer = null!;

        /// <summary>The position of the transaction traced; -1 when idle.</summary>
        public int Position = -1;

        public void Charge(long bytes, bool mayWait) => pool.Charge(Position, bytes, mayWait);

        public bool TryCharge(long bytes) => pool.TryCharge(bytes);

        public void Release(long bytes) => pool.Release(bytes);
    }

    /// <summary>Traces one transaction and is its own result; reused for the next transaction once disposed.</summary>
    private sealed class TxTracer(ParallelParityTxTracerPool pool)
        : IBlockTracer<ParityLikeTxTrace>, IReadOnlyList<ParityLikeTxTrace>, IDisposable
    {
        private Hash256? _txHash;
        private Block? _block;
        private Lease? _lease;
        private ParityLikeTxTrace? _trace;

        public bool IsTracingRewards => false;

        public int Count => _trace is null ? 0 : 1;

        public void Start(Hash256 txHash) => _txHash = txHash;

        public ParityLikeTxTrace this[int index] => index == 0 && _trace is not null ? _trace : throw new ArgumentOutOfRangeException(nameof(index));

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

        public void StartNewBlockTrace(Block block) => _block = block;

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            if (tx is null || tx.Hash != _txHash || _lease is not null) return NullTxTracer.Instance;

            _lease = pool.Rent(_block!, tx);
            return _lease.Tracer;
        }

        public void EndTxTrace()
        {
            if (_lease is not null) _trace ??= _lease.Tracer.BuildResult();
        }

        public void EndBlockTrace() { }

        public IReadOnlyCollection<ParityLikeTxTrace> BuildResult() => this;

        public IEnumerator<ParityLikeTxTrace> GetEnumerator()
        {
            if (_trace is not null) yield return _trace;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Returns the transaction tracer to the pool, one that did not finish its trace is released instead,
        /// and then this result itself. A second call before the result is rented again does nothing; its owner
        /// disposes it once, as every result is.</summary>
        public void Dispose()
        {
            if (_txHash is null) return;

            if (_lease is not null) pool.Return(_lease, completed: _trace is not null);
            _lease = null;
            _trace = null;
            _block = null;
            _txHash = null;
            pool.Return(this);
        }
    }
}
