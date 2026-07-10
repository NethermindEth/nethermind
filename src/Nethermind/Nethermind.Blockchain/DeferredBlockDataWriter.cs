// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Blockchain;

/// <summary>
/// Executes block-data persistence work (receipt writes, canonical tx-index writes, block-body
/// writes, and block-access-list writes) on a single background consumer so the engine API paths
/// do not wait on RocksDB.
/// </summary>
/// <remarks>
/// Durability is deferred, not visibility: callers publish an in-memory overlay before enqueueing.
/// FIFO ordering is load-bearing for canonical-index last-writer-wins semantics and the relative
/// ordering of each block's data writes, including during fault recovery.
/// </remarks>
public interface IDeferredBlockDataWriter : IAsyncDisposable
{
    /// <summary>Whether deferral is enabled. When false, <see cref="Enqueue"/> runs work inline.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Queues <paramref name="work"/> for background execution, or runs it inline when deferral is
    /// disabled. After a fault, replays retained work inline and re-arms background execution once
    /// recovery succeeds. Blocks when the bounded queue is full rather than growing memory.
    /// </summary>
    void Enqueue(Action work);

    /// <summary>
    /// Blocks until every write queued before this call has durably executed, retrying inline any that
    /// failed. Throws if a write is still failing, so the caller aborts the state persist rather than
    /// letting state outlive lost block data. The barrier calls this before fsyncing state.
    /// </summary>
    void Drain();
}

internal interface IDeferredWriteOperation
{
    void Execute();
}

internal static class DeferredBlockDataWriterExtensions
{
    public static void Enqueue(this IDeferredBlockDataWriter writer, IDeferredWriteOperation operation)
    {
        if (writer is DeferredBlockDataWriter deferredWriter)
        {
            deferredWriter.Enqueue(operation);
        }
        else
        {
            writer.Enqueue(operation.Execute);
        }
    }
}

/// <inheritdoc cref="IDeferredBlockDataWriter"/>
public sealed class DeferredBlockDataWriter : IDeferredBlockDataWriter
{
    private readonly int _capacity;
    private readonly bool _manualPump;
    private readonly ILogger _logger;
    private readonly Lock _faultLock = new();
    private volatile WriterGeneration? _generation;
    // Published before completing a faulted generation. It retains that generation's failed write and
    // unread backlog until ordered synchronous recovery succeeds and a fresh generation is published.
    private volatile FaultState? _faultState;
    private Exception? _faultException;
    private volatile bool _unrecoverable;

    /// <param name="enabled">When false the writer is a no-op passthrough that runs work inline.</param>
    /// <param name="capacity">Maximum queued work items before producers backpressure. Note this
    /// counts individual writes, not blocks: a BAL-enabled block can enqueue up to five items, although
    /// superseded pending writes are coalesced.</param>
    /// <param name="logManager">Log manager.</param>
    /// <param name="persistenceBarrier">Barrier to register this writer's <see cref="Drain"/> with, so a
    /// state persist drains queued writes before fsyncing. No drain is registered when null/disabled.</param>
    public DeferredBlockDataWriter(bool enabled, int capacity, ILogManager logManager, IStatePersistenceBarrier? persistenceBarrier = null)
        : this(enabled, capacity, logManager, persistenceBarrier, startConsumer: true)
    {
    }

    internal DeferredBlockDataWriter(bool enabled, int capacity, ILogManager logManager, IStatePersistenceBarrier? persistenceBarrier, bool startConsumer)
    {
        _logger = logManager.GetClassLogger<DeferredBlockDataWriter>();
        Enabled = enabled;
        _capacity = Math.Max(1, capacity);
        _manualPump = !startConsumer;
        if (enabled)
        {
            _generation = CreateGeneration();

            if (persistenceBarrier is { IsEnabled: true })
            {
                persistenceBarrier.RegisterDrain(Drain);
            }
        }
    }

    public bool Enabled { get; }

    internal int QueuedCount => _generation?.Channel.Reader.Count ?? 0;

    public void Enqueue(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Enqueue(new ActionOperation(work));
    }

