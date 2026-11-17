// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Consensus.Tracing;

/// <summary>Where the workers of a block traced in parallel leave each transaction's traces, by its index in the
/// block; the index after the last transaction holds the rewards.</summary>
/// <remarks>Instances are reused from block to block: <see cref="Clear"/> ends one block, and each kind has its own
/// call that starts the next.</remarks>
internal abstract class ParallelTraceResults<TTrace> : IDisposable
{
    /// <summary>Called before a worker takes its next transaction off the cursor, outside the processing permit.</summary>
    public virtual void Reserve(CancellationToken token) { }

    /// <summary>Gives back a reservation the cursor had no transaction for.</summary>
    public virtual void Unreserve() { }

    /// <summary>Called once the last transaction is taken off the cursor: no reservation can lead to a transaction any
    /// more, so up to <paramref name="waiters"/> workers waiting in <see cref="Reserve"/> are let go, to find the cursor
    /// spent and give their reservations back.</summary>
    public virtual void LastTaken(int waiters) { }

    /// <summary>Takes the traces of transaction <paramref name="index"/>, on the worker that traced it.</summary>
    public abstract void Add(int index, IReadOnlyCollection<TTrace> traces);

    /// <summary>Checks that the run handed everything on. Called once every worker has returned.</summary>
    public virtual void Complete() { }

    /// <summary>Releases what a failed run finished and nobody received. Called once every worker has returned.</summary>
    public abstract void DisposeUnpublished();

    /// <summary>Lets go of the block, so the instance holds nothing of it while it waits for the next one.</summary>
    public abstract void Clear();

    public virtual void Dispose() { }

    public static void DisposeTraces(IReadOnlyCollection<TTrace> traces)
    {
        foreach (TTrace trace in traces) trace.TryDispose();
        traces.TryDispose();
    }
}

/// <summary>Holds every transaction's traces until the block is done, for a caller that takes them all at once.</summary>
internal sealed class CollectedTraceResults<TTrace> : ParallelTraceResults<TTrace>
{
    private IReadOnlyCollection<TTrace>?[] _results = [];
    private int _count;

    public void Start(int count)
    {
        _results = ArrayPool<IReadOnlyCollection<TTrace>?>.Shared.Rent(count);
        // A non-null entry means a finished transaction, so nothing another renter left may remain.
        Array.Clear(_results, 0, count);
        _count = count;
    }

    public override void Add(int index, IReadOnlyCollection<TTrace> traces) => _results[index] = traces;

    public List<TTrace> Flatten()
    {
        List<TTrace> all = [with(_count)];
        for (int i = 0; i < _count; i++)
        {
            if (_results[i] is { } result) all.AddRange(result);
        }

        return all;
    }

    public override void DisposeUnpublished()
    {
        for (int i = 0; i < _count; i++)
        {
            IReadOnlyCollection<TTrace>? result = _results[i];
            if (result is null) continue;

            _results[i] = null;
            DisposeTraces(result);
        }
    }

    public override void Clear()
    {
        if (_results.Length != 0) ArrayPool<IReadOnlyCollection<TTrace>?>.Shared.Return(_results, clearArray: true);
        _results = [];
        _count = 0;
    }
}

/// <summary>Writes each transaction's traces to a stream in block order as soon as the transactions before it are
/// written, without ever making a worker wait for another one's write.</summary>
/// <remarks>
/// <para>A worker stores its slot and then tries to take the emission. The one that takes it writes every slot that
/// is ready in order, releases the emission, and looks at the next slot once more: a worker that stored that slot
/// while the emission was taken found it taken and left, trusting the holder to write it. The store and the attempt
/// to take, like the release and the second look, are full fences, so either the storing worker takes the emission
/// itself or the holder's second look sees the slot. A slot is written only while the emission is held, and is
/// cleared before it is written, so no slot is written twice.</para>
/// <para>A worker reserves a place in a window before it takes a transaction, and the place is given back when that
/// transaction is written. Only that many transactions are traced and not yet written, so a slow transaction, or a
/// client that stops reading, bounds the slots waiting behind it rather than letting the block pile up. The places
/// are reserved before the transactions are taken, so the oldest transaction not yet written always holds one, and
/// the window cannot fill with transactions that wait for it.</para>
/// </remarks>
internal abstract class OrderedTraceEmitter<TTrace, TSlot> : ParallelTraceResults<TTrace> where TSlot : class
{
    private readonly SemaphoreSlim _window = new(0, int.MaxValue);
    private TSlot?[] _slots = [];
    private int _length;
    private int _transactions;
    private int _next;
    private int _emitting;
    private bool _failed;

