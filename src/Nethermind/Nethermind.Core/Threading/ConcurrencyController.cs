// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// Encapsulate the pattern of checking if new task can be spawned based on a predefined limit.
/// Used in multithreaded tree visit where we don't know if we can spawn task or not and spawning task itself
/// is not a cheap operation.
///
/// Yes, I don't like the name. Give me a good one.
/// </summary>
/// <param name="concurrency">Desired concurrency which include the calling thread. So slot is slot-1.</param>
public class ConcurrencyController(int concurrency)
{
    private int _slots = concurrency;

    public bool TryTakeSlot(out Slot returner)
    {
        returner = new Slot(this);
        int newSlot = Volatile.Read(ref _slots);
        if (newSlot < 2)
        {
            return false;
        }

        newSlot = Interlocked.Decrement(ref _slots);
        if (newSlot < 1)
        {
            Interlocked.Increment(ref _slots);
            return false;
        }

        return true;
    }

    private void ReturnSlot()
    {
        Interlocked.Increment(ref _slots);
    }

    public readonly struct Slot(ConcurrencyController limiter) : IDisposable
    {
        public void Dispose()
        {
            limiter.ReturnSlot();
        }
    }
}
