// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Channels;
using System.Threading.Tasks;
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
}

/// <inheritdoc cref="IDeferredBlockDataWriter"/>
public sealed class DeferredBlockDataWriter : IDeferredBlockDataWriter
{
    private readonly Channel<Action>? _channel;
    private readonly Task? _consumer;
    private readonly bool _manualPump;
    private readonly ILogger _logger;
    private volatile bool _faulted;

    /// <param name="enabled">When false the writer is a no-op passthrough that runs work inline.</param>
    /// <param name="capacity">Maximum queued work items before producers backpressure. Note this
    /// counts individual writes, not blocks: a block can enqueue a body, a receipts and a canonical
    /// item, so the block headroom is roughly a third of this value.</param>
    /// <param name="logManager">Log manager.</param>
    /// <param name="startConsumer">When false no background consumer runs and <see cref="Pump"/>
    /// must be driven manually. For tests only.</param>
    public DeferredBlockDataWriter(bool enabled, int capacity, ILogManager logManager, bool startConsumer = true)
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

    private async Task RunAsync()
    {
        await foreach (Action work in _channel!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Run(work);
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
                // A hard persistence failure (disk full/corrupt). The overlay entry stays published so
                // live reads remain correct; durability is now compromised until restart recovery. All
                // future work runs inline so the failure surfaces on the producer exactly as the
                // synchronous path does today, and already-queued items continue draining in FIFO order.
                _faulted = true;
                if (_logger.IsError) _logger.Error("Deferred block-data write failed after retry; falling back to synchronous persistence.", secondException);
            }
        }
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
