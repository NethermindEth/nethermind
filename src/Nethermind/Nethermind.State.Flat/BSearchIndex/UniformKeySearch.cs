// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.State.Flat.BSearchIndex;

/// <summary>
/// Unified uniform-width key search utility. One public method per (size, endian) combo,
/// each internally choosing an AVX-512 linear scan vs. scalar binary search based on
/// hardware support and the <see cref="Enabled"/> / <see cref="LinearScanMaxCount"/> toggles.
/// </summary>
/// <remarks>
/// Layouts covered:
/// <list type="bullet">
///   <item><c>UniformN[LE|BE]</c>: contiguous fixed-width keys, N bytes per slot. Floor lookup.</item>
///   <item><c>UniformN[LE|BE]Strided</c>: same as above but each slot is followed by a value
///         (slot stride &gt; keySize), e.g. HSST PackedArray data section.</item>
///   <item><c>LowerBound2LE</c>: 2-byte LE-stored lower_bound (different semantics from floor).</item>
///   <item>Generic <c>UniformBE</c> / <c>UniformBEStrided</c>: lex
///         <see cref="MemoryExtensions.SequenceCompareTo{T}"/> binary search for keySizes
///         outside {2,3,4,8} (or 3-byte BE, which has no SIMD specialization).</item>
/// </list>
/// LE-stored fixed-width keys are byte-reversed on disk so a native unsigned integer load
/// recovers the BE numeric value of the original lex key — that makes unsigned integer
/// compare equivalent to lex byte compare and unlocks the SIMD <c>GreaterThan</c> fast path.
/// LE-stored is only valid for keySizes 2/4/8 (and 3 in the HSST PackedArray summary level).
/// </remarks>
public static class UniformKeySearch
{
    /// <summary>
    /// Runtime toggle for the AVX-512 floor-scan fast path. Default <c>true</c>. The
    /// benchmark uses [Params] to flip this for A/B comparison; tests sweep it as well.
    /// </summary>
    public static bool Enabled = true;

    /// <summary>
    /// Cap: scan up to this many keys with the linear SIMD path. Beyond this, scalar
    /// binary search wins despite mispredict cost. Tunable at runtime alongside
    /// <see cref="Enabled"/> so benchmarks can sweep it via <c>[Params]</c>.
    /// </summary>
    public static int LinearScanMaxCount = 1024;

    // ---- AVX-512 shuffle masks (private) ----

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

    // 3-byte LE packed-key gather: each output u32 lane pulls (3n, 3n+1, 3n+2) from the
    // raw 64-byte load and forces the high byte to zero via an out-of-range index (>=64
    // → 0 per Vector512.Shuffle<byte> semantics). Cross-lane: requires AVX-512 VBMI
    // (vpermb). The unused tail of the load (bytes 48..63) is never addressed.
    private static readonly Vector512<byte> Pack24LeMask512 = Vector512.Create(
        (byte)0, 1, 2, 0xFF,
        3, 4, 5, 0xFF,
        6, 7, 8, 0xFF,
        9, 10, 11, 0xFF,
        12, 13, 14, 0xFF,
        15, 16, 17, 0xFF,
        18, 19, 20, 0xFF,
        21, 22, 23, 0xFF,
        24, 25, 26, 0xFF,
        27, 28, 29, 0xFF,
        30, 31, 32, 0xFF,
        33, 34, 35, 0xFF,
        36, 37, 38, 0xFF,
        39, 40, 41, 0xFF,
        42, 43, 44, 0xFF,
        45, 46, 47, 0xFF);

    private static readonly Vector512<byte> ByteSwap64Mask512 = Vector512.Create(
        (byte)7, 6, 5, 4, 3, 2, 1, 0,
        15, 14, 13, 12, 11, 10, 9, 8,
        23, 22, 21, 20, 19, 18, 17, 16,
        31, 30, 29, 28, 27, 26, 25, 24,
        39, 38, 37, 36, 35, 34, 33, 32,
        47, 46, 45, 44, 43, 42, 41, 40,
        55, 54, 53, 52, 51, 50, 49, 48,
        63, 62, 61, 60, 59, 58, 57, 56);

