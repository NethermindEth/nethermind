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
/// [Lock:1][Epoch:16][Hash:20][Seq:26][Occ:1]
///
/// EpochAndCount layout (64-bit combined field):
/// [0:1][Epoch:16][Count:47]
/// Epoch occupies the same bit positions (47-62) as in the entry header,
/// so (epochAndCount &amp; EpochMask) can be used directly for header comparison.
/// Count occupies bits 0-46. Atomic CAS on Clear() bumps
/// epoch and resets count in a single operation — no race window.
///
/// The sequence counter has 26 bits, so a reader that stops between its two header loads sees the same header again
/// only after 2^26 writes to that entry. The epoch has 16 bits and wraps after 65,536 clears. So that an entry from
/// an epoch that wrapped can never look current, every <see cref="SweepPeriod"/>-th clear wipes all entries of older
/// epochs (see <see cref="IsSweepEpoch"/>); no entry then outlives half of the epoch range.
/// </summary>
internal static class SeqlockHeader
{
    public const long LockMarker = unchecked((long)0x8000_0000_0000_0000); // bit 63

    public const int EpochShift = 47;
    public const long EpochMask = 0x7FFF_8000_0000_0000;  // bits 47-62 (16 bits)

    public const long HashMask = 0x0000_7FFF_F800_0000;   // bits 27-46 (20 bits)
    public const int HashFieldShift = 27;                  // lowest bit of HashMask
    public const long HashRawMask = HashMask >> HashFieldShift; // 20-bit mask at position 0

    public const long SeqMask = 0x0000_0000_07FF_FFFE;    // bits 1-26 (26 bits)
    public const long SeqInc = 0x0000_0000_0000_0002;     // +1 in seq field

    public const long OccupiedBit = 1L;                    // bit 0

    /// <summary>All identity bits: epoch + hash + occupied. Excludes Lock and Seq.</summary>
    public const long TagMask = EpochMask | HashMask | OccupiedBit;

    /// <summary>Epoch + occupied, for checking if an entry is live in the current epoch.</summary>
    public const long EpochOccMask = EpochMask | OccupiedBit;

    /// <summary>
    /// Extract the 20-bit hash signature from the hash code, positioned into header bits 17-36.
    /// Uses dynamic shift to read bits immediately above the set index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ExtractHashPart(long hashCode, int hashShift) =>
        ((hashCode >> hashShift) & HashRawMask) << HashFieldShift;

    /// <summary>Mask for the count portion of the combined epoch+count field (bits 0-46).</summary>
    public const long CountMask = (1L << EpochShift) - 1;  // 0x0000_7FFF_FFFF_FFFF

    /// <summary>Clears between two sweeps of older-epoch entries: a quarter of the 16-bit epoch range.</summary>
    public const long SweepPeriod = 1L << 14;

    private const long SweepEpochMask = (SweepPeriod - 1) << EpochShift;

    /// <summary>
    /// True for every <see cref="SweepPeriod"/>-th epoch. The clear that publishes it must wipe all entries of older
    /// epochs, also when the caller did not ask to release references, so that no entry survives an epoch wrap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSweepEpoch(long epoch) => (epoch & SweepEpochMask) == 0;

    /// <summary>
    /// Maximum supported capacity. Leaves 20 bits of overflow headroom
    /// between a realistic max count and the count field limit at bit 46, keeping
    /// normal counts well away from the epoch boundary at bit 47.
    /// </summary>
    public const uint MaxCapacity = 1u << 27; // 134,217,728

    /// <summary>
    /// Atomically bumps the epoch and resets count to zero in one CAS.
    /// Returns the new epoch tag (bits 47-62, same positions as entry headers).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ClearEpochAndCount(ref long epochAndCount)
    {
        long oldValue = Volatile.Read(ref epochAndCount);

        while (true)
        {
            long newValue = (oldValue + (1L << EpochShift)) & EpochMask;

            long prev = Interlocked.CompareExchange(ref epochAndCount, newValue, oldValue);
            if (prev == oldValue) return newValue;

            oldValue = prev;
        }
    }

