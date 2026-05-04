// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.State.Flat.BSearchIndex;

/// <summary>
/// SIMD floor-search fast paths for <see cref="BSearchIndexReader"/> Uniform (KeyType=1)
/// keys with small fan-out. For 4- and 8-byte fixed-width keys (typical at intermediate
/// index levels and in compact leaves), the BCL's <c>SequenceCompareTo</c> per-call setup
/// cost dominates the actual byte compare; a vectorised linear scan is faster on small
/// counts and avoids the log-N branch mispredicts of binary search.
///
/// Unsigned big-endian integer compare is equivalent to lexicographic byte compare for
/// fixed-width keys, so we byte-swap each lane and use signed <c>GreaterThan</c> with a
/// sign-bias XOR to emulate unsigned compare.
///
/// Three vector widths supported with runtime dispatch (Vector512 → Vector256 → Vector128).
/// </summary>
internal static class BSearchIndexReaderSimd
{
    // Cap: scan up to this many keys with the linear SIMD path. Beyond this, scalar
    // binary search wins despite mispredict cost. The benchmark sweep informs this
    // value — current setting covers all probed leaf sizes (64–1024).
    private const int LinearScanMaxCount = 1024;

    private static readonly Vector128<byte> ByteSwap32Mask128 = Vector128.Create(
        (byte)3, 2, 1, 0,
        7, 6, 5, 4,
        11, 10, 9, 8,
        15, 14, 13, 12);

    private static readonly Vector128<byte> ByteSwap64Mask128 = Vector128.Create(
        (byte)7, 6, 5, 4, 3, 2, 1, 0,
        15, 14, 13, 12, 11, 10, 9, 8);

    private static readonly Vector256<byte> ByteSwap32Mask256 = Vector256.Create(
        (byte)3, 2, 1, 0,
        7, 6, 5, 4,
        11, 10, 9, 8,
        15, 14, 13, 12,
        19, 18, 17, 16,
        23, 22, 21, 20,
        27, 26, 25, 24,
        31, 30, 29, 28);

    private static readonly Vector256<byte> ByteSwap64Mask256 = Vector256.Create(
        (byte)7, 6, 5, 4, 3, 2, 1, 0,
        15, 14, 13, 12, 11, 10, 9, 8,
        23, 22, 21, 20, 19, 18, 17, 16,
        31, 30, 29, 28, 27, 26, 25, 24);

    private static readonly Vector512<byte> ByteSwap32Mask512 = Vector512.Create(
        (byte)3, 2, 1, 0,
        7, 6, 5, 4,
        11, 10, 9, 8,
        15, 14, 13, 12,
        19, 18, 17, 16,
        23, 22, 21, 20,
        27, 26, 25, 24,
        31, 30, 29, 28,
        35, 34, 33, 32,
        39, 38, 37, 36,
        43, 42, 41, 40,
        47, 46, 45, 44,
        51, 50, 49, 48,
        55, 54, 53, 52,
        59, 58, 57, 56,
        63, 62, 61, 60);

    private static readonly Vector512<byte> ByteSwap64Mask512 = Vector512.Create(
        (byte)7, 6, 5, 4, 3, 2, 1, 0,
        15, 14, 13, 12, 11, 10, 9, 8,
        23, 22, 21, 20, 19, 18, 17, 16,
        31, 30, 29, 28, 27, 26, 25, 24,
        39, 38, 37, 36, 35, 34, 33, 32,
        47, 46, 45, 44, 43, 42, 41, 40,
        55, 54, 53, 52, 51, 50, 49, 48,
        63, 62, 61, 60, 59, 58, 57, 56);

