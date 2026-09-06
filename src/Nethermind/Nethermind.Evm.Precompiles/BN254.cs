// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.MclBindings;

namespace Nethermind.Evm.Precompiles;

using static Mcl;

[SkipLocalsInit]
internal static unsafe class BN254
{
    internal const int PairSize = 192;
    private const int MaxStackPairCount = 32;

    static BN254()
    {
        if (mclBn_init(MCL_BN_SNARK1, MCLBN_COMPILED_TIME_VAR) != 0)
            throw new InvalidOperationException("MCL initialization failed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Add(byte[] output, ReadOnlySpan<byte> input)
    {
        const int chunkSize = 64;

        Debug.Assert(input.Length == 128);
        Debug.Assert(output.Length == 64);

        fixed (byte* data = &MemoryMarshal.GetReference(input))
        {
            if (!DeserializeG1(data, out mclBnG1 x, out _))
                return false;

            if (!DeserializeG1(data + chunkSize, out mclBnG1 y, out _))
                return false;

            mclBnG1_add(ref x, x, y); // x += y
            mclBnG1_normalize(ref x, x);

            return SerializeG1(x, output);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Mul(byte[] output, ReadOnlySpan<byte> input)
    {
        const int chunkSize = 64;

        Debug.Assert(input.Length == 96);
        Debug.Assert(output.Length == 64);

        fixed (byte* data = &MemoryMarshal.GetReference(input))
        {
            if (!DeserializeG1(data, out mclBnG1 x, out _))
                return false;

            Unsafe.SkipInit(out mclBnFr y);
            if (mclBnFr_setBigEndianMod(ref y, (nint)data + chunkSize, 32) == -1 || mclBnFr_isValid(y) == 0)
                return false;

            mclBnG1_mul(ref x, x, y);  // x *= y
            mclBnG1_normalize(ref x, x);
            return SerializeG1(x, output);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool CheckPairing(byte[] output, ReadOnlySpan<byte> input)
    {
        if (output.Length < 32)
            return false;

        // Empty input means "true" by convention
        if (input.Length == 0)
        {
            output[31] = 1;
            return true;
        }

        if (input.Length % PairSize != 0)
            return false;

        int pairCount = input.Length / PairSize;

        fixed (byte* data = &MemoryMarshal.GetReference(input))
        {
            return pairCount switch
            {
                1 => CheckPairingSingle(output, data),
                _ => CheckPairingVector(output, data, pairCount),
            };
        }
    }

    private static bool CheckPairingSingle(byte[] output, byte* data)
    {
        if (!DeserializeG1(data, out mclBnG1 g1, out bool g1IsZero))
            return false;

        if (!DeserializeG2(data + 64, out mclBnG2 g2, out bool g2IsZero))
            return false;

        if (g1IsZero || g2IsZero)
        {
            output[31] = 1;
            return true;
        }

        Unsafe.SkipInit(out mclBnGT acc);
        mclBn_millerLoop(ref acc, g1, g2);
        mclBn_finalExp(ref acc, acc);

        output[31] = Convert.ToByte(mclBnGT_isOne(acc) == 1);
        return true;
    }

    private static bool CheckPairingVector(byte[] output, byte* data, int pairCount)
    {
        // Process the pairs in chunks of at most MaxStackPairCount so the scratch buffers stay a fixed,
        // input-independent size on the stack (the >MaxStackPairCount case never grows the allocation),
        // while still feeding the vectorized multi-Miller-loop for every pair rather than falling back to
        // a per-pair scalar loop. Each chunk's Miller-loop product is multiplied into a running GT
        // accumulator and a single final exponentiation is applied at the end — finalExp(∏ ML) is invariant
        // to how the product is batched.
        // Allocate in bytes so the buffer size matches the write stride (sizeof) exactly, regardless of struct padding.
        int chunkCapacity = Math.Min(pairCount, MaxStackPairCount);
        byte* g1Bytes = stackalloc byte[chunkCapacity * sizeof(mclBnG1)];
        byte* g2Bytes = stackalloc byte[chunkCapacity * sizeof(mclBnG2)];

        Unsafe.SkipInit(out mclBnGT ml);
        Unsafe.SkipInit(out mclBnGT acc);
        bool hasMl = false;

        for (int chunkStart = 0; chunkStart < pairCount; chunkStart += MaxStackPairCount)
        {
            int chunkEnd = Math.Min(chunkStart + MaxStackPairCount, pairCount);
            int nonZeroInChunk = 0;

            for (int i = chunkStart; i < chunkEnd; i++)
            {
                int inputOffset = i * PairSize;

                if (!DeserializeG1(data + inputOffset, out mclBnG1 g1, out bool g1IsZero))
                    return false;

                if (!DeserializeG2(data + inputOffset + 64, out mclBnG2 g2, out bool g2IsZero))
                    return false;

                if (g1IsZero || g2IsZero)
                    continue;

                Unsafe.AsRef<mclBnG1>(g1Bytes + nonZeroInChunk * sizeof(mclBnG1)) = g1;
                Unsafe.AsRef<mclBnG2>(g2Bytes + nonZeroInChunk * sizeof(mclBnG2)) = g2;
                nonZeroInChunk++;
            }

            if (nonZeroInChunk == 0)
                continue;

            mclBn_millerLoopVec(
                ref hasMl ? ref ml : ref acc,
                in Unsafe.AsRef<mclBnG1>(g1Bytes),
                in Unsafe.AsRef<mclBnG2>(g2Bytes),
                (nuint)nonZeroInChunk);

            if (hasMl)
            {
                mclBnGT_mul(ref acc, acc, ml);
            }
            else
            {
                hasMl = true;
            }
        }

        // All pairs had a zero element -> valid
        if (!hasMl)
        {
            output[31] = 1;
            return true;
        }

        mclBn_finalExp(ref acc, acc);

        output[31] = Convert.ToByte(mclBnGT_isOne(acc) == 1);
        return true;
    }

    private static bool DeserializeG1(byte* data, out mclBnG1 point, out bool isZero)
    {
        const int chunkSize = 32;

        point = default;
        isZero = IsZero64(data);

        // Treat all-zero as point at infinity for your calling convention
        if (isZero)
        {
            return true;
        }

        // Input is big-endian; MCL call below expects little-endian byte order for Fp
        byte* tmp = stackalloc byte[chunkSize];

        // x
        CopyReverse32(data, tmp);
        if (mclBnFp_deserialize(ref point.x, (nint)tmp, chunkSize) == nuint.Zero)
            return false;
        // y
        CopyReverse32(data + chunkSize, tmp);
        if (mclBnFp_deserialize(ref point.y, (nint)tmp, chunkSize) == nuint.Zero)
            return false;

        mclBnFp_setInt32(ref point.z, 1);
        return mclBnG1_isValid(point) == 1;
    }

    private static bool DeserializeG2(byte* data, out mclBnG2 point, out bool isZero)
    {
        const int chunkSize = 32;

        point = default;
        isZero = IsZero128(data);

        // Treat all-zero as point at infinity
        if (isZero)
        {
            return true;
        }

        // Input layout: x_im, x_re, y_im, y_re (each 32 bytes, big-endian)
        // MCL Fp2 layout: d0 = re, d1 = im
        byte* tmp = stackalloc byte[chunkSize];

        // x.im
        CopyReverse32(data, tmp);
        if (mclBnFp_deserialize(ref point.x.d1, (nint)tmp, chunkSize) == nuint.Zero)
            return false;

        // x.re
        CopyReverse32(data + chunkSize, tmp);
        if (mclBnFp_deserialize(ref point.x.d0, (nint)tmp, chunkSize) == nuint.Zero)
            return false;

        // y.im
        CopyReverse32(data + chunkSize * 2, tmp);
        if (mclBnFp_deserialize(ref point.y.d1, (nint)tmp, chunkSize) == nuint.Zero)
            return false;

        // y.re
        CopyReverse32(data + chunkSize * 3, tmp);
        if (mclBnFp_deserialize(ref point.y.d0, (nint)tmp, chunkSize) == nuint.Zero)
            return false;

        mclBnFp_setInt32(ref point.z.d0, 1);

        return mclBnG2_isValid(point) == 1 && mclBnG2_isValidOrder(point) == 1;
    }

    private static bool SerializeG1(in mclBnG1 point, byte[] output)
    {
        const int chunkSize = 32;

        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(output))
        {
            if (mclBnFp_getLittleEndian((nint)ptr, chunkSize, point.x) == nuint.Zero)
                return false;

            if (mclBnFp_getLittleEndian((nint)ptr + chunkSize, chunkSize, point.y) == nuint.Zero)
                return false;

            CopyReverse32(ptr, ptr); // To big-endian
            CopyReverse32(ptr + chunkSize, ptr + chunkSize); // To big-endian
        }

        return true;
    }

    private static unsafe bool IsZero64(byte* ptr)
    {
        const int Length = 64;

        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<byte> a = Unsafe.ReadUnaligned<Vector512<byte>>(ptr);
            return a == default;
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> a = Unsafe.ReadUnaligned<Vector256<byte>>(ptr);
            Vector256<byte> b = Unsafe.ReadUnaligned<Vector256<byte>>(ptr + Vector256<byte>.Count);
            Vector256<byte> o = Vector256.BitwiseOr(a, b);
            return o == default;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            // 4x16-byte blocks, coalesced in pairs
            for (nuint offset = 0; offset < Length; offset += (nuint)Vector128<byte>.Count * 2)
            {
                Vector128<byte> a = Unsafe.ReadUnaligned<Vector128<byte>>(ptr + offset);
                Vector128<byte> b = Unsafe.ReadUnaligned<Vector128<byte>>(ptr + offset + Vector128<byte>.Count);
                Vector128<byte> o = Vector128.BitwiseOr(a, b);
                if (o != default) return false;
            }
            return true;
        }
        else
        {
            // scalar fallback
            ulong* x = (ulong*)ptr;
            for (int i = 0; i < 8; i++)
            {
                if (x[i] != 0)
                    return false;
            }
            return true;
        }
    }

    private static unsafe bool IsZero128(byte* ptr)
    {
        const int Length = 128;

        if (Vector512.IsHardwareAccelerated)
        {
            // 2x512 -> OR‑reduce -> EqualsAll
            Vector512<byte> a = Unsafe.ReadUnaligned<Vector512<byte>>(ptr + 0);
            Vector512<byte> b = Unsafe.ReadUnaligned<Vector512<byte>>(ptr + Vector512<byte>.Count);
            Vector512<byte> o = Vector512.BitwiseOr(a, b);
            return o == default;
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            // 4x32-byte blocks, coalesced in pairs (2 loads per iteration)
            for (nuint offset = 0; offset < Length; offset += (nuint)Vector256<byte>.Count * 2)
            {
                Vector256<byte> a = Unsafe.ReadUnaligned<Vector256<byte>>(ptr + offset);
                Vector256<byte> b = Unsafe.ReadUnaligned<Vector256<byte>>(ptr + offset + Vector256<byte>.Count);
                Vector256<byte> o = Vector256.BitwiseOr(a, b);
                if (o != default) return false;
            }
            return true;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            // 8x16-byte blocks, coalesced in pairs
            for (nuint offset = 0; offset < Length; offset += (nuint)Vector128<byte>.Count * 2)
            {
                Vector128<byte> a = Unsafe.ReadUnaligned<Vector128<byte>>(ptr + offset);
                Vector128<byte> b = Unsafe.ReadUnaligned<Vector128<byte>>(ptr + offset + Vector128<byte>.Count);
                Vector128<byte> o = Vector128.BitwiseOr(a, b);
                if (o != default) return false;
            }
            return true;
        }
        else
        {
            // scalar fallback
            ulong* x = (ulong*)ptr;
            for (int i = 0; i < 16; i++)
            {
                if (x[i] != 0)
                    return false;
            }
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyReverse32(byte* srcRef, byte* dstRef)
    {
        if (Avx2.IsSupported)
        {
            Reverse32BytesAvx2(srcRef, dstRef);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Reverse32Bytes128(srcRef, dstRef);
        }
        else
        {
            // Fallback scalar path
            Reverse32BytesScalar(srcRef, dstRef);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reverse32BytesAvx2(byte* srcRef, byte* dstRef)
    {
        // Load 32 bytes as one 256-bit vector
        Vector256<byte> vec = Unsafe.ReadUnaligned<Vector256<byte>>(srcRef);
        Vector256<byte> fullRev;

        Vector256<byte> mask = Vector256.Create((byte)31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0);
        if (Avx512Vbmi.VL.IsSupported)
        {
            fullRev = Avx512Vbmi.VL.PermuteVar32x8(vec, mask);
        }
        else
        {
            Vector256<byte> revInLane = Avx2.Shuffle(vec, mask);
            fullRev = Avx2.Permute2x128(revInLane, revInLane, 0x01);
        }

        Unsafe.WriteUnaligned(dstRef, fullRev);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reverse32Bytes128(byte* srcRef, byte* dstRef)
    {
        // Two 16-byte halves: reverse each then swap them
        Vector128<byte> lo = Unsafe.ReadUnaligned<Vector128<byte>>(srcRef);
        Vector128<byte> hi = Unsafe.ReadUnaligned<Vector128<byte>>(srcRef + Vector128<byte>.Count);

        Vector128<byte> indices = Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0);
        lo = Vector128.Shuffle(lo, indices);
        hi = Vector128.Shuffle(hi, indices);

        // Store swapped halves reversed
        Unsafe.WriteUnaligned(dstRef, hi);
        Unsafe.WriteUnaligned(dstRef + Vector128<byte>.Count, lo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reverse32BytesScalar(byte* srcRef, byte* dstRef)
    {
        ulong* src = (ulong*)srcRef;
        ulong* dst = (ulong*)dstRef;

        ulong a = BinaryPrimitives.ReverseEndianness(src[0]);
        ulong b = BinaryPrimitives.ReverseEndianness(src[1]);
        ulong c = BinaryPrimitives.ReverseEndianness(src[2]);
        ulong d = BinaryPrimitives.ReverseEndianness(src[3]);

        dst[0] = d;
        dst[1] = c;
        dst[2] = b;
        dst[3] = a;
    }
}
