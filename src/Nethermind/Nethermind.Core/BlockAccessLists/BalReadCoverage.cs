// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-slice proof that the suggested BAL's declared storage reads were executed, replacing the
/// materialized generated read set. Two lanes over the dense ordinal space of
/// <see cref="BalReadStoragePlan"/>: a structural coverage bitmap (every declared read, including
/// system-contract reads) and a chargeable count (non-system reads, distinct within the slice) for
/// the EIP-7928 read budget.
/// </summary>
/// <remarks>
/// Each parallel worker owns a fresh instance per slice (per tx) and marks it without synchronization
/// on the SLOAD hot path: <see cref="MarkRead"/> is a single bit OR. Because the bitmap starts empty
/// per slice, an unset bit means "first read of this ordinal in this slice", so the chargeable count
/// dedups via the bitmap itself with no separate stamp array. At block end the slices' bitmaps are
/// OR-reduced via <see cref="Absorb"/> (chargeable counts summed); the reduced bitmap must have all
/// <c>TotalReads</c> bits set (checked by <see cref="TryFindFirstUncovered"/>). The backing array is
/// pooled and released on <see cref="Dispose"/>.
/// </remarks>
public sealed class BalReadCoverage : IDisposable
{
    private ulong[] _coverage;
    private readonly int _count;
    private readonly int _wordCount;
    private long _chargeableCount;

    /// <summary>Size of the ordinal space (the block's total declared storage reads).</summary>
    public int Count => _count;

    /// <summary>This slice's distinct chargeable read count (summed across slices by <see cref="Absorb"/>).</summary>
    public long ChargeableCount => _chargeableCount;

    public BalReadCoverage(int count)
    {
        _count = count;
        _wordCount = (count + 63) >> 6;
        _coverage = _wordCount == 0 ? [] : ArrayPool<ulong>.Shared.Rent(_wordCount);
        // Rented arrays carry the previous tenant's contents; the bitmap must start cleared.
        _coverage.AsSpan(0, _wordCount).Clear();
    }

    /// <summary>
    /// Records a declared read at global <paramref name="ordinal"/>. Marks the structural coverage bit;
    /// when <paramref name="chargeable"/> (a non-system-contract read) and this is the first time the
    /// ordinal is seen this slice, counts it.
    /// </summary>
    public void MarkRead(int ordinal, bool chargeable)
    {
        ref ulong word = ref _coverage[ordinal >> 6];
        ulong mask = 1UL << (ordinal & 63);
        if ((word & mask) == 0)
        {
            word |= mask;
            if (chargeable) _chargeableCount++;
        }
    }

    /// <summary>OR-reduces <paramref name="other"/>'s structural coverage into this instance and adds its chargeable count.</summary>
    public void Absorb(BalReadCoverage other)
    {
        int n = _wordCount;
        ref ulong mine = ref MemoryMarshal.GetArrayDataReference(_coverage);
        ref ulong theirs = ref MemoryMarshal.GetArrayDataReference(other._coverage);
        int w = 0;

        if (Vector512.IsHardwareAccelerated)
        {
            for (; w + Vector512<ulong>.Count <= n; w += Vector512<ulong>.Count)
                (Vector512.LoadUnsafe(ref mine, (nuint)w) | Vector512.LoadUnsafe(ref theirs, (nuint)w)).StoreUnsafe(ref mine, (nuint)w);
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            for (; w + Vector256<ulong>.Count <= n; w += Vector256<ulong>.Count)
                (Vector256.LoadUnsafe(ref mine, (nuint)w) | Vector256.LoadUnsafe(ref theirs, (nuint)w)).StoreUnsafe(ref mine, (nuint)w);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; w + Vector128<ulong>.Count <= n; w += Vector128<ulong>.Count)
                (Vector128.LoadUnsafe(ref mine, (nuint)w) | Vector128.LoadUnsafe(ref theirs, (nuint)w)).StoreUnsafe(ref mine, (nuint)w);
        }

        for (; w < n; w++)
        {
            _coverage[w] |= other._coverage[w];
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
        ref ulong cov = ref MemoryMarshal.GetArrayDataReference(_coverage);
        int w = 0;
        // SIMD fast-skip: a fully-covered run is all-ones, the common valid-block case. Drop to the
        // scalar word scan at the first vector block (or the partial tail word) that has a gap.
        if (Vector512.IsHardwareAccelerated)
        {
            for (; w + Vector512<ulong>.Count <= fullWords; w += Vector512<ulong>.Count)
                if (Vector512.LoadUnsafe(ref cov, (nuint)w) != Vector512<ulong>.AllBitsSet) break;
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            for (; w + Vector256<ulong>.Count <= fullWords; w += Vector256<ulong>.Count)
                if (Vector256.LoadUnsafe(ref cov, (nuint)w) != Vector256<ulong>.AllBitsSet) break;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; w + Vector128<ulong>.Count <= fullWords; w += Vector128<ulong>.Count)
                if (Vector128.LoadUnsafe(ref cov, (nuint)w) != Vector128<ulong>.AllBitsSet) break;
        }
        for (; w < fullWords; w++)
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
        _coverage = [];
        if (coverage.Length > 0) ArrayPool<ulong>.Shared.Return(coverage);
    }
}
