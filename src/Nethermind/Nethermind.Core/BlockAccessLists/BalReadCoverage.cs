// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Numerics;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-worker proof that the suggested BAL's declared storage reads were all executed, replacing
/// the materialized generated read set. Two lanes over the dense ordinal space of
/// <see cref="BalReadStoragePlan"/>: a structural coverage bitmap (every declared read, including
/// system-contract reads) and a per-slice chargeable count (non-system reads, distinct within a
/// slice, summed across slices) for the EIP-7928 read budget.
/// </summary>
/// <remarks>
/// Each parallel worker owns one instance and marks it without synchronization on the SLOAD hot
/// path: <see cref="MarkRead"/> is a single bit OR plus at most one stamp write. At block end the
/// workers' bitmaps are OR-reduced via <see cref="Absorb"/> and the chargeable counts summed; the
/// reduced bitmap must have all <c>TotalReads</c> bits set (checked by <see cref="TryFindFirstUncovered"/>).
/// <para>
/// The chargeable stamp distinguishes "first read of this ordinal in this slice" from a repeat by
/// comparing a caller-supplied non-zero slice stamp against the rented-cleared stamp array (0 means
/// "never seen"); callers pass the block-access index plus one so slice 0 is non-zero. Distinct
/// slices stamp distinct values, so the same ordinal counts once per slice without clearing the
/// array between slices. Backing arrays are pooled and released on <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed class BalReadCoverage : IDisposable
{
    private ulong[] _coverage;
    private uint[] _sliceStamp;
    private readonly int _count;
    private readonly int _wordCount;
    private long _chargeableCount;

    /// <summary>Size of the ordinal space (the block's total declared storage reads).</summary>
    public int Count => _count;

    /// <summary>This instance's accumulated per-slice-distinct chargeable read count.</summary>
    public long ChargeableCount => _chargeableCount;

    public BalReadCoverage(int count)
    {
        _count = count;
        _wordCount = (count + 63) >> 6;
        _coverage = _wordCount == 0 ? [] : ArrayPool<ulong>.Shared.Rent(_wordCount);
        _sliceStamp = count == 0 ? [] : ArrayPool<uint>.Shared.Rent(count);
        // Rented arrays carry the previous tenant's contents; both lanes must start cleared.
        _coverage.AsSpan(0, _wordCount).Clear();
        _sliceStamp.AsSpan(0, count).Clear();
    }

    /// <summary>
    /// Records a declared read at global <paramref name="ordinal"/>. Always marks the structural
    /// coverage bit; when <paramref name="chargeable"/> (a non-system-contract read), also counts it
    /// once per <paramref name="sliceStamp"/>.
    /// </summary>
    /// <param name="sliceStamp">Non-zero per-slice stamp (block-access index + 1).</param>
    public void MarkRead(int ordinal, uint sliceStamp, bool chargeable)
    {
        _coverage[ordinal >> 6] |= 1UL << (ordinal & 63);
        if (chargeable && _sliceStamp[ordinal] != sliceStamp)
        {
            _sliceStamp[ordinal] = sliceStamp;
            _chargeableCount++;
        }
    }

    /// <summary>OR-reduces <paramref name="other"/>'s structural coverage into this instance and adds its chargeable count.</summary>
    public void Absorb(BalReadCoverage other)
    {
        ulong[] mine = _coverage;
        ulong[] theirs = other._coverage;
        for (int w = 0; w < _wordCount; w++)
        {
            mine[w] |= theirs[w];
        }
        _chargeableCount += other._chargeableCount;
    }

    /// <summary>
    /// Finds the first declared read ordinal whose coverage bit is unset, i.e. a suggested read that
    /// execution never performed. The "expected" mask (all <see cref="Count"/> bits set) is implied
    /// and computed inline rather than materialized.
    /// </summary>
    /// <returns><c>true</c> with the uncovered ordinal; <c>false</c> if every declared read is covered.</returns>
    public bool TryFindFirstUncovered(out int ordinal)
    {
        int fullWords = _count >> 6;
        for (int w = 0; w < fullWords; w++)
        {
            ulong missing = ~_coverage[w];
            if (missing != 0)
            {
                ordinal = (w << 6) + BitOperations.TrailingZeroCount(missing);
                return true;
            }
        }

        int remBits = _count & 63;
        if (remBits != 0)
        {
            ulong mask = (1UL << remBits) - 1;
            ulong missing = ~_coverage[fullWords] & mask;
            if (missing != 0)
            {
                ordinal = (fullWords << 6) + BitOperations.TrailingZeroCount(missing);
                return true;
            }
        }

        ordinal = -1;
        return false;
    }

    public void Dispose()
    {
        ulong[] coverage = _coverage;
        uint[] sliceStamp = _sliceStamp;
        _coverage = [];
        _sliceStamp = [];

        if (coverage.Length > 0) ArrayPool<ulong>.Shared.Return(coverage);
        if (sliceStamp.Length > 0) ArrayPool<uint>.Shared.Return(sliceStamp);
    }
}
