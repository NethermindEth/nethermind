// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Caching;

/// <summary>
/// Shared seqlock header constants and helpers for set-associative caches.
///
/// Header layout (64-bit):
/// [Lock:1][Epoch:26][Hash:20][Seq:16][Occ:1]
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

    /// <summary>Shift applied to the 64-bit hash to extract the 20-bit signature stored in the header.</summary>
    public const int HashShift = 5;

    /// <summary>
    /// O(1) epoch bump — all entries with old epoch are treated as empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BumpEpoch(ref long shiftedEpoch)
    {
        long oldShifted = Volatile.Read(ref shiftedEpoch);

        while (true)
        {
            long oldEpoch = (oldShifted & EpochMask) >> EpochShift;
            long newEpoch = oldEpoch + 1;
            long newShifted = (newEpoch << EpochShift) & EpochMask;

            long prev = Interlocked.CompareExchange(ref shiftedEpoch, newShifted, oldShifted);
            if (prev == oldShifted) return;

            oldShifted = prev;
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
    /// Uses xorshift mixing on the timestamp for better entropy than raw TSC low bits.
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