    /// <summary>
    /// Try to compute the floor index using a SIMD linear scan. Returns false if the
    /// key shape is not supported by a fast path; the caller falls back to scalar
    /// binary search.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFindFloorIndexUniformSimd(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> keys,
        int count,
        int keySize,
        out int result)
    {
        result = 0;
        if (count < 2 || count > LinearScanMaxCount) return false;
        if (key.Length != keySize) return false;
        if (!Vector128.IsHardwareAccelerated) return false;

        switch (keySize)
        {
            case 4:
                result = FloorScan32(key, keys, count);
                return true;
            case 8:
                result = FloorScan64(key, keys, count);
                return true;
            default:
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan32(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        uint search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(key)));
        ref byte src = ref MemoryMarshal.GetReference(keys);

        if (Vector512.IsHardwareAccelerated)
            return FloorScan32_V512(search, ref src, count);
        if (Vector256.IsHardwareAccelerated)
            return FloorScan32_V256(search, ref src, count);
        return FloorScan32_V128(search, ref src, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan64(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        ulong search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(key)));
        ref byte src = ref MemoryMarshal.GetReference(keys);

        if (Vector512.IsHardwareAccelerated)
            return FloorScan64_V512(search, ref src, count);
        if (Vector256.IsHardwareAccelerated)
            return FloorScan64_V256(search, ref src, count);
        return FloorScan64_V128(search, ref src, count);
    }

    // ---------------- KeySize=4 ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan32_V128(uint search, ref byte src, int count)
    {
        Vector128<int> searchVec = Vector128.Create(unchecked((int)(search ^ 0x80000000u)));
        Vector128<uint> signBias = Vector128.Create(0x80000000u);
        int i = 0;
        // 4 keys per iteration.
        while (i + 4 <= count)
        {
            Vector128<uint> raw = Vector128.LoadUnsafe(ref src, (nuint)(i * 4)).AsUInt32();
            Vector128<uint> be = Vector128.Shuffle(raw.AsByte(), ByteSwap32Mask128).AsUInt32();
            Vector128<int> gt = Vector128.GreaterThan((be ^ signBias).AsInt32(), searchVec);
            uint mask = gt.AsByte().ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask) >> 2;
                return i + firstGtLane - 1;
            }
            i += 4;
        }
        return ScalarTail32(search, ref src, i, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan32_V256(uint search, ref byte src, int count)
    {
        Vector256<int> searchVec = Vector256.Create(unchecked((int)(search ^ 0x80000000u)));
        Vector256<uint> signBias = Vector256.Create(0x80000000u);
        int i = 0;
        // 8 keys per iteration.
        while (i + 8 <= count)
        {
            Vector256<uint> raw = Vector256.LoadUnsafe(ref src, (nuint)(i * 4)).AsUInt32();
            Vector256<uint> be = Vector256.Shuffle(raw.AsByte(), ByteSwap32Mask256).AsUInt32();
            Vector256<int> gt = Vector256.GreaterThan((be ^ signBias).AsInt32(), searchVec);
            uint mask = gt.AsByte().ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask) >> 2;
                return i + firstGtLane - 1;
            }
            i += 8;
        }
        // Tail (at most 7 keys remain): scalar.
        return ScalarTail32(search, ref src, i, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan32_V512(uint search, ref byte src, int count)
    {
        Vector512<int> searchVec = Vector512.Create(unchecked((int)(search ^ 0x80000000u)));
        Vector512<uint> signBias = Vector512.Create(0x80000000u);
        int i = 0;
        // 16 keys per iteration.
        while (i + 16 <= count)
        {
            Vector512<uint> raw = Vector512.LoadUnsafe(ref src, (nuint)(i * 4)).AsUInt32();
            Vector512<uint> be = Vector512.Shuffle(raw.AsByte(), ByteSwap32Mask512).AsUInt32();
            Vector512<int> gt = Vector512.GreaterThan((be ^ signBias).AsInt32(), searchVec);
            ulong mask = gt.AsByte().ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask) >> 2;
                return i + firstGtLane - 1;
            }
            i += 16;
        }
        return ScalarTail32(search, ref src, i, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarTail32(uint search, ref byte src, int i, int count)
    {
        for (; i < count; i++)
        {
            uint k = BinaryPrimitives.ReverseEndianness(
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, (nint)(i * 4))));
            if (k > search) return i - 1;
        }
        return count - 1;
    }

    // ---------------- KeySize=8 ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan64_V128(ulong search, ref byte src, int count)
    {
        Vector128<long> searchVec = Vector128.Create(unchecked((long)(search ^ 0x8000000000000000UL)));
        Vector128<ulong> signBias = Vector128.Create(0x8000000000000000UL);
        int i = 0;
        // 2 keys per iteration.
        while (i + 2 <= count)
        {
            Vector128<ulong> raw = Vector128.LoadUnsafe(ref src, (nuint)(i * 8)).AsUInt64();
            Vector128<ulong> be = Vector128.Shuffle(raw.AsByte(), ByteSwap64Mask128).AsUInt64();
            Vector128<long> gt = Vector128.GreaterThan((be ^ signBias).AsInt64(), searchVec);
            uint mask = gt.AsByte().ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask) >> 3;
                return i + firstGtLane - 1;
            }
            i += 2;
        }
        return ScalarTail64(search, ref src, i, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan64_V256(ulong search, ref byte src, int count)
    {
        Vector256<long> searchVec = Vector256.Create(unchecked((long)(search ^ 0x8000000000000000UL)));
        Vector256<ulong> signBias = Vector256.Create(0x8000000000000000UL);
        int i = 0;
        // 4 keys per iteration.
        while (i + 4 <= count)
        {
            Vector256<ulong> raw = Vector256.LoadUnsafe(ref src, (nuint)(i * 8)).AsUInt64();
            Vector256<ulong> be = Vector256.Shuffle(raw.AsByte(), ByteSwap64Mask256).AsUInt64();
            Vector256<long> gt = Vector256.GreaterThan((be ^ signBias).AsInt64(), searchVec);
            uint mask = gt.AsByte().ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask) >> 3;
                return i + firstGtLane - 1;
            }
            i += 4;
        }
        return ScalarTail64(search, ref src, i, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan64_V512(ulong search, ref byte src, int count)
    {
        Vector512<long> searchVec = Vector512.Create(unchecked((long)(search ^ 0x8000000000000000UL)));
        Vector512<ulong> signBias = Vector512.Create(0x8000000000000000UL);
        int i = 0;
        // 8 keys per iteration.
        while (i + 8 <= count)
        {
            Vector512<ulong> raw = Vector512.LoadUnsafe(ref src, (nuint)(i * 8)).AsUInt64();
            Vector512<ulong> be = Vector512.Shuffle(raw.AsByte(), ByteSwap64Mask512).AsUInt64();
            Vector512<long> gt = Vector512.GreaterThan((be ^ signBias).AsInt64(), searchVec);
            ulong mask = gt.AsByte().ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask) >> 3;
                return i + firstGtLane - 1;
            }
            i += 8;
        }
        return ScalarTail64(search, ref src, i, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarTail64(ulong search, ref byte src, int i, int count)
    {
        for (; i < count; i++)
        {
            ulong k = BinaryPrimitives.ReverseEndianness(
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, (nint)(i * 8))));
            if (k > search) return i - 1;
        }
        return count - 1;
    }
}
