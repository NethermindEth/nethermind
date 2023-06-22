// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;

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

    public async ValueTask WaitAsync(CancellationToken ctx)
    {
        while (true)
        {
            long originalNextSlot = _nextSlot;

            // Technically its possible that two `GetCurrentTick()` call at the same time can return same value,
            // but its very unlikely.
            long now = GetCurrentTick();
            if (now >= originalNextSlot
                && Interlocked.CompareExchange(ref _nextSlot, now + _delay, originalNextSlot) == originalNextSlot)
            {
                return;
            }

            long toWait = _nextSlot - now;
            if (toWait < 0) continue;

            await Task.Delay(TimeSpan.FromMilliseconds(TickToMs(toWait)), ctx);
        }
    }
}
