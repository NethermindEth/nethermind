// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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

    static BN254()
    {
        if (mclBn_init(MCL_BN_SNARK1, MCLBN_COMPILED_TIME_VAR) != 0)
            throw new InvalidOperationException("MCL initialization failed");
    }

    internal static bool Add(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length != 128)
            return false;

        mclBnG1 x;
        mclBnG1 y;
        fixed (byte* data = &MemoryMarshal.GetReference(input))
        {
            if (!DeserializeG1(data, out x))
                return false;

            if (!DeserializeG1(data + 64, out y))
                return false;
        }

        mclBnG1_add(ref x, x, y); // x += y
        mclBnG1_normalize(ref x, x);

        return SerializeG1(x, output);
    }

    internal static bool Mul(Span<byte> input, Span<byte> output)
    {
        if (input.Length != 96)
            return false;

        mclBnG1 x;
        mclBnFr y = default;
        fixed (byte* data = &MemoryMarshal.GetReference(input))
        {
            if (!DeserializeG1(data, out x))
                return false;

            CopyReverse32((data + 64), (data + 64)); // To little-endian

            if (mclBnFr_setLittleEndianMod(ref y, (nint)(data + 64), 32) == -1 || mclBnFr_isValid(y) == 0)
                return false;
        }

        mclBnG1_mul(ref x, x, y);  // x *= y
        mclBnG1_normalize(ref x, x);

        return SerializeG1(x, output);
    }

    internal static bool CheckPairing(ReadOnlySpan<byte> input, Span<byte> output)
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

        mclBnGT acc = default;
        bool hasMl = false;

        fixed (byte* data = &MemoryMarshal.GetReference(input))
        {
            for (int i = 0; i < input.Length; i += PairSize)
            {
                if (!DeserializeG1(data + i, out mclBnG1 g1))
                    return false;

                if (!DeserializeG2(data + i + 64, out mclBnG2 g2))
                    return false;

                // Skip explicit neutral pairs
                if (mclBnG1_isZero(g1) == 1 || mclBnG2_isZero(g2) == 1)
                    continue;

                mclBnGT ml = default;
                mclBn_millerLoop(ref ml, g1, g2); // Miller loop only

                if (hasMl)
                {
                    mclBnGT_mul(ref acc, acc, ml);
                }
                else
                {
                    acc = ml;
                    hasMl = true;
                }
            }
        }

        // No effective pairs -> valid
        if (!hasMl)
        {
            output[31] = 1;
            return true;
        }

        // Single final exponentiation for the product
        mclBn_finalExp(ref acc, acc);

        // True if the product of pairings equals 1 in GT
        output[31] = (byte)(mclBnGT_isOne(acc) == 1 ? 1 : 0);
        return true;
    }

    [SkipLocalsInit]
    private static bool DeserializeG1(byte* data, out mclBnG1 point)
    {
        point = default;

        // Treat all-zero as point at infinity for your calling convention
        if (IsZero64(data))
            return true;

        // Input is big-endian; MCL call below expects little-endian byte order for Fp
        Span<byte> tmp = stackalloc byte[32];
        fixed (byte* p = &MemoryMarshal.GetReference(tmp))
        {
            // x
            CopyReverse32(data, p);
            if (mclBnFp_deserialize(ref point.x, (nint)p, 32) == nuint.Zero)
                return false;
            // y
            CopyReverse32((data + 32), p);
            if (mclBnFp_deserialize(ref point.y, (nint)p, 32) == nuint.Zero)
                return false;
        }

        mclBnFp_setInt32(ref point.z, 1);
        return mclBnG1_isValid(point) == 1;
    }

    [SkipLocalsInit]
    private static bool DeserializeG2(byte* data, out mclBnG2 point)
    {
        point = default;

        // Treat all-zero as point at infinity
        if (IsZero128(data))
            return true;

        // Input layout: x_im, x_re, y_im, y_re (each 32 bytes, big-endian)
        // MCL Fp2 layout: d0 = re, d1 = im
        Span<byte> tmp = stackalloc byte[32];
        fixed (byte* p = &MemoryMarshal.GetReference(tmp))
        {
            // x.im
            CopyReverse32(data, p);
            if (mclBnFp_deserialize(ref point.x.d1, (nint)p, 32) == nuint.Zero)
                return false;

            // x.re
            CopyReverse32((data + 32), p);
            if (mclBnFp_deserialize(ref point.x.d0, (nint)p, 32) == nuint.Zero)
                return false;

            // y.im
            CopyReverse32((data + 64), p);
            if (mclBnFp_deserialize(ref point.y.d1, (nint)p, 32) == nuint.Zero)
                return false;

            // y.re
            CopyReverse32((data + 96), p);
            if (mclBnFp_deserialize(ref point.y.d0, (nint)p, 32) == nuint.Zero)
                return false;
        }

        mclBnFp_setInt32(ref point.z.d0, 1);

        return mclBnG2_isValid(point) == 1 && mclBnG2_isValidOrder(point) == 1;
    }

    private static bool SerializeG1(in mclBnG1 point, Span<byte> output)
    {
        fixed (byte* ptr = &MemoryMarshal.GetReference(output))
        {
            if (mclBnFp_getLittleEndian((nint)ptr, 32, point.x) == nuint.Zero)
                return false;

            if (mclBnFp_getLittleEndian((nint)ptr + 32, 32, point.y) == nuint.Zero)
                return false;

            CopyReverse32(ptr, ptr); // To big-endian
            CopyReverse32(ptr + 32, ptr + 32); // To big-endian
        }

        return true;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool IsZero64(byte* ptr)
    {
        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<byte> a = Unsafe.ReadUnaligned<Vector512<byte>>(ptr);
            return a == default;
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> a = Unsafe.ReadUnaligned<Vector256<byte>>(ptr + 0);
            Vector256<byte> b = Unsafe.ReadUnaligned<Vector256<byte>>(ptr + 32);
            Vector256<byte> o = Vector256.BitwiseOr(a, b);
            return o == default;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            // 4x16-byte blocks, coalesced in pairs
            for (nuint offset = 0; offset < 64; offset += 32)
            {
                Vector128<byte> a = Unsafe.ReadUnaligned<Vector128<byte>>(ptr + offset);
                Vector128<byte> b = Unsafe.ReadUnaligned<Vector128<byte>>(ptr + offset + 16);
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

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool IsZero128(byte* ptr)
    {
        if (Vector512.IsHardwareAccelerated)
        {
            // 2x512 -> OR‑reduce -> EqualsAll
            Vector512<byte> a = Unsafe.ReadUnaligned<Vector512<byte>>(ptr + 0);
            Vector512<byte> b = Unsafe.ReadUnaligned<Vector512<byte>>(ptr + 64);
            Vector512<byte> o = Vector512.BitwiseOr(a, b);
            return o == default;
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            // 4x32-byte blocks, coalesced in pairs (2 loads per iteration)
            for (nuint offset = 0; offset < 128; offset += 64)
            {
                Vector256<byte> a = Unsafe.ReadUnaligned<Vector256<byte>>(ptr + offset);
                Vector256<byte> b = Unsafe.ReadUnaligned<Vector256<byte>>(ptr + offset + 32);
                Vector256<byte> o = Vector256.BitwiseOr(a, b);
                if (o != default) return false;
            }
            return true;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            // 8x16-byte blocks, coalesced in pairs
            for (nuint offset = 0; offset < 128; offset += 32)
            {
                Vector128<byte> a = Unsafe.ReadUnaligned<Vector128<byte>>(ptr + offset);
                Vector128<byte> b = Unsafe.ReadUnaligned<Vector128<byte>>(ptr + offset + 16);
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

        // Build reverse mask once — [31,30,...,0]
        Vector256<byte> mask = Vector256.Create(
            (byte)31, (byte)30, (byte)29, (byte)28,
            (byte)27, (byte)26, (byte)25, (byte)24,
            (byte)23, (byte)22, (byte)21, (byte)20,
            (byte)19, (byte)18, (byte)17, (byte)16,
            (byte)15, (byte)14, (byte)13, (byte)12,
            (byte)11, (byte)10, (byte)9, (byte)8,
            (byte)7, (byte)6, (byte)5, (byte)4,
            (byte)3, (byte)2, (byte)1, (byte)0);

        Vector256<byte> revInLane = Avx2.Shuffle(vec, mask);
        Vector256<byte> fullRev = Avx2.Permute2x128(revInLane, revInLane, 0x01);
        Unsafe.WriteUnaligned(dstRef, fullRev);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reverse32Bytes128(byte* srcRef, byte* dstRef)
    {
        // Two 16-byte halves: reverse each then swap them
        Vector128<byte> lo = Unsafe.ReadUnaligned<Vector128<byte>>(srcRef);
        Vector128<byte> hi = Unsafe.ReadUnaligned<Vector128<byte>>(srcRef + 16);

        lo = Vector128.Shuffle(lo, Vector128.Create(
            (byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0));
        hi = Vector128.Shuffle(hi, Vector128.Create(
            (byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0));

        // Store swapped halves reversed
        Unsafe.WriteUnaligned(dstRef, hi);
        Unsafe.WriteUnaligned(dstRef + 16, lo);
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
