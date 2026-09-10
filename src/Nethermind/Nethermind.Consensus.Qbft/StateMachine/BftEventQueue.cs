// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>
/// Bounded, single-consumer queue of consensus events.
/// </summary>
/// <remarks>
/// Until <see cref="Start"/> is called only block timer expiries are accepted, so messages that
/// arrive while the node is still syncing are not replayed against a stale chain head. Events beyond
/// <paramref name="messageQueueLimit"/> are dropped with a warning rather than blocking the network thread.
/// </remarks>
public sealed class BftEventQueue(int messageQueueLimit, ILogManager logManager) : IBftEventQueue
{
    private readonly Channel<BftEvent> _queue = Channel.CreateUnbounded<BftEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ILogger _logger = logManager.GetClassLogger<BftEventQueue>();
    private volatile bool _started;
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Start() => _started = true;

    public void Stop() => _started = false;

    public void Add(BftEvent bftEvent)
    {
        if (!_started && bftEvent is not BlockTimerExpiryEvent)
        {
            return;
        }

        if (Count > messageQueueLimit)
        {
            if (_logger.IsWarn) _logger.Warn($"Queue size exceeded trying to add new bft event {bftEvent}");
            return;
        }

        Interlocked.Increment(ref _count);
        _queue.Writer.TryWrite(bftEvent);
    }

    /// <summary>
    /// Blocks the calling thread until the next event arrives; returns null when
    /// <paramref name="cancellationToken"/> is cancelled or the queue is completed.
    /// </summary>
    /// <remarks>
    /// The consensus loop owns a dedicated thread and creates blocks synchronously, so it blocks here
    /// rather than awaiting and continuing on a pool thread that a proposal build would then occupy.
    /// </remarks>
    public BftEvent? Read(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (TryRead(out BftEvent? bftEvent))
                {
                    return bftEvent;
                }

                if (!_queue.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
                {
                    return null;
                }
            }
        }
        catch (System.OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Waits for the next event; returns null when <paramref name="cancellationToken"/> is cancelled.</summary>
    public async ValueTask<BftEvent?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            BftEvent bftEvent = await _queue.Reader.ReadAsync(cancellationToken);
            Interlocked.Decrement(ref _count);
            return bftEvent;
        }
        catch (System.OperationCanceledException)
        {
            return null;
        }
    }

    public bool TryRead(out BftEvent? bftEvent)
    {
        if (_queue.Reader.TryRead(out bftEvent))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }

        return false;
    }
}