    // Per-lane index vectors. Combined with Vector512.LessThan(idx, broadcast(remaining))
    // they produce the lane mask consumed by Avx512{BW,F}.MaskLoad for the trailing
    // (<N keys) iteration of the FloorScan kernels.
    private static readonly Vector512<ushort> LaneIdx16 = Vector512.Create(
        (ushort)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31);
    private static readonly Vector512<uint> LaneIdx32 = Vector512.Create(
        0u, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
    private static readonly Vector512<ulong> LaneIdx64 = Vector512.Create(0ul, 1, 2, 3, 4, 5, 6, 7);

    // =====================================================================================
    //  Contiguous floor index (largest i in [0, count) where keys[i] <= search; -1 if none)
    // =====================================================================================

    /// <summary>Floor index over 2-byte LE-stored keys.</summary>
    public static int Uniform2LE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        if (count == 0) return -1;
        if (Enabled && Vector512.IsHardwareAccelerated && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan16(key, keys, count, isLittleEndian: true);
        return BinarySearch2LE(key, keys, count);
    }

    /// <summary>Floor index over 2-byte BE-stored (lex-ordered) keys.</summary>
    public static int Uniform2BE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        if (count == 0) return -1;
        if (Enabled && Vector512.IsHardwareAccelerated && key.Length == 2 && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan16(key, keys, count, isLittleEndian: false);
        return BinarySearchLex(key, keys, count, keySize: 2);
    }

    /// <summary>
    /// Floor index over 3-byte LE-stored keys. SIMD path requires AVX-512 VBMI; otherwise
    /// falls back to scalar integer-compare binary search.
    /// </summary>
    public static int Uniform3LE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        if (count == 0) return -1;
        if (Enabled && Vector512.IsHardwareAccelerated && Avx512Vbmi.IsSupported
            && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan24Le(key, keys, count);
        return BinarySearch3LE(key, keys, count);
    }

    /// <summary>Floor index over 4-byte LE-stored keys.</summary>
    public static int Uniform4LE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        if (count == 0) return -1;
        if (Enabled && Vector512.IsHardwareAccelerated && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan32(key, keys, count, isLittleEndian: true);
        return BinarySearch4LE(key, keys, count);
    }

    /// <summary>Floor index over 4-byte BE-stored (lex-ordered) keys.</summary>
    public static int Uniform4BE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        if (count == 0) return -1;
        if (Enabled && Vector512.IsHardwareAccelerated && key.Length == 4 && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan32(key, keys, count, isLittleEndian: false);
        return BinarySearchLex(key, keys, count, keySize: 4);
    }

    /// <summary>Floor index over 8-byte LE-stored keys.</summary>
    public static int Uniform8LE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        if (count == 0) return -1;
        if (Enabled && Vector512.IsHardwareAccelerated && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan64(key, keys, count, isLittleEndian: true);
        return BinarySearch8LE(key, keys, count);
    }

    /// <summary>Floor index over 8-byte BE-stored (lex-ordered) keys.</summary>
    public static int Uniform8BE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        if (count == 0) return -1;
        if (Enabled && Vector512.IsHardwareAccelerated && key.Length == 8 && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan64(key, keys, count, isLittleEndian: false);
        return BinarySearchLex(key, keys, count, keySize: 8);
    }

    /// <summary>
    /// Floor index over BE-stored (lex-ordered) keys of arbitrary <paramref name="keySize"/>.
    /// Always scalar; use the size-specialised methods when applicable.
    /// </summary>
    public static int UniformBE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int keySize)
    {
        if (count == 0) return -1;
        return BinarySearchLex(key, keys, count, keySize);
    }

    // =====================================================================================
    //  Strided floor index (interleaved key+value entries; stride > keySize typical, but
    //  stride == keySize is delegated to the contiguous fast path)
    // =====================================================================================

