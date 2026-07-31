// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// A counter spread over per-cache-line slots, for counters incremented once per unit of work by many
/// threads at once.
/// </summary>
/// <remarks>
/// A single shared counter turns a hot loop into a serialized one: an atomic increment on a line another
/// core owns costs tens of nanoseconds, so N threads incrementing once per item cap the loop's throughput
/// regardless of how much parallel width it is given. Striping by thread keeps each increment on a line
/// that is almost always locally owned, which restores the parallelism without giving up exactness —
/// increments stay atomic, because two threads can still hash to the same slot.
/// <para>
/// Slot count is a fixed power of two rather than a function of <see cref="System.Environment.ProcessorCount"/>
/// so the index is a mask instead of a division; at 128 bytes per slot the whole array is small enough that
/// over-provisioning costs nothing measurable.
/// </para>
/// </remarks>
public static class StripedCounter
{
    private const int SlotCount = 64;
    private const int SlotMask = SlotCount - 1;

    public static CacheLinePaddedLong[] Create() => new CacheLinePaddedLong[SlotCount];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Increment(CacheLinePaddedLong[] slots) =>
        Interlocked.Increment(ref slots[Environment.CurrentManagedThreadId & SlotMask].Value);

    public static long Sum(CacheLinePaddedLong[] slots)
    {
        long total = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            total += Volatile.Read(ref slots[i].Value);
        }

        return total;
    }
}
