// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    /// Blocks until every work item queued before this call has executed. The state persistence barrier
    /// calls this before fsyncing, so a block's deferred writes are on disk before its state is persisted.
    /// </summary>
    void Drain();
}

/// <inheritdoc cref="IDeferredBlockDataWriter"/>
public sealed class DeferredBlockDataWriter : IDeferredBlockDataWriter
{
    private readonly Channel<Action>? _channel;
    private readonly Task? _consumer;
    private readonly bool _manualPump;
    private readonly ILogger _logger;
    // Set before _faulted (whose volatile write publishes it) on a hard write failure; Drain rethrows it.
    private Exception? _faultException;
    private volatile bool _faulted;

    /// <param name="enabled">When false the writer is a no-op passthrough that runs work inline.</param>
    /// <param name="capacity">Maximum queued work items before producers backpressure. Note this
    /// counts individual writes, not blocks: a block can enqueue a body, a receipts and a canonical
    /// item, so the block headroom is roughly a third of this value.</param>
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
            _channel = Channel.CreateBounded<Action>(new BoundedChannelOptions(Math.Max(1, capacity))
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

    public void Enqueue(Action work)
    {
        if (_channel is null || _faulted)
        {
            work();
            return;
        }

        while (!_channel.Writer.TryWrite(work))
        {
            if (_faulted || _channel.Reader.Completion.IsCompleted)
            {
                work();
                return;
            }

            // Backpressure: block the producer so a slow disk degrades to synchronous, not unbounded memory.
            if (!_channel.Writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
            {
                work();
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
        while (_channel is not null && _channel.Reader.TryRead(out Action? work))
        {
            Run(work);
        }
    }

    public void Drain()
    {
        if (_channel is null) return;          // disabled: work ran inline, nothing is queued
        if (_manualPump) { Pump(); ThrowIfFaulted(); return; }   // no background consumer (tests)

        // Fence: block on a queued marker (all earlier writes ran) OR consumer termination, so a consumer
        // that stops without reaching the marker aborts here instead of freezing this thread.
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(drained.SetResult);
        Task.WhenAny(drained.Task, _consumer!).GetAwaiter().GetResult();

        // Marker not reached: a write may be lost or the consumer died; abort the persist rather than fsync.
        if (!drained.Task.IsCompleted)
        {
            ThrowIfFaulted();
            throw new InvalidOperationException("Deferred writer consumer stopped before the drain completed.");
        }

        ThrowIfFaulted();
    }

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException("Deferred block-data write failed; aborting state persistence.", _faultException);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (Action work in _channel!.Reader.ReadAllAsync().ConfigureAwait(false))
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

    private void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception firstException)
        {
            if (_logger.IsWarn) _logger.Warn($"Deferred block-data write failed, retrying once: {firstException}");
            try
            {
                work();
            }
            catch (Exception secondException)
            {
                // Hard failure: fault so future work runs inline (reads stay correct via the overlay) and
                // Drain rethrows, blocking a state persist past the lost write.
                Fault(secondException);
            }
        }
    }

    private void Fault(Exception exception)
    {
        _faultException = exception;
        _faulted = true; // volatile write publishes _faultException
        if (_logger.IsError) _logger.Error("Deferred block-data write failed; falling back to synchronous persistence.", exception);
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
}
