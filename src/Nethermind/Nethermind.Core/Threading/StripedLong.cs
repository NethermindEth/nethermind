// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// Additive counter safe for hot concurrent paths: increments land on a per-core slot, reads sum
/// the slots.
/// </summary>
/// <remarks>
/// A shared counter word turns every increment into a serialized cross-core cache-line transfer
/// once several threads hit it (RPC workers, prewarm workers). Striping by
/// <see cref="Thread.GetCurrentProcessorId"/> keeps the RMW local to the core in the common case;
/// the atomic add only guards against threads that share or migrate between cores. Slots are
/// 128-byte spaced — same isolation as <see cref="CacheLinePaddedLong"/> (adjacent-line prefetch
/// pairs lines). Reads are O(stripes) and torn only across slots: fine for metrics, not for
/// invariants.
/// </remarks>
public sealed class StripedLong
{
    // 16 longs = 128 bytes between live slots.
    private const int SlotStride = 16;
    private static readonly int s_stripeMask = (int)BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount) - 1;

    private readonly long[] _slots = new long[(s_stripeMask + 1) * SlotStride];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(long value)
        => Interlocked.Add(ref _slots[(Thread.GetCurrentProcessorId() & s_stripeMask) * SlotStride], value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment() => Add(1);

    public long Sum
    {
        get
        {
            long[] slots = _slots;
            long sum = 0;
            for (int i = 0; i < slots.Length; i += SlotStride)
            {
                sum += Volatile.Read(ref slots[i]);
            }
            return sum;
        }
    }
}
