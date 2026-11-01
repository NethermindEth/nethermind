// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
/// <remarks>The transaction tracers have no <c>vmTrace</c> slot to fill, so they never write to
/// <paramref name="writer"/>: they buffer the trace like <see cref="ParityLikeTxTracer"/> but pool what it holds.</remarks>
internal sealed class ParallelParityTxTracerPool(Utf8JsonWriter writer, CancellationToken cancellationToken, ParityTraceTypes types, IReleaseSpec? spec) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Stack<StreamingParityLikeTxTracer> _idle = new();
    private readonly Stack<TxTracer> _idleResults = new();
    private bool _disposed;

    /// <summary>Traces the transaction <paramref name="txHash"/>; its result returns the transaction tracer to the pool when disposed.</summary>
    public IBlockTracer<ParityLikeTxTrace> For(Hash256 txHash)
    {
        TxTracer? result;
        lock (_lock) _idleResults.TryPop(out result);
        result ??= new TxTracer(this);
        result.Start(txHash);
        return result;
    }

    private StreamingParityLikeTxTracer Rent(Block block, Transaction tx)
    {
        StreamingParityLikeTxTracer? tracer;
        lock (_lock) _idle.TryPop(out tracer);
        if (tracer is null)
            return new StreamingParityLikeTxTracer(block, tx, types, writer, pipeWriter: null, cancellationToken, fillVmTraceSlot: false, spec: spec);

        tracer.ResetForNextTx(block, tx);
        return tracer;
    }

    private void Return(TxTracer result)
    {
        lock (_lock)
        {
            if (!_disposed) _idleResults.Push(result);
        }
    }

    private void Return(StreamingParityLikeTxTracer tracer, bool completed)
    {
        lock (_lock)
        {
            if (completed && !_disposed)
            {
                _idle.Push(tracer);
                return;
            }
        }

        tracer.ReleaseResources();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            while (_idle.TryPop(out StreamingParityLikeTxTracer? tracer)) tracer.ReleaseResources();
            _idleResults.Clear();
        }
    }

    /// <summary>Traces one transaction and is its own result; reused for the next transaction once disposed.</summary>
    private sealed class TxTracer(ParallelParityTxTracerPool pool)
        : IBlockTracer<ParityLikeTxTrace>, IReadOnlyList<ParityLikeTxTrace>, IDisposable
    {
        private Hash256? _txHash;
        private Block? _block;
        private StreamingParityLikeTxTracer? _txTracer;
        private ParityLikeTxTrace? _trace;

        public bool IsTracingRewards => false;

        public int Count => _trace is null ? 0 : 1;

        public void Start(Hash256 txHash) => _txHash = txHash;

        public ParityLikeTxTrace this[int index] => index == 0 && _trace is not null ? _trace : throw new ArgumentOutOfRangeException(nameof(index));

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

        public void StartNewBlockTrace(Block block) => _block = block;

        public ITxTracer StartNewTxTrace(Transaction? tx) =>
            tx is null || tx.Hash != _txHash || _txTracer is not null
                ? NullTxTracer.Instance
                : _txTracer = pool.Rent(_block!, tx);

        public void EndTxTrace()
        {
            if (_txTracer is not null) _trace ??= _txTracer.BuildResult();
        }

        public void EndBlockTrace() { }

        public IReadOnlyCollection<ParityLikeTxTrace> BuildResult() => this;

        public IEnumerator<ParityLikeTxTrace> GetEnumerator()
        {
            if (_trace is not null) yield return _trace;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Returns the transaction tracer to the pool, one that did not finish its trace is released instead,
        /// and then this result itself; a second call does nothing.</summary>
        public void Dispose()
        {
            if (_txHash is null) return;

            if (_txTracer is not null) pool.Return(_txTracer, completed: _trace is not null);
            _txTracer = null;
            _trace = null;
            _block = null;
            _txHash = null;
            pool.Return(this);
        }
    }
}
