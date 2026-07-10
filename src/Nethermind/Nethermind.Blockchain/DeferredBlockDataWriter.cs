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
/// writes) on a single background consumer so the engine API paths do not wait on RocksDB.
/// </summary>
/// <remarks>
/// Durability is deferred, not visibility: callers publish an in-memory overlay before enqueueing.
/// The single FIFO queue is load-bearing - a block's body write must precede its receipts write
/// (compact-receipt sender recovery reads the body database directly).
/// </remarks>
public interface IDeferredBlockDataWriter : IAsyncDisposable
{
    /// <summary>Whether deferral is enabled. When false, <see cref="Enqueue"/> runs work inline.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Queues <paramref name="work"/> for background execution, or runs it inline when deferral is
    /// disabled or the writer has faulted. Blocks the calling thread when the bounded queue is full;
    /// this backpressure intentionally degrades to synchronous behaviour rather than growing memory.
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
    private readonly Channel<IDeferredWriteOperation>? _channel;
    private readonly Task? _consumer;
    private readonly bool _manualPump;
    private readonly ILogger _logger;
    private readonly Lock _faultLock = new();
    // Writes that failed twice, retained for an inline retry at the next Drain (their overlay entries stay
    // pending), so a transient disk fault self-heals instead of wedging persistence. Guarded by _faultLock.
    private List<IDeferredWriteOperation>? _failedWrites;
    // Set before _faulted (whose volatile write publishes them). _unrecoverable marks a dead consumer with
    // no retained work to retry, so Drain always aborts rather than proceeding.
    private Exception? _faultException;
    private volatile bool _unrecoverable;
    private volatile bool _faulted;

