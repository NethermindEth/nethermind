// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// A monotonically increasing counter sharded across cache-line-isolated stripes to avoid the cross-core
/// cache-line contention of a single shared <see cref="Interlocked"/> target under many concurrent writers.
/// </summary>
/// <remarks>
/// Reads (<see cref="Value"/>) sum all stripes and are eventually consistent — intended for diagnostic
/// counters (cache hit/read tallies) hit on hot multi-threaded paths (e.g. the block pre-warmer's reads),
/// not for values requiring an exact instantaneous total. Each stripe sits on its own cache line.
/// </remarks>
public sealed class StripedLongCounter
{
    private readonly CacheLinePaddedLong[] _stripes;
    private readonly int _mask;

    public StripedLongCounter()
    {
        // One stripe per logical core (rounded up to a power of two) spreads writers across distinct
        // cache lines; threads hashing to the same stripe still contend, but ~ProcessorCount× less.
        int stripeCount = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, Environment.ProcessorCount));
        _stripes = new CacheLinePaddedLong[stripeCount];
        _mask = stripeCount - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment()
    {
        int idx = Environment.CurrentManagedThreadId & _mask;
        Interlocked.Increment(ref _stripes[idx].Value);
    }

    public long Value
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < _stripes.Length; i++)
            {
                sum += Volatile.Read(ref _stripes[i].Value);
            }
            return sum;
        }
    }
}
