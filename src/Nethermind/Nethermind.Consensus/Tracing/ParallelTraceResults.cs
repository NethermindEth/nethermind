// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Consensus.Tracing;

/// <summary>Where the workers of a block traced in parallel leave each transaction's traces, by its index in the
/// block; the index after the last transaction holds the rewards.</summary>
internal abstract class ParallelTraceResults<TTrace> : IDisposable
{
    /// <summary>Called before a worker takes its next transaction off the cursor, outside the processing permit.</summary>
    public virtual void Reserve(CancellationToken token) { }

    /// <summary>Gives back a reservation the cursor had no transaction for.</summary>
    public virtual void Unreserve() { }

    /// <summary>Takes the traces of transaction <paramref name="index"/>, on the worker that traced it.</summary>
    public abstract void Add(int index, IReadOnlyCollection<TTrace> traces);

    /// <summary>Checks that the run handed everything on. Called once every worker has returned.</summary>
    public virtual void Complete() { }

    /// <summary>Releases what a failed run finished and nobody received. Called once every worker has returned.</summary>
    public abstract void DisposeUnpublished();

    public virtual void Dispose() { }

    public static void DisposeTraces(IReadOnlyCollection<TTrace> traces)
    {
        foreach (TTrace trace in traces) trace.TryDispose();
        traces.TryDispose();
    }
}

/// <summary>Holds every transaction's traces until the block is done, for a caller that takes them all at once.</summary>
internal sealed class CollectedTraceResults<TTrace>(int count) : ParallelTraceResults<TTrace>
{
    private readonly IReadOnlyCollection<TTrace>?[] _results = new IReadOnlyCollection<TTrace>?[count];

    public override void Add(int index, IReadOnlyCollection<TTrace> traces) => _results[index] = traces;

    public List<TTrace> Flatten()
    {
        List<TTrace> all = new(_results.Length);
        foreach (IReadOnlyCollection<TTrace>? result in _results)
        {
            if (result is not null) all.AddRange(result);
        }

        return all;
    }

    public override void DisposeUnpublished()
    {
        for (int i = 0; i < _results.Length; i++)
        {
            IReadOnlyCollection<TTrace>? result = _results[i];
            if (result is null) continue;

            _results[i] = null;
            DisposeTraces(result);
        }
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
internal abstract class OrderedTraceEmitter<TTrace, TSlot>(int transactions, int window)
    : ParallelTraceResults<TTrace> where TSlot : class
{
    private readonly TSlot?[] _slots = new TSlot?[transactions + 1];
    private readonly SemaphoreSlim _window = new(window, window);
    private int _next;
    private int _emitting;
    private bool _failed;

    public override void Reserve(CancellationToken token) => _window.Wait(token);

    public override void Unreserve() => _window.Release();

    public override void Add(int index, IReadOnlyCollection<TTrace> traces)
    {
        Volatile.Write(ref _slots[index], Prepare(traces));
        Emit();
    }

    /// <summary>Turns traces into what waits in a slot; runs on the worker, concurrently with the others.</summary>
    protected abstract TSlot Prepare(IReadOnlyCollection<TTrace> traces);

    /// <summary>Writes a slot to the stream and lets go of it; runs while the emission is held.</summary>
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

            if (next >= _slots.Length || Volatile.Read(ref _slots[next]) is null) return;
        }
    }

    private int WriteReady()
    {
        if (_failed) return _slots.Length;

        int next = _next;
        try
        {
            while (next < _slots.Length && Volatile.Read(ref _slots[next]) is { } slot)
            {
                _slots[next] = null;
                _next = ++next;
                Write(slot);
                if (next <= transactions) _window.Release();
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

    public override void DisposeUnpublished()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            TSlot? slot = _slots[i];
            if (slot is null) continue;

            _slots[i] = null;
            Release(slot);
        }
    }

    public override void Complete()
    {
        if (_next < transactions)
            throw new InvalidOperationException($"A streamed block wrote {_next} of its {transactions} transactions.");
    }

    public override void Dispose() => _window.Dispose();
}

/// <summary>Serializes each transaction's traces on the worker that traced them, so only copying the bytes into the
/// response is left for the emission.</summary>
internal sealed class SerializingTraceEmitter<TTrace>(
    IParallelTraceStream<TTrace> stream, JsonWriterOptions options, SerializedTraces.Pool pool, int transactions, int window)
    : OrderedTraceEmitter<TTrace, SerializedTraces>(transactions, window)
{
    protected override SerializedTraces Prepare(IReadOnlyCollection<TTrace> traces)
    {
        SerializedTraces serialized = pool.Rent();
        try
        {
            Utf8JsonWriter writer = serialized.Start(options);
            stream.Serialize(traces, writer);
            writer.Flush();
            return serialized;
        }
        catch
        {
            pool.Return(serialized);
            throw;
        }
    }

    protected override void Write(SerializedTraces slot)
    {
        try
        {
            if (!slot.Written.IsEmpty) stream.WriteSerialized(slot.Written);
        }
        finally
        {
            pool.Return(slot);
        }
    }

    protected override void Release(SerializedTraces slot) => pool.Return(slot);
}

/// <summary>Hands each transaction's traces to the stream to serialize in block order, for a stream whose output
/// depends on what it wrote before.</summary>
internal sealed class InTurnTraceEmitter<TTrace>(IParallelTraceStream<TTrace> stream, int transactions, int window)
    : OrderedTraceEmitter<TTrace, IReadOnlyCollection<TTrace>>(transactions, window)
{
    protected override IReadOnlyCollection<TTrace> Prepare(IReadOnlyCollection<TTrace> traces) => traces;

    protected override void Write(IReadOnlyCollection<TTrace> slot) => stream.Write(slot);

    protected override void Release(IReadOnlyCollection<TTrace> slot) => DisposeTraces(slot);
}