    internal void Enqueue(IDeferredWriteOperation work)
    {
        if (!Enabled)
        {
            work.Execute();
            return;
        }

        while (true)
        {
            ThrowIfUnrecoverable();

            if (_faultState is { } faultState)
            {
                if (TryRecoverAndRearm(faultState, work)) return;
                continue;
            }

            WriterGeneration generation = _generation!;
            if (TryEnqueue(generation, work)) return;

            // A normally completed current generation means disposal raced this enqueue. Preserve the
            // previous fallback behaviour; a retired faulted generation instead loops into its replacement.
            if (ReferenceEquals(generation, _generation)
                && _faultState is null
                && generation.Channel.Reader.Completion.IsCompleted)
            {
                work.Execute();
                return;
            }
        }
    }

    private bool TryEnqueue(WriterGeneration generation, IDeferredWriteOperation work)
    {
        while (!generation.Channel.Writer.TryWrite(work))
        {
            if (_unrecoverable
                || _faultState is not null
                || !ReferenceEquals(generation, _generation)
                || generation.Channel.Reader.Completion.IsCompleted)
            {
                return false;
            }

            // Backpressure: block the producer so a slow disk degrades to synchronous, not unbounded memory.
            // Channel may return an incomplete IValueTaskSource-backed ValueTask, whose GetResult does not block.
            if (!generation.Channel.Writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult()) return false;
        }

        return true;
    }

    private bool TryRecoverAndRearm(FaultState observed, IDeferredWriteOperation? work = null)
    {
        // Producers may have observed an old fault while another producer re-armed the writer. Waiting on the
        // fault-owned completion, rather than the mutable current consumer, cannot accidentally await its replacement.
        observed.ConsumerStopped.Task.GetAwaiter().GetResult();

        lock (_faultLock)
        {
            if (!ReferenceEquals(observed, _faultState)) return false;

            ThrowIfUnrecoverable();
            if (work is not null) observed.RetainedWrites.Enqueue(work);
            RecoverOrThrowUnderLock(observed);
            RearmUnderLock(observed);
            return true;
        }
    }

    /// <summary>
    /// Synchronously executes all currently queued work. Valid only when the writer was constructed
    /// with <c>startConsumer: false</c>; throws otherwise. For tests, to make pre-flush states
    /// deterministic.
    /// </summary>
    /// <exception cref="InvalidOperationException">The background consumer is running.</exception>
    internal void Pump()
    {
        if (!_manualPump) throw new InvalidOperationException("Pump is only usable when the consumer task is not running.");
        WriterGeneration? generation = _generation;
        while (generation is not null && generation.Channel.Reader.TryRead(out IDeferredWriteOperation? work))
        {
            if (!Run(generation, work))
            {
                RetainBufferedWrites(_faultState!);
                return;
            }
        }
    }

