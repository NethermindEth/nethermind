// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core;

/// <summary>
/// Simple rate limiter that limits rate of event, by delaying the caller so that a minimum amount of time elapsed
/// between event.
/// </summary>
public class RateLimiter
{
    private readonly long _delay;
    private long _nextSlot;

    public RateLimiter(int eventPerSec) : this(1.0 / eventPerSec)
    {
    }

    private RateLimiter(double intervalSec)
    {
        _delay = (long)(Stopwatch.Frequency * intervalSec);

        _nextSlot = GetCurrentTick();
    }

    public static long GetCurrentTick() => Stopwatch.GetTimestamp();

    private static double TickToMs(long tick) => tick * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Return true if its definitely will be throttled when calling WaitAsync. May still get throttled even if this
    /// return false.
    /// </summary>
    /// <returns></returns>
    public bool IsThrottled() => GetCurrentTick() < _nextSlot;

    /// <summary>
    /// How long a caller of <see cref="WaitAsync"/> would be delayed right now.
    /// </summary>
    public TimeSpan Backlog
    {
        get
        {
            long toWait = _nextSlot - GetCurrentTick();
            return toWait <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(TickToMs(toWait));
        }
    }

    public Task WaitAsync(CancellationToken ctx)
    {
        long now = GetCurrentTick();
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

        return Task.Delay(TimeSpan.FromMilliseconds(TickToMs(toWait)), ctx);
    }
}
