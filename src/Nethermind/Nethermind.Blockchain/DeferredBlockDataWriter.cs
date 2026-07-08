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
/// Durability is deferred, visibility is not: callers publish an in-memory overlay synchronously
/// before enqueueing, so reads never observe missing data. The single FIFO queue is load-bearing -
/// per block the body write must precede the receipts write because compact-receipt sender recovery
/// reads the body database directly. Consistency between a queued write and a synchronous delete of
/// the same block is the caller's responsibility (the enqueued work and the delete take a shared
/// lock and re-check the overlay), so this type is a plain ordered executor with no per-key state.
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
    // Set (before _faulted, whose volatile write publishes it) on a hard write failure or an unexpected
    // consumer exit. Drain rethrows it so state persistence aborts rather than proceeding without the data.
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

            // Bounded-queue backpressure: intentionally blocks the producing (block-processing) thread
            // so a slow disk degrades to synchronous behaviour instead of unbounded memory growth.
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

        // Fence: enqueue a completion marker and block until the single FIFO consumer reaches it, which
        // proves every earlier queued write has executed. Wait on the marker OR consumer termination, so a
        // consumer that ever stops without draining the marker aborts here instead of freezing this thread.
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(drained.SetResult);
        Task.WhenAny(drained.Task, _consumer!).GetAwaiter().GetResult();

        // A hard write failure - or a consumer that stopped before running the marker - means a block's
        // data may not be durable; abort the state persist rather than fsyncing and letting state pass it.
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
            // The consumer must never exit silently: fault so Enqueue/Drain fall back inline and surface it,
            // rather than leaving a queued Drain sentinel that would never be signalled (a hang).
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
                // Hard persistence failure: keep the overlay published (reads stay correct) and run all
                // future work inline so the failure surfaces on the producer as the synchronous path would;
                // Drain rethrows it so a state persist cannot proceed past the lost write.
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
