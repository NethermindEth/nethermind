// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Caching;

/// <summary>
/// Shared seqlock header constants and helpers for set-associative caches.
///
/// Header layout (64-bit entry header):
/// [Lock:1][Epoch:26][Hash:20][Seq:16][Occ:1]
///
/// EpochAndCount layout (64-bit combined field):
/// [0:1][Epoch:26][Count:37]
/// Epoch occupies the same bit positions (37-62) as in the entry header,
/// so (epochAndCount &amp; EpochMask) can be used directly for header comparison.
/// Count occupies bits 0-36 (max ~137 billion). Atomic CAS on Clear() bumps
/// epoch and resets count in a single operation — no race window.
/// </summary>
internal static class SeqlockHeader
{
    public const long LockMarker = unchecked((long)0x8000_0000_0000_0000); // bit 63

    public const int EpochShift = 37;
    public const long EpochMask = 0x7FFF_FFE0_0000_0000;  // bits 37-62 (26 bits)

    public const long HashMask = 0x0000_001F_FFFE_0000;   // bits 17-36 (20 bits)

    public const long SeqMask = 0x0000_0000_0001_FFFE;    // bits 1-16 (16 bits)
    public const long SeqInc = 0x0000_0000_0000_0002;     // +1 in seq field

    public const long OccupiedBit = 1L;                    // bit 0

    /// <summary>All identity bits: epoch + hash + occupied. Excludes Lock and Seq.</summary>
    public const long TagMask = EpochMask | HashMask | OccupiedBit;

    /// <summary>Epoch + occupied, for checking if an entry is live in the current epoch.</summary>
    public const long EpochOccMask = EpochMask | OccupiedBit;

    /// <summary>Mask for the count portion of the combined epoch+count field (bits 0-36).</summary>
    public const long CountMask = (1L << EpochShift) - 1;  // 0x0000_001F_FFFF_FFFF

    /// <summary>
    /// Maximum supported capacity. Leaves 10 bits (1024×) of underflow headroom
    /// between a realistic max count and the epoch bits at position 37, so transient
    /// count underflows from bugs cannot corrupt the epoch.
    /// </summary>
    public const int MaxCapacity = 1 << 27; // 134,217,728

    /// <summary>
    /// Atomically bumps the epoch and resets count to zero in one CAS.
    /// No race window between epoch change and count reset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearEpochAndCount(ref long epochAndCount)
    {
        long old = Volatile.Read(ref epochAndCount);

        while (true)
        {
            long oldEpoch = (old & EpochMask) >> EpochShift;
            long newEpoch = oldEpoch + 1;
            long newVal = (newEpoch << EpochShift) & EpochMask; // count = 0

            long prev = Interlocked.CompareExchange(ref epochAndCount, newVal, old);
            if (prev == old) return;

            old = prev;
        }
    }

    /// <summary>
    /// Reads the epoch tag from the combined field (bits 37-62, same positions as entry header).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadEpoch(ref long epochAndCount)
    {
        return Volatile.Read(ref epochAndCount) & EpochMask;
    }

    /// <summary>
    /// Reads the count from the combined field (bits 0-36).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadCount(ref long epochAndCount)
    {
        return (int)(Volatile.Read(ref epochAndCount) & CountMask);
    }

    /// <summary>
    /// Validates capacity is within the supported range for the combined epoch+count field.
    /// </summary>
    public static void ThrowIfCapacityTooLarge(int maxCapacity)
    {
        if (maxCapacity > MaxCapacity)
        {
            ThrowCapacityTooLarge(maxCapacity);
        }

        static void ThrowCapacityTooLarge(int maxCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCapacity),
                maxCapacity,
                $"Capacity exceeds maximum supported value of {MaxCapacity}. " +
                $"The combined epoch+count field requires headroom to prevent count underflow from corrupting the epoch.");
        }
    }

    /// <summary>
    /// TTAS (test-and-test-and-set) spinlock acquire on a per-set gate.
    /// Spins on a plain read first to avoid bus-locked CAS traffic under contention.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AcquireGate(ref int gate)
    {
        SpinWait sw = default;
        while (Volatile.Read(ref gate) != 0 || Interlocked.CompareExchange(ref gate, 1, 0) != 0)
        {
            sw.SpinOnce();
        }
    }

    /// <summary>
    /// Release a per-set gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReleaseGate(ref int gate)
    {
        Volatile.Write(ref gate, 0);
    }

    /// <summary>
    /// Pick 3 distinct indices from [0, 8), return the one whose ticker is smallest (oldest).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Pick3RandomEvict(long tickerA, long tickerB, long tickerC, int a, int b, int c)
    {
        if (tickerA <= tickerB && tickerA <= tickerC) return a;
        if (tickerB <= tickerC) return b;
        return c;
    }

    /// <summary>
    /// Generate 3 distinct indices in [0, 8) from a timestamp, with xorshift mixing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int a, int b, int c) Pick3Indices(long now)
    {
        uint r = (uint)now;
        r ^= r >> 13;
        r ^= r << 17;
        r ^= r >> 5;

        int a = (int)(r & 0x7);
        int b = (int)((r >> 3) & 0x7);
        int c = (int)((r >> 6) & 0x7);

        if (b == a) b = (a + 1) & 0x7;
        if (c == a) c = (a + 2) & 0x7;
        if (c == b) c = (b + 1) & 0x7;
        if (c == a) c = (a + 3) & 0x7;

        return (a, b, c);
    }
}
