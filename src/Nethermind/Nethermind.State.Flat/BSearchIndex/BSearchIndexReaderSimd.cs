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
/// keys with small fan-out. For 2-, 4- and 8-byte fixed-width keys (typical at intermediate
/// index levels and in compact leaves), the BCL's <c>SequenceCompareTo</c> per-call setup
/// cost dominates the actual byte compare; a vectorised linear scan is faster on small
/// counts and avoids the log-N branch mispredicts of binary search.
///
/// Unsigned big-endian integer compare is equivalent to lexicographic byte compare for
/// fixed-width keys, so we byte-swap each lane and use AVX-512's native unsigned
/// <c>GreaterThan</c> on <c>Vector512&lt;uint&gt;</c> / <c>Vector512&lt;ulong&gt;</c>.
///
/// AVX-512 only: when <see cref="Vector512.IsHardwareAccelerated"/> is false the
/// fast path is skipped and the caller falls back to scalar binary search.
/// </summary>
public static class BSearchIndexReaderSimd
{
    /// <summary>
    /// Runtime toggle for the SIMD floor-scan fast path. Default <c>false</c>: scalar
    /// binary search wins at cache-resident scales on AMD EPYC 9575F (BDN bench at
    /// 100k entries, minSep=4); the SIMD code is preserved for re-enable under future
    /// workloads / dispatch tuning. The benchmark uses [Params] to flip this for A/B.
    /// </summary>
    public static bool Enabled = false;

    /// <summary>
    /// Cap: scan up to this many keys with the linear SIMD path. Beyond this, scalar
    /// binary search wins despite mispredict cost. Tunable at runtime alongside
    /// <see cref="Enabled"/> so benchmarks can sweep it via <c>[Params]</c>.
    /// </summary>
    public static int LinearScanMaxCount = 1024;