    public void Drain()
    {
        if (!Enabled) return; // disabled: work ran inline, nothing is queued

        if (_manualPump)
        {
            Pump();
            while (_faultState is { } faultState)
            {
                if (TryRecoverAndRearm(faultState)) return;
            }
            return;
        }

        while (true)
        {
            ThrowIfUnrecoverable();
            if (_faultState is { } faultState)
            {
                if (TryRecoverAndRearm(faultState)) return;
                continue;
            }

            WriterGeneration generation = _generation!;
            TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryEnqueue(generation, new ActionOperation(drained.SetResult))) continue;

            // A marker proves every earlier write in this generation executed. Consumer termination wins the
            // race on a fault, in which case the loop recovers the retained marker with the rest of the backlog.
            Task.WhenAny(drained.Task, generation.Consumer!).GetAwaiter().GetResult();
            if (drained.Task.IsCompleted) return;
        }
    }

    private void RecoverOrThrowUnderLock(FaultState faultState)
    {
        Queue<IDeferredWriteOperation> retained = faultState.RetainedWrites;
        while (retained.Count > 0)
        {
            IDeferredWriteOperation work = retained.Peek();
            try
            {
                work.Execute();
            }
            catch (Exception exception)
            {
                _faultException = exception;
                throw new InvalidOperationException("Deferred block-data write still failing; aborting state persistence.", exception);
            }

            retained.Dequeue();
        }
    }

    private void RearmUnderLock(FaultState recovered)
    {
        if (!ReferenceEquals(recovered, _faultState)) return;

        // Publish the replacement before clearing the fault. A producer that observed the old fault either
        // joins its ordered replay under _faultLock or retries against this fully initialized generation.
        _generation = CreateGeneration();
        _faultException = null;
        _faultState = null;
        if (_logger.IsInfo) _logger.Info("Deferred block-data writer recovered; background persistence resumed.");
    }

    private void ThrowIfUnrecoverable()
    {
        if (_unrecoverable)
        {
            throw new InvalidOperationException("Deferred writer consumer stopped unexpectedly; aborting state persistence.", _faultException);
        }
    }

    private WriterGeneration CreateGeneration()
    {
        Channel<IDeferredWriteOperation> channel = Channel.CreateBounded<IDeferredWriteOperation>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        WriterGeneration generation = new(channel);
        if (!_manualPump)
        {
            generation.Consumer = Task.Run(() => RunAsync(generation));
        }
        return generation;
    }

    private async Task RunAsync(WriterGeneration generation)
    {
        try
        {
            await foreach (IDeferredWriteOperation work in generation.Channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!Run(generation, work))
                {
                    RetainBufferedWrites(_faultState!);
                    return;
                }
            }
        }
        catch (Exception e)
        {
            // Never exit silently: make producers and Drain fail rather than hanging on a lost marker.
            Fault(generation, e);
        }
    }

    private bool Run(WriterGeneration generation, IDeferredWriteOperation work)
    {
        try
        {
            work.Execute();
            return true;
        }
        catch (Exception firstException)
        {
            if (_logger.IsWarn) _logger.Warn($"Deferred block-data write failed, retrying once: {firstException}");
            try
            {
                work.Execute();
                return true;
            }
            catch (Exception secondException)
            {
                RetainFailure(generation, work, secondException);
                return false;
            }
        }
    }

    // Stop at the first hard failure; executing later buffered writes first would invert canonical-index updates.
    private void RetainFailure(WriterGeneration generation, IDeferredWriteOperation work, Exception exception)
    {
        lock (_faultLock)
        {
            FaultState faultState = new(generation);
            faultState.RetainedWrites.Enqueue(work);
            _faultException = exception;
            _faultState = faultState;
        }
        generation.Channel.Writer.TryComplete();
        if (_logger.IsError) _logger.Error("Deferred block-data write failed; preserving queued order for inline recovery.", exception);
    }

    private void RetainBufferedWrites(FaultState faultState)
    {
        try
        {
            lock (_faultLock)
            {
                while (faultState.Generation.Channel.Reader.TryRead(out IDeferredWriteOperation? work))
                {
                    faultState.RetainedWrites.Enqueue(work);
                }
            }
        }
        finally
        {
            faultState.ConsumerStopped.TrySetResult();
        }
    }

    // The consumer loop itself died (not a single write) - nothing to retry, so persistence must abort.
    private void Fault(WriterGeneration generation, Exception exception)
    {
        _faultException = exception;
        _unrecoverable = true; // volatile write publishes _faultException
        generation.Channel.Writer.TryComplete(exception);
        if (_logger.IsError) _logger.Error("Deferred block-data consumer stopped unexpectedly; aborting persistence.", exception);
    }

    public async ValueTask DisposeAsync()
    {
        WriterGeneration? generation = _generation;
        if (generation is null) return;

        generation.Channel.Writer.TryComplete();
        if (generation.Consumer is not null)
        {
            await generation.Consumer.ConfigureAwait(false);
        }
        else
        {
            Pump();
        }
    }

    private sealed class WriterGeneration(Channel<IDeferredWriteOperation> channel)
    {
        public Channel<IDeferredWriteOperation> Channel { get; } = channel;
        public Task? Consumer { get; set; }
    }

    private sealed class FaultState(WriterGeneration generation)
    {
        public WriterGeneration Generation { get; } = generation;
        public Queue<IDeferredWriteOperation> RetainedWrites { get; } = new();
        public TaskCompletionSource ConsumerStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ActionOperation(Action action) : IDeferredWriteOperation
    {
        public void Execute() => action();
    }
}