    /// <summary>Starts a block of <paramref name="transactions"/> with <paramref name="window"/> places.</summary>
    protected void Start(int transactions, int window)
    {
        _transactions = transactions;
        _length = transactions + 1;
        _slots = ArrayPool<TSlot?>.Shared.Rent(_length);
        // A non-null slot means a transaction ready to write, so nothing another renter left may remain.
        Array.Clear(_slots, 0, _length);
        _next = 0;
        _failed = false;

        // Every place of the last block came back unless it failed, and more once its last transaction was taken;
        // either way no worker of it is left waiting.
        int places = _window.CurrentCount;
        if (places < window) _window.Release(window - places);
        while (places-- > window) _window.Wait(0);
    }

    public override void Reserve(CancellationToken token)
    {
        if (!_window.Wait(0)) _window.Wait(token);
    }

    public override void Unreserve() => _window.Release();

    /// <remarks>The places released here are more than the window holds; <see cref="Start"/> trims them away again.</remarks>
    public override void LastTaken(int waiters)
    {
        if (waiters > 0) _window.Release(waiters);
    }

    public override void Add(int index, IReadOnlyCollection<TTrace> traces)
    {
        Volatile.Write(ref _slots[index], Prepare(traces));
        Emit();
    }

    /// <summary>Turns traces into what waits in a slot; runs on the worker, concurrently with the others.</summary>
    protected abstract TSlot Prepare(IReadOnlyCollection<TTrace> traces);

    /// <summary>Writes a slot to the stream and lets go of it, even when the write fails; runs while the emission is
    /// held.</summary>
    protected abstract void Write(TSlot slot);

    /// <summary>Lets go of a slot that will never be written.</summary>
    protected abstract void Release(TSlot slot);

    private void Emit()
    {
        while (Interlocked.CompareExchange(ref _emitting, 1, 0) == 0)
        {
            int next;
            try
            {
                next = WriteReady();
            }
            finally
            {
                Interlocked.Exchange(ref _emitting, 0);
            }

            if (next >= _length || Volatile.Read(ref _slots[next]) is null) return;
        }
    }

    private int WriteReady()
    {
        if (_failed) return _length;

        int next = _next;
        try
        {
            while (next < _length && Volatile.Read(ref _slots[next]) is { } slot)
            {
                _slots[next] = null;
                _next = ++next;
                Write(slot);
                if (next <= _transactions) _window.Release();
            }
        }
        catch
        {
            // The response is broken past this point: nothing more is written, and the slots left are the run's to
            // release once its workers stop.
            _failed = true;
            throw;
        }

        return next;
    }

    public override void Complete()
    {
        if (_next < _transactions)
            throw new InvalidOperationException($"A streamed block wrote {_next} of its {_transactions} transactions.");
    }

    public override void DisposeUnpublished()
    {
        for (int i = 0; i < _length; i++)
        {
            TSlot? slot = _slots[i];
            if (slot is null) continue;

            _slots[i] = null;
            Release(slot);
        }
    }

    public override void Clear()
    {
        if (_slots.Length != 0) ArrayPool<TSlot?>.Shared.Return(_slots, clearArray: true);
        _slots = [];
        _length = 0;
        _transactions = 0;
        _emitting = 0;
    }

    public override void Dispose() => _window.Dispose();
}