    /// <summary>
    /// Reads the epoch tag from the combined field (bits 47-62, same positions as entry header).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadEpoch(ref long epochAndCount) => Volatile.Read(ref epochAndCount) & EpochMask;

    /// <summary>Half of the epoch range, in EpochMask bit positions (2^15 &lt;&lt; EpochShift).</summary>
    private const long EpochHalfRangeShifted = 1L << 62;

    /// <summary>
    /// True if <paramref name="headerEpoch"/> is strictly older than <paramref name="currentEpoch"/>,
    /// comparing modularly over the 16-bit epoch field. Used to distinguish stale slots from
    /// future-epoch slots a concurrent <see cref="ClearEpochAndCount"/> has published mid-walk.
    /// </summary>
    /// <param name="headerEpoch">Epoch bits as stored in a header — i.e. <c>h &amp; EpochMask</c>.</param>
    /// <param name="currentEpoch">Epoch tag as returned by <see cref="ClearEpochAndCount"/> or <see cref="ReadEpoch"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEpochOlderThan(long headerEpoch, long currentEpoch)
    {
        long diff = (currentEpoch - headerEpoch) & EpochMask;
        return diff != 0 && diff <= EpochHalfRangeShifted;
    }

    /// <summary>
    /// Reads the count from the combined field (bits 0-46).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadCount(ref long epochAndCount) => (int)(Volatile.Read(ref epochAndCount) & CountMask);

    /// <summary>
    /// Adjusts the count portion of epochAndCount by delta, but only if the epoch
    /// still matches expectedEpoch. Prevents count drift when Clear() races with Set/Delete.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AdjustCountIfEpoch(ref long epochAndCount, long expectedEpoch, long delta)
    {
        long current = Volatile.Read(ref epochAndCount);

        while ((current & EpochMask) == expectedEpoch)
        {
            long newCount = Math.Clamp((current & CountMask) + delta, 0, CountMask);
            long updated = (current & EpochMask) | newCount;

            long prev = Interlocked.CompareExchange(ref epochAndCount, updated, current);
            if (prev == current)
                return;

            current = prev;
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
    public static void ReleaseGate(ref int gate) => Volatile.Write(ref gate, 0);

    /// <summary>
    /// Pick 3 distinct indices from [0, 8), return the one whose ticker is smallest (oldest).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Pick3RandomEvict(long tickerA, long tickerB, long tickerC, int a, int b, int c) =>
        tickerA <= tickerB
            ? tickerA <= tickerC ? a : c
            : tickerB <= tickerC ? b : c;

    /// <summary>
    /// Generate 3 distinct indices in [0, 8) from a seed, with multiplicative and xorshift mixing.
    /// </summary>
    /// <remarks>
    /// Seeds are recency stamps plus a thread probe, which on a coarse clock step by little between evictions; the xorshift
    /// alone leaves such seeds sampling some ways far more than others, so the golden-ratio multiply spreads them first.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int a, int b, int c) Pick3Indices(long seed)
    {
        uint r = (uint)(((ulong)seed * 0x9E3779B97F4A7C15UL) >> 32);
        r ^= r >> 13;
        r ^= r << 17;
        r ^= r >> 5;

        int a = (int)(r & 0x7);
        int b = (int)((r >> 3) & 0x7);
        int c = (int)((r >> 6) & 0x7);

        // Ensure a, b, c are distinct. Each fixup shifts by a different offset so
        // the final fallback (c = a+3) cannot equal a (offset 0) or b (offset 1).
        if (b == a) b = (a + 1) & 0x7;
        if (c == a) c = (a + 2) & 0x7;
        if (c == b) c = (b + 1) & 0x7;
        if (c == a) c = (a + 3) & 0x7;

        return (a, b, c);
    }
}