    private static readonly Vector512<byte> ByteSwap16Mask512 = Vector512.Create(
        (byte)1, 0,
        3, 2,
        5, 4,
        7, 6,
        9, 8,
        11, 10,
        13, 12,
        15, 14,
        17, 16,
        19, 18,
        21, 20,
        23, 22,
        25, 24,
        27, 26,
        29, 28,
        31, 30,
        33, 32,
        35, 34,
        37, 36,
        39, 38,
        41, 40,
        43, 42,
        45, 44,
        47, 46,
        49, 48,
        51, 50,
        53, 52,
        55, 54,
        57, 56,
        59, 58,
        61, 60,
        63, 62);

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
        bool isLittleEndian,
        out int result)
    {
        result = 0;
        if (!Enabled) return false;
        if (count < 2 || count > LinearScanMaxCount) return false;
        // BE path requires exact-length keys (lex compare semantics). LE path tolerates a
        // longer search key — the first keySize bytes drive the integer compare and an equal
        // prefix with a longer key still yields the correct "search >= stored" floor decision.
        if (isLittleEndian ? key.Length < keySize : key.Length != keySize) return false;
        if (!Vector512.IsHardwareAccelerated) return false;

        switch (keySize)
        {
            case 2:
                result = FloorScan16(key, keys, count, isLittleEndian);
                return true;
            case 4:
                result = FloorScan32(key, keys, count, isLittleEndian);
                return true;
            case 8:
                result = FloorScan64(key, keys, count, isLittleEndian);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// SIMD floor scan for <c>UniformWithLen</c> nodes with slotSize=4 (3-byte payload +
    /// 1-byte length). The writer guarantees unused payload bytes are zero
    /// (<see cref="BSearchIndexWriter{TWriter}.FinalizeUniformWithLenKeys"/> clears the
    /// slot before filling), so each slot's uint32 BE value preserves lex+length ordering:
    /// (a) within equal lengths, the payload prefix dominates the compare; (b) for keys
    /// sharing a prefix but differing in length, the shorter key has zero-padded bytes
    /// followed by a smaller length byte, which gives the correct "shorter is less"
    /// ordering. The search key is encoded into the same 4-byte slot format and we reuse
    /// the existing <see cref="FloorScan32"/> dispatcher.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFindFloorIndexUniformWithLenSimd(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> keys,
        int count,
        int slotSize,
        out int result)
    {
        result = 0;
        if (!Enabled) return false;
        if (slotSize != 4) return false;
        if (count < 2 || count > LinearScanMaxCount) return false;
        if (!Vector512.IsHardwareAccelerated) return false;

        // Encode the search key into the storage slot format: first min(3, keyLen) bytes
        // of payload (zero-padded), then a length byte = min(keyLen, 255). The writer
        // stores actualLen ∈ [0, 3] in the length byte; using 255 for over-long search
        // keys is safe because uint32 BE compare on the length byte runs last and the
        // cap stays > any stored length.
        Span<byte> encoded = stackalloc byte[4];
        int payloadLen = Math.Min(key.Length, 3);
        if (payloadLen > 0) key[..payloadLen].CopyTo(encoded);
        encoded[3] = (byte)Math.Min(key.Length, 255);

        // UniformWithLen always stores slots in BE form (the LE flag never applies — see
        // BSearchIndexWriter.ShouldEncodeKeyLittleEndian), so reuse the BE FloorScan32 path.
        result = FloorScan32(encoded, keys, count, isLittleEndian: false);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan16(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, bool isLittleEndian)
    {
        // search arrives lex-ordered. ReverseEndianness produces the value of a native LE load
        // applied to the BE-stored bytes — equivalent to the value of a native LE load applied
        // to LE-stored bytes — so the same broadcast works for both layouts.
        ushort search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(key)));
        ref byte src = ref MemoryMarshal.GetReference(keys);

        Vector512<ushort> searchVec = Vector512.Create(search);
        int i = 0;
        // 32 keys per iteration.
        while (i + 32 <= count)
        {
            Vector512<ushort> raw = Vector512.LoadUnsafe(ref src, (nuint)(i * 2)).AsUInt16();
            // BE-stored: shuffle each lane to recover the native integer value. LE-stored:
            // raw already IS the native integer value — skip the shuffle.
            Vector512<ushort> lanes = isLittleEndian
                ? raw
                : Vector512.Shuffle(raw.AsByte(), ByteSwap16Mask512).AsUInt16();
            Vector512<ushort> gt = Vector512.GreaterThan(lanes, searchVec);
            ulong mask = gt.ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask);
                return i + firstGtLane - 1;
            }
            i += 32;
        }
        return ScalarTail16(search, ref src, i, count, isLittleEndian);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan32(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, bool isLittleEndian)
    {
        uint search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(key)));
        ref byte src = ref MemoryMarshal.GetReference(keys);

        Vector512<uint> searchVec = Vector512.Create(search);
        int i = 0;
        // 16 keys per iteration.
        while (i + 16 <= count)
        {
            Vector512<uint> raw = Vector512.LoadUnsafe(ref src, (nuint)(i * 4)).AsUInt32();
            Vector512<uint> lanes = isLittleEndian
                ? raw
                : Vector512.Shuffle(raw.AsByte(), ByteSwap32Mask512).AsUInt32();
            Vector512<uint> gt = Vector512.GreaterThan(lanes, searchVec);
            ulong mask = gt.ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask);
                return i + firstGtLane - 1;
            }
            i += 16;
        }
        return ScalarTail32(search, ref src, i, count, isLittleEndian);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan64(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, bool isLittleEndian)
    {
        ulong search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(key)));
        ref byte src = ref MemoryMarshal.GetReference(keys);

        Vector512<ulong> searchVec = Vector512.Create(search);
        int i = 0;
        // 8 keys per iteration.
        while (i + 8 <= count)
        {
            Vector512<ulong> raw = Vector512.LoadUnsafe(ref src, (nuint)(i * 8)).AsUInt64();
            Vector512<ulong> lanes = isLittleEndian
                ? raw
                : Vector512.Shuffle(raw.AsByte(), ByteSwap64Mask512).AsUInt64();
            Vector512<ulong> gt = Vector512.GreaterThan(lanes, searchVec);
            ulong mask = gt.ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask);
                return i + firstGtLane - 1;
            }
            i += 8;
        }
        return ScalarTail64(search, ref src, i, count, isLittleEndian);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarTail16(ushort search, ref byte src, int i, int count, bool isLittleEndian)
    {
        for (; i < count; i++)
        {
            ushort raw = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, (nint)(i * 2)));
            ushort k = isLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            if (k > search) return i - 1;
        }
        return count - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarTail32(uint search, ref byte src, int i, int count, bool isLittleEndian)
    {
        for (; i < count; i++)
        {
            uint raw = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, (nint)(i * 4)));
            uint k = isLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            if (k > search) return i - 1;
        }
        return count - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarTail64(ulong search, ref byte src, int i, int count, bool isLittleEndian)
    {
        for (; i < count; i++)
        {
            ulong raw = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, (nint)(i * 8)));
            ulong k = isLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            if (k > search) return i - 1;
        }
        return count - 1;
    }
}
