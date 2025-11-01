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

    static BN254()
    {
        if (mclBn_init(MCL_BN_SNARK1, MCLBN_COMPILED_TIME_VAR) != 0)
            throw new InvalidOperationException("MCL initialization failed");
    }

    internal static bool Add(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length != 128)
            return false;

        if (!DeserializeG1(input[0..64], out mclBnG1 x))
            return false;

        if (!DeserializeG1(input[64..128], out mclBnG1 y))
            return false;

        mclBnG1_add(ref x, x, y); // x += y
        mclBnG1_normalize(ref x, x);

        return SerializeG1(x, output);
    }

    internal static bool Mul(Span<byte> input, Span<byte> output)
    {
        if (input.Length != 96)
            return false;

        if (!DeserializeG1(input[0..64], out mclBnG1 x))
            return false;

        Span<byte> yData = input[64..];
        yData.Reverse(); // To little-endian

        mclBnFr y = default;

        fixed (byte* ptr = &MemoryMarshal.GetReference(yData))
        {
            if (mclBnFr_setLittleEndianMod(ref y, (nint)ptr, 32) == -1 || mclBnFr_isValid(y) == 0)
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

        for (int i = 0; i < input.Length; i += PairSize)
        {
            int i64 = i + 64;

            if (!DeserializeG1(input[i..i64], out mclBnG1 g1))
                return false;

            if (!DeserializeG2(input[i64..(i64 + 128)], out mclBnG2 g2))
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
    private static bool DeserializeG1(ReadOnlySpan<byte> data, out mclBnG1 point)
    {
        point = default;

        // Treat all-zero as point at infinity for your calling convention
        if (data.IndexOfAnyExcept((byte)0) == -1)
            return true;

        // Input is big-endian; MCL call below expects little-endian byte order for Fp
        Span<byte> tmp = stackalloc byte[32];
        fixed (byte* p = &MemoryMarshal.GetReference(tmp))
        {
            // x
            CopyReverse32(data.Slice(0, 32), tmp);
            if (mclBnFp_deserialize(ref point.x, (nint)p, 32) == nuint.Zero)
                return false;
            // y
            CopyReverse32(data.Slice(32, 32), tmp);
            if (mclBnFp_deserialize(ref point.y, (nint)p, 32) == nuint.Zero)
                return false;
        }

        mclBnFp_setInt32(ref point.z, 1);
        return mclBnG1_isValid(point) == 1;
    }

    [SkipLocalsInit]
    private static bool DeserializeG2(ReadOnlySpan<byte> data, out mclBnG2 point)
    {
        point = default;

        // Treat all-zero as point at infinity
        if (data.IndexOfAnyExcept((byte)0) == -1)
            return true;

        // Input layout: x_im, x_re, y_im, y_re (each 32 bytes, big-endian)
        // MCL Fp2 layout: d0 = re, d1 = im
        Span<byte> tmp = stackalloc byte[32];
        fixed (byte* p = &MemoryMarshal.GetReference(tmp))
        {
            // x.re
            CopyReverse32(data.Slice(32, 32), tmp);
            if (mclBnFp_deserialize(ref point.x.d0, (nint)p, 32) == nuint.Zero)
                return false;

            // x.im
            CopyReverse32(data.Slice(0, 32), tmp);
            if (mclBnFp_deserialize(ref point.x.d1, (nint)p, 32) == nuint.Zero)
                return false;

            // y.re
            CopyReverse32(data.Slice(96, 32), tmp);
            if (mclBnFp_deserialize(ref point.y.d0, (nint)p, 32) == nuint.Zero)
                return false;

            // y.im
            CopyReverse32(data.Slice(64, 32), tmp);
            if (mclBnFp_deserialize(ref point.y.d1, (nint)p, 32) == nuint.Zero)
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

            new Span<byte>(ptr, 32).Reverse(); // To big-endian
            new Span<byte>(ptr + 32, 32).Reverse(); // To big-endian
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void CopyReverse32(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Debug.Assert(src.Length == 32);
        Debug.Assert(dst.Length == 32);

        if (Avx2.IsSupported)
        {
            Reverse32BytesAvx2(src, dst);
        }
        else
        if (Sse2.IsSupported && Ssse3.IsSupported)
        {
            Reverse32BytesSse2(src, dst);
        }
        else
        {
            // Fallback scalar path
            Reverse32BytesScalar(src, dst);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Reverse32BytesAvx2(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        ref byte srcRef = ref MemoryMarshal.GetReference(src);
        ref byte dstRef = ref MemoryMarshal.GetReference(dst);

        // Load 32 bytes as one 256-bit vector
        Vector256<byte> vec = Unsafe.ReadUnaligned<Vector256<byte>>(ref srcRef);

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
        Unsafe.WriteUnaligned(ref dstRef, fullRev);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Reverse32BytesSse2(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        ref byte srcRef = ref MemoryMarshal.GetReference(src);
        ref byte dstRef = ref MemoryMarshal.GetReference(dst);

        // Two 16-byte halves: reverse each then swap them
        Vector128<byte> lo = Unsafe.ReadUnaligned<Vector128<byte>>(ref srcRef);
        Vector128<byte> hi = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref srcRef, 16));

        Vector128<byte> revMask = Vector128.Create(
            (byte)15, (byte)14, (byte)13, (byte)12,
            (byte)11, (byte)10, (byte)9, (byte)8,
            (byte)7, (byte)6, (byte)5, (byte)4,
            (byte)3, (byte)2, (byte)1, (byte)0);

        lo = Ssse3.Shuffle(lo, revMask);
        hi = Ssse3.Shuffle(hi, revMask);

        // Store swapped halves reversed
        Unsafe.WriteUnaligned(ref dstRef, hi);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, 16), lo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Reverse32BytesScalar(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        ref ulong srcRef = ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(src));
        ref ulong dstRef = ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(dst));

        ulong a = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref srcRef, 3));
        ulong b = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref srcRef, 2));
        ulong c = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref srcRef, 1));
        ulong d = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref srcRef, 0));

        Unsafe.Add(ref dstRef, 0) = a;
        Unsafe.Add(ref dstRef, 1) = b;
        Unsafe.Add(ref dstRef, 2) = c;
        Unsafe.Add(ref dstRef, 3) = d;
    }
}
