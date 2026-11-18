// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core;

/// <summary>
/// Simple rate limiter that limits rate of event, by delaying the caller so that a minimum amount of time elapsed
/// between event.
/// </summary>
public class RateLimiter
{
    private readonly TimeProvider _timeProvider;
    private readonly long _delay;
    private long _nextSlot;

    public RateLimiter(int eventPerSec) : this((double)eventPerSec)
    {
    }

    /// <param name="eventsPerSecond">The rate, which can be below one event per second.</param>
    /// <param name="timeProvider">The clock and timers, <see cref="TimeProvider.System"/> when null.</param>
    public RateLimiter(double eventsPerSecond, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventsPerSecond);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = (long)(_timeProvider.TimestampFrequency / eventsPerSecond);

        _nextSlot = _timeProvider.GetTimestamp();
    }

    private long Now => _timeProvider.GetTimestamp();

    private double TickToMs(long tick) => tick * 1000.0 / _timeProvider.TimestampFrequency;

    /// <summary>
    /// Return true if its definitely will be throttled when calling WaitAsync. May still get throttled even if this
    /// return false.
    /// </summary>
    /// <returns></returns>
    public bool IsThrottled() => Now < _nextSlot;

    /// <summary>
    /// How long a caller of <see cref="WaitAsync"/> would be delayed right now.
    /// </summary>
    public TimeSpan Backlog
    {
        get
        {
            long toWait = _nextSlot - Now;
            return toWait <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(TickToMs(toWait));
        }
    }

    /// <summary>
    /// Reserves an event within the permitted delay, or refuses without consuming a slot.
    /// </summary>
    /// <remarks>Abandoned reservations are not refunded; debt is bounded by the maximum delay plus one event interval.</remarks>
    public bool TryReserve(TimeSpan maximumDelay, out TimeSpan delay) => TryReserve(Now, maximumDelay, out delay);

    internal bool TryReserve(long now, TimeSpan maximumDelay, out TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, TimeSpan.Zero);
        double maximumTicks = maximumDelay.TotalSeconds * _timeProvider.TimestampFrequency;
        long currentNextSlot = Volatile.Read(ref _nextSlot);
        while (true)
        {
            long slot = Math.Max(now, currentNextSlot);
            if (slot - now > maximumTicks)
            {
                delay = default;
                return false;
            }

            long observed = Interlocked.CompareExchange(ref _nextSlot, slot + _delay, currentNextSlot);
            if (observed == currentNextSlot)
            {
                delay = TimeSpan.FromMilliseconds(TickToMs(slot - now));
                return true;
            }
            currentNextSlot = observed;
        }
    }

    public Task WaitAsync(CancellationToken ctx)
    {
        long now = Now;
        long currentNextSlot = _nextSlot;
        while (true)
        {
            // Slots are never taken from the past: an idle period must not bank a burst.
            long mySlot = Math.Max(currentNextSlot, now);
            long nextSlot = Interlocked.CompareExchange(ref _nextSlot, mySlot + _delay, currentNextSlot);
            if (nextSlot == currentNextSlot)
            {
                currentNextSlot = mySlot;
                break;
            }
            currentNextSlot = nextSlot;
        }

        long toWait = currentNextSlot - now;

        if (toWait <= 0) return Task.CompletedTask;

        return Task.Delay(TimeSpan.FromMilliseconds(TickToMs(toWait)), _timeProvider, ctx);
    }
}
