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
/// </summary>
internal static class BSearchIndexReaderSimd
{
    // HSST nodes hold up to MaxLeafEntries = 64 entries; cover the full range so the
    // SIMD path also fires on packed leaves (not only partial / upper-level nodes).
    private const int LinearScanMaxCount = 64;

    private static readonly Vector128<byte> ByteSwap32Mask = Vector128.Create(
        (byte)3, 2, 1, 0,
        7, 6, 5, 4,
        11, 10, 9, 8,
        15, 14, 13, 12);

    private static readonly Vector128<byte> ByteSwap64Mask = Vector128.Create(
        (byte)7, 6, 5, 4, 3, 2, 1, 0,
        15, 14, 13, 12, 11, 10, 9, 8);

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
        Vector128<int> searchVec = Vector128.Create(unchecked((int)(search ^ 0x80000000u)));
        Vector128<uint> signBias = Vector128.Create(0x80000000u);

        ref byte src = ref MemoryMarshal.GetReference(keys);
        int i = 0;
        // Each Vector128 holds 4 keys (16 bytes). count ≤ 16 so at most 4 iterations.
        while (i + 4 <= count)
        {
            Vector128<uint> raw = Vector128
                .LoadUnsafe(ref src, (nuint)(i * 4))
                .AsUInt32();
            Vector128<uint> be = Vector128.Shuffle(raw.AsByte(), ByteSwap32Mask).AsUInt32();
            Vector128<int> gt = Vector128.GreaterThan((be ^ signBias).AsInt32(), searchVec);
            uint mask = gt.AsByte().ExtractMostSignificantBits();
            if (mask != 0)
            {
                // mask has 4 bits per lane (one per byte). Lane index = trailing-zero-count >> 2.
                int firstGtLane = BitOperations.TrailingZeroCount(mask) >> 2;
                return i + firstGtLane - 1;
            }
            i += 4;
        }
        // Tail (count not a multiple of 4): scalar with the same big-endian compare.
        for (; i < count; i++)
        {
            uint k = BinaryPrimitives.ReverseEndianness(
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, (nint)(i * 4))));
            if (k > search) return i - 1;
        }
        return count - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan64(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        ulong search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(key)));
        Vector128<long> searchVec = Vector128.Create(unchecked((long)(search ^ 0x8000000000000000UL)));
        Vector128<ulong> signBias = Vector128.Create(0x8000000000000000UL);

        ref byte src = ref MemoryMarshal.GetReference(keys);
        int i = 0;
        // Each Vector128 holds 2 keys (16 bytes).
        while (i + 2 <= count)
        {
            Vector128<ulong> raw = Vector128
                .LoadUnsafe(ref src, (nuint)(i * 8))
                .AsUInt64();
            Vector128<ulong> be = Vector128.Shuffle(raw.AsByte(), ByteSwap64Mask).AsUInt64();
            Vector128<long> gt = Vector128.GreaterThan((be ^ signBias).AsInt64(), searchVec);
            uint mask = gt.AsByte().ExtractMostSignificantBits();
            if (mask != 0)
            {
                // 8 bits per lane; lane index = trailing-zero-count >> 3.
                int firstGtLane = BitOperations.TrailingZeroCount(mask) >> 3;
                return i + firstGtLane - 1;
            }
            i += 2;
        }
        if (i < count)
        {
            ulong k = BinaryPrimitives.ReverseEndianness(
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, (nint)(i * 8))));
            if (k > search) return i - 1;
        }
        return count - 1;
    }
}
