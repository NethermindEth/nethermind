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
/// <see cref="Count"/> is not a snapshot: it reads the slots one by one. It never reads lower than the leases still
/// held when each of them was taken before the full fence that precedes the read (for example, before the owner set a
/// closed flag with an interlocked write), because the read then sees every such increment. A lease taken and
/// returned while the slots are read must not add a lone decrement, so <see cref="Acquire"/> returns its slot for
/// <see cref="Release"/> on the same call path. A lease kept past its call can be returned from
/// <see cref="CurrentSlot"/>.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release(int slot) => Interlocked.Decrement(ref _slots[slot]);

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
