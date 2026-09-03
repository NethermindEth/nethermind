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
/// pairs lines) — with a leading pad stride so the first slot is also isolated from whatever
/// precedes the array in memory. Each instance allocates (stripes + 1) * 128 bytes, where stripes
/// is <see cref="Environment.ProcessorCount"/> rounded up to a power of two, captured once at
/// type initialization (later CPU hot-add is not tracked; the mask just folds new ids onto
/// existing slots). Reads are O(stripes) and torn only across slots: fine for metrics, not for
/// invariants.
/// </remarks>
public sealed partial class StripedLong
{
    // 16 longs = 128 bytes between live slots.
    private const int SlotStride = 16;
    private static readonly int s_stripeMask = (int)BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount) - 1;

    private readonly long[] _slots = new long[(s_stripeMask + 2) * SlotStride];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(long value)
        => Interlocked.Add(ref _slots[((Thread.GetCurrentProcessorId() & s_stripeMask) + 1) * SlotStride], value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment() => Add(1);

    public long Sum
    {
        get
        {
            long[] slots = _slots;
            long sum = 0;
            for (int stripe = 0; stripe <= s_stripeMask; stripe++)
            {
                sum += Volatile.Read(ref slots[(stripe + 1) * SlotStride]);
            }
            return sum;
        }
    }
}
