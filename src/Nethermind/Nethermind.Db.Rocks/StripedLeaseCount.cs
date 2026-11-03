// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Threading;

namespace Nethermind.Db.Rocks;

/// <summary>
/// Counts leases taken and returned on hot concurrent paths, with one slot per core so concurrent readers do not
/// contend on a shared counter word. Slots use the same 128-byte spacing as <see cref="StripedLong"/>.
/// </summary>
/// <remarks>
/// <see cref="Count"/> is not a snapshot: it reads the slots one by one. It follows three rules:
/// <list type="number">
/// <item><description><see cref="Count"/> reads every increment made before a full fence on the reading thread, for
/// example an interlocked write of a closed flag.</description></item>
/// <item><description>A lease taken and returned on one call path goes back to the slot <see cref="Acquire"/> gave, so
/// <see cref="Count"/> never reads a lone decrement.</description></item>
/// <item><description>A lease returned through <see cref="CurrentSlot"/> must have been taken before that fence;
/// otherwise its decrement can cancel a lease that is still held.</description></item>
/// </list>
/// <see cref="StripedLong"/> picks the slot again on each update and is for metrics only, so it cannot keep these rules.
/// </remarks>
internal sealed class StripedLeaseCount
{
    // 16 longs = 128 bytes between live slots, with a leading pad stride.
    private const int SlotStride = 16;
    private static readonly int s_stripeMask = (int)BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount) - 1;

    private readonly long[] _slots = new long[(s_stripeMask + 2) * SlotStride];

    /// <summary>The slot of the core that runs the caller.</summary>
    public static int CurrentSlot
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Thread.GetCurrentProcessorId() & s_stripeMask) + 1) * SlotStride;
    }

    /// <summary>Counts a lease and returns the slot it used.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Acquire()
    {
        int slot = CurrentSlot;
        Interlocked.Increment(ref _slots[slot]);
        return slot;
    }

    /// <summary>Returns a lease to <paramref name="slot"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release(int slot) => Interlocked.Decrement(ref _slots[slot]);

    /// <summary>The leases taken and not returned, read slot by slot.</summary>
    public long Count
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
