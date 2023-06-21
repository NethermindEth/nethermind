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
    private TimeSpan _delay;
    private DateTimeOffset _nextSlot;
    private SpinLock _spinLock = new();

    public RateLimiter(int eventPerSec) : this(1.0 / eventPerSec)
    {
    }

    private RateLimiter(double intervalSec)
    {
        _delay = TimeSpan.FromSeconds(intervalSec);
        _nextSlot = DateTimeOffset.Now;
    }

    public async Task WaitAsync(CancellationToken ctx)
    {
        while (true)
        {
            TimeSpan toWait;
            bool taken = false;
            while (!taken)
            {
                _spinLock.Enter(ref taken);
            }
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                if (now >= _nextSlot)
                {
                    _nextSlot = now + _delay;
                    return;
                }

                toWait = _nextSlot - now;
            }
            finally
            {
                _spinLock.Exit();
            }

            if (toWait < TimeSpan.Zero) continue;

            await Task.Delay(toWait, ctx);
        }
    }
}