/// <summary>Serializes each transaction's traces on the worker that traced them, so only copying the bytes into the
/// response is left for the emission.</summary>
/// <remarks>Besides the window, a byte budget bounds what is serialized and not yet written: a worker that finds it
/// spent waits, before it takes another transaction, until the emission has written enough. The oldest transaction
/// not yet written was taken already, so the wait cannot keep it from being written.</remarks>
internal sealed class SerializingTraceEmitter<TTrace> : OrderedTraceEmitter<TTrace, SerializedTraces>
{
    private readonly ManualResetEventSlim _underBudget = new(true);
    private IParallelTraceStream<TTrace>? _stream;
    private SerializedTraces.Pool? _pool;
    private JsonWriterOptions _options;
    private long _budget;
    private long _pending;
    private bool _lastTaken;

    public void Start(IParallelTraceStream<TTrace> stream, JsonWriterOptions options, SerializedTraces.Pool pool, int transactions, int window, long budgetBytes)
    {
        _stream = stream;
        _options = options;
        _pool = pool;
        _budget = budgetBytes;
        _pending = 0;
        _lastTaken = false;
        _underBudget.Set();
        Start(transactions, window);
    }

    /// <summary>The chunk memory held by what is serialized and not yet written.</summary>
    internal long PendingBytes => Volatile.Read(ref _pending);

    public override void Reserve(CancellationToken token)
    {
        while (!Volatile.Read(ref _lastTaken) && Volatile.Read(ref _pending) > _budget)
        {
            _underBudget.Reset();
            // Read after the reset, as LastTaken writes the flag before it sets the event, so no wake-up is lost.
            if (Volatile.Read(ref _lastTaken) || Volatile.Read(ref _pending) <= _budget)
            {
                _underBudget.Set();
                break;
            }

            _underBudget.Wait(token);
        }

        base.Reserve(token);
    }

    public override void LastTaken(int waiters)
    {
        Volatile.Write(ref _lastTaken, true);
        _underBudget.Set();
        base.LastTaken(waiters);
    }

    protected override SerializedTraces Prepare(IReadOnlyCollection<TTrace> traces)
    {
        SerializedTraces serialized = _pool!.Rent(_options);
        try
        {
            Utf8JsonWriter writer = serialized.Start(_options);
            _stream!.Serialize(traces, writer);
            writer.Flush();
            serialized.Seal();
        }
        catch
        {
            _pool.Return(serialized);
            throw;
        }

        Interlocked.Add(ref _pending, serialized.Held);
        return serialized;
    }

    protected override void Write(SerializedTraces slot)
    {
        try
        {
            if (slot.Length != 0) _stream!.WriteSerialized(slot.Written);
        }
        finally
        {
            Release(slot);
        }
    }

    protected override void Release(SerializedTraces slot)
    {
        long held = slot.Held;
        _pool!.Return(slot);
        if (Interlocked.Add(ref _pending, -held) <= _budget) _underBudget.Set();
    }

    public override void Clear()
    {
        base.Clear();
        _stream = null;
        _pool = null;
    }

    public override void Dispose()
    {
        base.Dispose();
        _underBudget.Dispose();
    }
}

/// <summary>Hands each transaction's traces to the stream to serialize in block order, for a stream whose output
/// depends on what it wrote before.</summary>
internal sealed class InTurnTraceEmitter<TTrace> : OrderedTraceEmitter<TTrace, IReadOnlyCollection<TTrace>>
{
    private IParallelTraceStream<TTrace>? _stream;

    public void Start(IParallelTraceStream<TTrace> stream, int transactions, int window)
    {
        _stream = stream;
        Start(transactions, window);
    }

    protected override IReadOnlyCollection<TTrace> Prepare(IReadOnlyCollection<TTrace> traces) => traces;

    protected override void Write(IReadOnlyCollection<TTrace> slot) => _stream!.Write(slot);

    protected override void Release(IReadOnlyCollection<TTrace> slot) => DisposeTraces(slot);

    public override void Clear()
    {
        base.Clear();
        _stream = null;
    }
}
