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

    public static long GetCurrentTick()
    {
        return Stopwatch.GetTimestamp();
    }

    private static double TickToMs(long tick)
    {
        return tick * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Return true if its definitely will be throttled when calling WaitAsync. May still get throttled even if this
    /// return false.
    /// </summary>
    /// <returns></returns>
    public bool IsThrottled()
    {
        return GetCurrentTick() < _nextSlot;
    }

    public Task WaitAsync(CancellationToken ctx)
    {
        long currentNextSlot = _nextSlot;
        while (true)
        {
            long nextSlot = Interlocked.CompareExchange(ref _nextSlot, currentNextSlot + _delay, currentNextSlot);
            if (nextSlot == currentNextSlot)
            {
                break;
            }
            currentNextSlot = nextSlot;
        }

        long now = GetCurrentTick();
        long toWait = currentNextSlot - now;

        if (toWait <= 0) return Task.CompletedTask;

        return Task.Delay(TimeSpan.FromMilliseconds(TickToMs(toWait)), ctx);
    }
}