    /// <summary>Floor index over 2-byte LE-stored keys with a strided layout.</summary>
    public static int Uniform2LEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride)
    {
        if (count == 0) return -1;
        if (stride == 2) return Uniform2LE(key, src, count);
        if (Enabled && Vector512.IsHardwareAccelerated && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan16Strided(key, src, count, stride, isLittleEndian: true);
        return BinarySearch2LEStrided(key, src, count, stride);
    }

    /// <summary>Floor index over 2-byte BE-stored keys with a strided layout.</summary>
    public static int Uniform2BEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride)
    {
        if (count == 0) return -1;
        if (stride == 2) return Uniform2BE(key, src, count);
        if (Enabled && Vector512.IsHardwareAccelerated && key.Length == 2 && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan16Strided(key, src, count, stride, isLittleEndian: false);
        return BinarySearchLexStrided(key, src, count, keySize: 2, stride);
    }

    /// <summary>Floor index over 4-byte LE-stored keys with a strided layout.</summary>
    public static int Uniform4LEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride)
    {
        if (count == 0) return -1;
        if (stride == 4) return Uniform4LE(key, src, count);
        if (Enabled && Vector512.IsHardwareAccelerated && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan32Strided(key, src, count, stride, isLittleEndian: true);
        return BinarySearch4LEStrided(key, src, count, stride);
    }

    /// <summary>Floor index over 4-byte BE-stored keys with a strided layout.</summary>
    public static int Uniform4BEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride)
    {
        if (count == 0) return -1;
        if (stride == 4) return Uniform4BE(key, src, count);
        if (Enabled && Vector512.IsHardwareAccelerated && key.Length == 4 && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan32Strided(key, src, count, stride, isLittleEndian: false);
        return BinarySearchLexStrided(key, src, count, keySize: 4, stride);
    }

    /// <summary>Floor index over 8-byte LE-stored keys with a strided layout.</summary>
    public static int Uniform8LEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride)
    {
        if (count == 0) return -1;
        if (stride == 8) return Uniform8LE(key, src, count);
        if (Enabled && Vector512.IsHardwareAccelerated && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan64Strided(key, src, count, stride, isLittleEndian: true);
        return BinarySearch8LEStrided(key, src, count, stride);
    }

    /// <summary>Floor index over 8-byte BE-stored keys with a strided layout.</summary>
    public static int Uniform8BEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride)
    {
        if (count == 0) return -1;
        if (stride == 8) return Uniform8BE(key, src, count);
        if (Enabled && Vector512.IsHardwareAccelerated && key.Length == 8 && count >= 2 && count <= LinearScanMaxCount)
            return FloorScan64Strided(key, src, count, stride, isLittleEndian: false);
        return BinarySearchLexStrided(key, src, count, keySize: 8, stride);
    }

    /// <summary>
    /// Strided floor index over BE-stored (lex-ordered) keys of arbitrary <paramref name="keySize"/>.
    /// </summary>
    public static int UniformBEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int keySize, int stride)
    {
        if (count == 0) return -1;
        return BinarySearchLexStrided(key, src, count, keySize, stride);
    }

    // =====================================================================================
    //  Lower-bound on 2-byte LE-stored keys (smallest i where keys[i] >= target; count if
    //  none). Different semantics from floor; used by HsstTwoByteSlotValue{,Large}Reader.
    // =====================================================================================

    /// <summary>
    /// Smallest <c>i</c> in <c>[0, count]</c> where the i-th LE-stored 2-byte key, interpreted
    /// as a BE-numeric <see cref="ushort"/>, is &gt;= <paramref name="targetBe"/>'s BE-numeric
    /// value. Returns <paramref name="count"/> when every stored key is less than the target.
    /// </summary>
    /// <param name="keys">LE-stored 2-byte keys, packed (<c>2 * count</c> bytes).</param>
    /// <param name="count">Number of stored keys.</param>
    /// <param name="targetBe">Target key in input (BE / lex) byte order; exactly 2 bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LowerBound2LE(ReadOnlySpan<byte> keys, int count, scoped ReadOnlySpan<byte> targetBe)
    {
        if (count == 0) return 0;

        ushort search = (ushort)((targetBe[0] << 8) | targetBe[1]);
        ref byte src = ref MemoryMarshal.GetReference(keys);
        int i = 0;

        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<ushort> searchVec = Vector512.Create(search);
            while (i + 32 <= count)
            {
                Vector512<ushort> lanes = Vector512.LoadUnsafe(ref src, (nuint)(i * 2)).AsUInt16();
                Vector512<ushort> ge = Vector512.GreaterThanOrEqual(lanes, searchVec);
                ulong mask = ge.ExtractMostSignificantBits();
                if (mask != 0)
                    return i + BitOperations.TrailingZeroCount(mask);
                i += 32;
            }
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            Vector256<ushort> searchVec = Vector256.Create(search);
            while (i + 16 <= count)
            {
                Vector256<ushort> lanes = Vector256.LoadUnsafe(ref src, (nuint)(i * 2)).AsUInt16();
                Vector256<ushort> ge = Vector256.GreaterThanOrEqual(lanes, searchVec);
                uint mask = ge.ExtractMostSignificantBits();
                if (mask != 0)
                    return i + BitOperations.TrailingZeroCount(mask);
                i += 16;
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<ushort> searchVec = Vector128.Create(search);
            while (i + 8 <= count)
            {
                Vector128<ushort> lanes = Vector128.LoadUnsafe(ref src, (nuint)(i * 2)).AsUInt16();
                Vector128<ushort> ge = Vector128.GreaterThanOrEqual(lanes, searchVec);
                uint mask = ge.ExtractMostSignificantBits();
                if (mask != 0)
                    return i + BitOperations.TrailingZeroCount(mask);
                i += 8;
            }
        }

        for (; i < count; i++)
        {
            ushort lane = BinaryPrimitives.ReadUInt16LittleEndian(keys.Slice(i * 2, 2));
            if (lane >= search) return i;
        }
        return count;
    }

    /// <summary>
    /// Read the i-th LE-stored 2-byte key as its BE-numeric <see cref="ushort"/> value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadKey2LE(ReadOnlySpan<byte> keys, int idx)
        => BinaryPrimitives.ReadUInt16LittleEndian(keys.Slice(idx * 2, 2));

    // =====================================================================================
    //  Storage equality helper (HsstPackedArrayReader).
    // =====================================================================================

    /// <summary>
    /// True iff the stored bytes encode the same lex key as <paramref name="key"/>. Equality
    /// requires same length; for LE-stored keys the stored bytes are the reverse of <paramref name="key"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool StorageEqualsLex(scoped ReadOnlySpan<byte> stored, scoped ReadOnlySpan<byte> key, bool isLittleEndian)
    {
        if (key.Length != stored.Length) return false;
        if (!isLittleEndian) return stored.SequenceEqual(key);
        for (int i = 0; i < stored.Length; i++)
            if (stored[i] != key[stored.Length - 1 - i]) return false;
        return true;
    }

    // =====================================================================================
    //  AVX-512 SIMD scan kernels (private; called from the per-size dispatchers above).
    // =====================================================================================

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
        return Avx512BW.IsSupported
            ? MaskedTail16(search, keys, i, count, isLittleEndian)
            : ScalarTail16(search, ref src, i, count, isLittleEndian);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan24Le(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        // Pack the first 3 search-key bytes into the low 24 bits of a uint, high byte zero —
        // matches the lane format produced by Vector512.Shuffle(raw, Pack24LeMask512).
        ref byte keyRef = ref MemoryMarshal.GetReference(key);
        uint search = Unsafe.ReadUnaligned<ushort>(ref keyRef)
                      | ((uint)Unsafe.Add(ref keyRef, 2) << 16);
        ref byte src = ref MemoryMarshal.GetReference(keys);

        Vector512<uint> searchVec = Vector512.Create(search);
        int i = 0;
        // Each iteration consumes 16 keys (48 bytes) but the unaligned vector load reads 64
        // bytes from offset i*3. Stop while that load still fits inside the keys span; the
        // scalar tail handles the (up to ~22) remaining keys without overrun.
        int keysLen = keys.Length;
        while (i + 16 <= count && i * 3 + 64 <= keysLen)
        {
            Vector512<byte> raw = Vector512.LoadUnsafe(ref src, (nuint)(i * 3));
            // vpermb: gather (3n, 3n+1, 3n+2) into each u32 lane; out-of-range index 0xFF
            // zeros the high byte for free, so no follow-up vpand is needed.
            Vector512<uint> lanes = Vector512.Shuffle(raw, Pack24LeMask512).AsUInt32();
            Vector512<uint> gt = Vector512.GreaterThan(lanes, searchVec);
            ulong mask = gt.ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask);
                return i + firstGtLane - 1;
            }
            i += 16;
        }
        return ScalarTail24Le(search, ref src, i, count);
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
        return Avx512F.IsSupported
            ? MaskedTail32(search, keys, i, count, isLittleEndian)
            : ScalarTail32(search, ref src, i, count, isLittleEndian);
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
        return Avx512F.IsSupported
            ? MaskedTail64(search, keys, i, count, isLittleEndian)
            : ScalarTail64(search, ref src, i, count, isLittleEndian);
    }

    // ---- Strided SIMD kernels ----
    //
    // Strided variants gather lanes from interleaved slots via per-lane scalar loads. AVX-512
    // has no efficient general gather for arbitrary 4/8-byte strides, but a single
    // Vector512.GreaterThan over the assembled lanes still amortises well at small counts —
    // the win comes from removing the branch mispredicts of binary search.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan16Strided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride, bool isLittleEndian)
    {
        ushort search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(key)));
        ref byte s = ref MemoryMarshal.GetReference(src);
        Vector512<ushort> searchVec = Vector512.Create(search);

        int i = 0;
        Span<ushort> lanes = stackalloc ushort[32];
        while (i + 32 <= count)
        {
            for (int j = 0; j < 32; j++)
            {
                ushort raw = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, (nint)((i + j) * stride)));
                lanes[j] = isLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            }
            Vector512<ushort> v = Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(lanes));
            Vector512<ushort> gt = Vector512.GreaterThan(v, searchVec);
            ulong mask = gt.ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask);
                return i + firstGtLane - 1;
            }
            i += 32;
        }
        return ScalarTail16Strided(search, ref s, i, count, stride, isLittleEndian);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan32Strided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride, bool isLittleEndian)
    {
        uint search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(key)));
        ref byte s = ref MemoryMarshal.GetReference(src);
        Vector512<uint> searchVec = Vector512.Create(search);

        int i = 0;
        Span<uint> lanes = stackalloc uint[16];
        while (i + 16 <= count)
        {
            for (int j = 0; j < 16; j++)
            {
                uint raw = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, (nint)((i + j) * stride)));
                lanes[j] = isLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            }
            Vector512<uint> v = Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(lanes));
            Vector512<uint> gt = Vector512.GreaterThan(v, searchVec);
            ulong mask = gt.ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask);
                return i + firstGtLane - 1;
            }
            i += 16;
        }
        return ScalarTail32Strided(search, ref s, i, count, stride, isLittleEndian);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorScan64Strided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride, bool isLittleEndian)
    {
        ulong search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(key)));
        ref byte s = ref MemoryMarshal.GetReference(src);
        Vector512<ulong> searchVec = Vector512.Create(search);

        int i = 0;
        Span<ulong> lanes = stackalloc ulong[8];
        while (i + 8 <= count)
        {
            for (int j = 0; j < 8; j++)
            {
                ulong raw = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, (nint)((i + j) * stride)));
                lanes[j] = isLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            }
            Vector512<ulong> v = Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(lanes));
            Vector512<ulong> gt = Vector512.GreaterThan(v, searchVec);
            ulong mask = gt.ExtractMostSignificantBits();
            if (mask != 0)
            {
                int firstGtLane = BitOperations.TrailingZeroCount(mask);
                return i + firstGtLane - 1;
            }
            i += 8;
        }
        return ScalarTail64Strided(search, ref s, i, count, stride, isLittleEndian);
    }

    // ---- AVX-512 masked-load tails (private; replace the scalar tail when Avx512{BW,F}
    //      is supported). Hardware masked load (vmovdqu16/32/64 zmm{k}{z}) reads only
    //      the lanes selected by the mask, so no padding past `count` is required.
    //      Lanes outside the mask are zeroed and therefore never compare greater under
    //      unsigned GT — no explicit mask of the gt-result is needed. ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int MaskedTail16(ushort search, ReadOnlySpan<byte> keys, int i, int count, bool isLittleEndian)
    {
        int remaining = count - i;
        if (remaining == 0) return count - 1;
        Vector512<ushort> mask = Vector512.LessThan(LaneIdx16, Vector512.Create((ushort)remaining));
        // `fixed` pins for the duration of the masked load — callers pass arbitrary
        // spans (ArrayPool buffers, mmap'd FlatDB pages), so Unsafe.AsPointer would be GC-unsafe.
        fixed (byte* p = keys)
        {
            Vector512<ushort> raw = Avx512BW.MaskLoad((ushort*)(p + i * 2), mask, Vector512<ushort>.Zero);
            Vector512<ushort> lanes = isLittleEndian
                ? raw
                : Vector512.Shuffle(raw.AsByte(), ByteSwap16Mask512).AsUInt16();
            ulong gtMask = Vector512.GreaterThan(lanes, Vector512.Create(search)).ExtractMostSignificantBits();
            if (gtMask != 0) return i + BitOperations.TrailingZeroCount(gtMask) - 1;
        }
        return count - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int MaskedTail32(uint search, ReadOnlySpan<byte> keys, int i, int count, bool isLittleEndian)
    {
        int remaining = count - i;
        if (remaining == 0) return count - 1;
        Vector512<uint> mask = Vector512.LessThan(LaneIdx32, Vector512.Create((uint)remaining));
        fixed (byte* p = keys)
        {
            Vector512<uint> raw = Avx512F.MaskLoad((uint*)(p + i * 4), mask, Vector512<uint>.Zero);
            Vector512<uint> lanes = isLittleEndian
                ? raw
                : Vector512.Shuffle(raw.AsByte(), ByteSwap32Mask512).AsUInt32();
            ulong gtMask = Vector512.GreaterThan(lanes, Vector512.Create(search)).ExtractMostSignificantBits();
            if (gtMask != 0) return i + BitOperations.TrailingZeroCount(gtMask) - 1;
        }
        return count - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int MaskedTail64(ulong search, ReadOnlySpan<byte> keys, int i, int count, bool isLittleEndian)
    {
        int remaining = count - i;
        if (remaining == 0) return count - 1;
        Vector512<ulong> mask = Vector512.LessThan(LaneIdx64, Vector512.Create((ulong)remaining));
        fixed (byte* p = keys)
        {
            Vector512<ulong> raw = Avx512F.MaskLoad((ulong*)(p + i * 8), mask, Vector512<ulong>.Zero);
            Vector512<ulong> lanes = isLittleEndian
                ? raw
                : Vector512.Shuffle(raw.AsByte(), ByteSwap64Mask512).AsUInt64();
            ulong gtMask = Vector512.GreaterThan(lanes, Vector512.Create(search)).ExtractMostSignificantBits();
            if (gtMask != 0) return i + BitOperations.TrailingZeroCount(gtMask) - 1;
        }
        return count - 1;
    }

    // ---- Scalar tails (private; finish the SIMD scan over the leftover < 32/16/8 keys). ----

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
    private static int ScalarTail24Le(uint search, ref byte src, int i, int count)
    {
        for (; i < count; i++)
        {
            ref byte slot = ref Unsafe.Add(ref src, (nint)(i * 3));
            uint k = Unsafe.ReadUnaligned<ushort>(ref slot)
                     | ((uint)Unsafe.Add(ref slot, 2) << 16);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarTail16Strided(ushort search, ref byte s, int i, int count, int stride, bool isLittleEndian)
    {
        for (; i < count; i++)
        {
            ushort raw = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, (nint)(i * stride)));
            ushort k = isLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            if (k > search) return i - 1;
        }
        return count - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarTail32Strided(uint search, ref byte s, int i, int count, int stride, bool isLittleEndian)
    {
        for (; i < count; i++)
        {
            uint raw = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, (nint)(i * stride)));
            uint k = isLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            if (k > search) return i - 1;
        }
        return count - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarTail64Strided(ulong search, ref byte s, int i, int count, int stride, bool isLittleEndian)
    {
        for (; i < count; i++)
        {
            ulong raw = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, (nint)(i * stride)));
            ulong k = isLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            if (k > search) return i - 1;
        }
        return count - 1;
    }

    // =====================================================================================
    //  Scalar binary-search fallbacks (private). LE-stored variants use direct unsigned
    //  integer compare on the native LE-load value, which equals the BE-numeric value of
    //  the original lex key. BE-stored variants use lex SequenceCompareTo.
    // =====================================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearch2LE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        ushort search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(key)));
        ref byte src = ref MemoryMarshal.GetReference(keys);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ushort midKey = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, (nint)(mid * 2)));
            if (search >= midKey) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearch3LE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        ref byte keyRef = ref MemoryMarshal.GetReference(key);
        uint search = Unsafe.ReadUnaligned<ushort>(ref keyRef)
                      | ((uint)Unsafe.Add(ref keyRef, 2) << 16);
        ref byte src = ref MemoryMarshal.GetReference(keys);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ref byte slot = ref Unsafe.Add(ref src, (nint)(mid * 3));
            uint midKey = Unsafe.ReadUnaligned<ushort>(ref slot)
                          | ((uint)Unsafe.Add(ref slot, 2) << 16);
            if (search >= midKey) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearch4LE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        uint search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(key)));
        ref byte src = ref MemoryMarshal.GetReference(keys);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            uint midKey = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, (nint)(mid * 4)));
            if (search >= midKey) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearch8LE(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        ulong search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(key)));
        ref byte src = ref MemoryMarshal.GetReference(keys);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ulong midKey = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, (nint)(mid * 8)));
            if (search >= midKey) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearch2LEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride)
    {
        ushort search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(key)));
        ref byte s = ref MemoryMarshal.GetReference(src);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ushort midKey = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, (nint)(mid * stride)));
            if (search >= midKey) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearch4LEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride)
    {
        uint search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(key)));
        ref byte s = ref MemoryMarshal.GetReference(src);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            uint midKey = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, (nint)(mid * stride)));
            if (search >= midKey) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearch8LEStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int stride)
    {
        ulong search = BinaryPrimitives.ReverseEndianness(
            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(key)));
        ref byte s = ref MemoryMarshal.GetReference(src);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ulong midKey = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, (nint)(mid * stride)));
            if (search >= midKey) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearchLex(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int keySize)
    {
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ReadOnlySpan<byte> midKey = keys.Slice(mid * keySize, keySize);
            int cmp = key.SequenceCompareTo(midKey);
            if (cmp >= 0) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearchLexStrided(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int count, int keySize, int stride)
    {
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ReadOnlySpan<byte> midKey = src.Slice(mid * stride, keySize);
            int cmp = key.SequenceCompareTo(midKey);
            if (cmp >= 0) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

}