    /// <param name="enabled">When false the writer is a no-op passthrough that runs work inline.</param>
    /// <param name="capacity">Maximum queued work items before producers backpressure. Note this
    /// counts individual writes, not blocks: a BAL-enabled block can enqueue up to five items, although
    /// superseded pending writes are coalesced.</param>
    /// <param name="logManager">Log manager.</param>
    /// <param name="persistenceBarrier">Barrier to register this writer's <see cref="Drain"/> with, so a
    /// state persist drains queued writes before fsyncing. No drain is registered when null/disabled.</param>
    /// <param name="startConsumer">When false no background consumer runs and <see cref="Pump"/>
    /// must be driven manually. For tests only.</param>
    public DeferredBlockDataWriter(bool enabled, int capacity, ILogManager logManager, IStatePersistenceBarrier? persistenceBarrier = null, bool startConsumer = true)
    {
        _logger = logManager.GetClassLogger<DeferredBlockDataWriter>();
        Enabled = enabled;
        _manualPump = !startConsumer;
        if (enabled)
        {
            _channel = Channel.CreateBounded<IDeferredWriteOperation>(new BoundedChannelOptions(Math.Max(1, capacity))
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            if (startConsumer)
            {
                _consumer = Task.Run(RunAsync);
            }

            if (persistenceBarrier is { IsEnabled: true })
            {
                persistenceBarrier.RegisterDrain(Drain);
            }
        }
    }

    public bool Enabled { get; }

    internal int QueuedCount => _channel?.Reader.Count ?? 0;

    public void Enqueue(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Enqueue(new ActionOperation(work));
    }

    internal void Enqueue(IDeferredWriteOperation work)
    {
        if (_channel is null || _faulted)
        {
            work.Execute();
            return;
        }

        while (!_channel.Writer.TryWrite(work))
        {
            if (_faulted || _channel.Reader.Completion.IsCompleted)
            {
                work.Execute();
                return;
            }

            // Backpressure: block the producer so a slow disk degrades to synchronous, not unbounded memory.
            // Channel may return an incomplete IValueTaskSource-backed ValueTask, whose GetResult does not block.
            if (!_channel.Writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
            {
                work.Execute();
                return;
            }
        }
    }

    /// <summary>
    /// Synchronously executes all currently queued work. Valid only when the writer was constructed
    /// with <c>startConsumer: false</c>; throws otherwise. For tests, to make pre-flush states
    /// deterministic.
    /// </summary>
    /// <exception cref="InvalidOperationException">The background consumer is running.</exception>
    public void Pump()
    {
        if (!_manualPump) throw new InvalidOperationException("Pump is only usable when the consumer task is not running.");
        while (_channel is not null && _channel.Reader.TryRead(out IDeferredWriteOperation? work))
        {
            Run(work);
        }
    }

    public void Drain()
    {
        if (_channel is null) return;          // disabled: work ran inline, nothing is queued
        if (_manualPump) { Pump(); RecoverOrThrow(); return; }   // no background consumer (tests)

        if (!_faulted)
        {
            // Fence: a queued marker proves every earlier queued write has executed. Wait on the marker OR
            // consumer termination, so a stopped consumer aborts here instead of freezing this thread.
            TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Enqueue(drained.SetResult);
            Task.WhenAny(drained.Task, _consumer!).GetAwaiter().GetResult();
            if (drained.Task.IsCompleted && !_faulted) return;   // clean: all writes ran, none failed
        }

        // A write failed hard: the fault completed the channel, so the consumer finishes attempting every
        // buffered item (retaining failures) and exits. Wait for it, then retry the retained writes inline -
        // their overlay entries are still pending, so a recovered disk lands them and persistence may proceed;
        // a still-failing write aborts the persist rather than letting state outlive lost block data.
        _consumer!.GetAwaiter().GetResult();
        RecoverOrThrow();
    }

    private void RecoverOrThrow()
    {
        if (!_faulted) return;

        if (_unrecoverable)
        {
            throw new InvalidOperationException("Deferred writer consumer stopped unexpectedly; aborting state persistence.", _faultException);
        }

        lock (_faultLock)
        {
            if (_failedWrites is not { Count: > 0 }) return;   // no retained writes, or an earlier drain landed them

            List<IDeferredWriteOperation> retrying = _failedWrites;
            _failedWrites = null;
            List<IDeferredWriteOperation>? stillFailing = null;
            Exception? lastException = null;
            foreach (IDeferredWriteOperation work in retrying)
            {
                try
                {
                    work.Execute();
                }
                catch (Exception e)
                {
                    (stillFailing ??= []).Add(work);
                    lastException = e;
                }
            }

            if (stillFailing is not null)
            {
                _failedWrites = stillFailing;
                _faultException = lastException;
                throw new InvalidOperationException("Deferred block-data write still failing; aborting state persistence.", lastException);
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (IDeferredWriteOperation work in _channel!.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Run(work);
            }
        }
        catch (Exception e)
        {
            // Never exit silently: fault so Enqueue/Drain fall back inline instead of hanging on a lost marker.
            Fault(e);
        }
    }

    private void Run(IDeferredWriteOperation work)
    {
        try
        {
            work.Execute();
        }
        catch (Exception firstException)
        {
            if (_logger.IsWarn) _logger.Warn($"Deferred block-data write failed, retrying once: {firstException}");
            try
            {
                work.Execute();
            }
            catch (Exception secondException)
            {
                // Hard failure: retain the write for an inline retry at the next Drain and fall back inline
                // for future work (reads stay correct via the overlay). Drain lands or rethrows it.
                RetainFailure(work, secondException);
            }
        }
    }

    // A queued write failed twice. Retain it for an inline retry at the next Drain and complete the channel
    // so the consumer stops and all further work runs inline in producer order.
    private void RetainFailure(IDeferredWriteOperation work, Exception exception)
    {
        lock (_faultLock)
        {
            (_failedWrites ??= []).Add(work);
        }
        _faultException = exception;
        _faulted = true; // volatile write publishes the retained write and flips Enqueue inline
        _channel?.Writer.TryComplete();
        if (_logger.IsError) _logger.Error("Deferred block-data write failed; retrying inline before each state persist.", exception);
    }

    // The consumer loop itself died (not a single write) - nothing to retry, so persistence must abort.
    private void Fault(Exception exception)
    {
        _faultException = exception;
        _unrecoverable = true;
        _faulted = true; // volatile write publishes _faultException
        if (_logger.IsError) _logger.Error("Deferred block-data consumer stopped unexpectedly; falling back to synchronous persistence.", exception);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is null) return;

        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }
        else
        {
            Pump();
        }
    }

    private sealed class ActionOperation(Action action) : IDeferredWriteOperation
    {
        public void Execute() => action();
    }
}
