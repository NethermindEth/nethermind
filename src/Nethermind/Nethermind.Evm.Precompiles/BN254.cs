// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
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

    // The curve seed x: p = 36x^4 + 36x^3 + 24x^2 + 6x + 1, r = 36x^4 + 36x^3 + 18x^2 + 6x + 1
    internal const ulong Seed = 4965661367192848881;

    // ξ^((p - 1) / 3) and ξ^((p - 1) / 2) for ξ = 9 + u, the coefficients of ψ
    private static readonly mclBnFp2* PsiCoefficients;

    static BN254()
    {
        if (mclBn_init(MCL_BN_SNARK1, MCLBN_COMPILED_TIME_VAR) != 0)
            throw new InvalidOperationException("MCL initialization failed");

        // Let mclBnG2_isValid check only the curve equation; the subgroup is checked by IsInG2
        mclBn_verifyOrderG2(0);

        PsiCoefficients = (mclBnFp2*)NativeMemory.Alloc(2, (nuint)sizeof(mclBnFp2));
        SetFp(ref PsiCoefficients[0].d0, "21575463638280843010398324269430826099269044274347216827212613867836435027261"u8);
        SetFp(ref PsiCoefficients[0].d1, "10307601595873709700152284273816112264069230130616436755625194854815875713954"u8);
        SetFp(ref PsiCoefficients[1].d0, "2821565182194536844548159561693502659359617185244120367078079554186484126554"u8);
        SetFp(ref PsiCoefficients[1].d1, "3505843767911556378687030309984248845540243509899259641013678093033130930403"u8);
    }

    private static void SetFp(ref mclBnFp fp, ReadOnlySpan<byte> value)
    {
        fixed (byte* ptr = value)
        {
            if (mclBnFp_setStr(ref fp, ptr, (nuint)value.Length, 10) != 0)
                throw new InvalidOperationException("MCL field element initialization failed");
        }
    }

    /// <summary>Adds two BN254 G1 points and writes the normalized result (EIP-196).</summary>
    /// <remarks>
    /// <paramref name="input"/> must be exactly 128 bytes (two 64-byte big-endian G1 points) and
    /// <paramref name="output"/> at least 64 bytes: both are accessed through raw pointers with no bounds check, so a
    /// short buffer would read or write past the end. A longer <paramref name="input"/> is rejected to keep the
    /// contract exact; a longer <paramref name="output"/> is fine — only the first 64 bytes are written.
    /// </remarks>
    /// <returns>
    /// <c>null</c> on success, otherwise why a point failed to deserialize, or <see cref="Errors.Failed"/> on a length
    /// mismatch or a serialization failure.
    /// </returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string? Add(byte[] output, ReadOnlySpan<byte> input)
    {
        const int chunkSize = 64;

        if (input.Length != 2 * chunkSize || output.Length < chunkSize)
            return Errors.Failed;

        fixed (byte* data = &MemoryMarshal.GetReference(input))
        {
            string? error = DeserializeG1(data, out mclBnG1 x, out _);
            if (error is not null)
                return error;

            error = DeserializeG1(data + chunkSize, out mclBnG1 y, out _);
            if (error is not null)
                return error;

            mclBnG1_add(ref x, x, y); // x += y
            mclBnG1_normalize(ref x, x);

            return SerializeG1(x, output) ? null : Errors.Failed;
        }
    }

    /// <summary>Multiplies a BN254 G1 point by a scalar and writes the normalized result (EIP-196).</summary>
    /// <remarks>
    /// <paramref name="input"/> must be exactly 96 bytes (a 64-byte big-endian G1 point followed by a 32-byte
    /// big-endian scalar) and <paramref name="output"/> at least 64 bytes: both are accessed through raw pointers
    /// with no bounds check, so a short buffer would read or write past the end. A longer <paramref name="input"/> is
    /// rejected to keep the contract exact; a longer <paramref name="output"/> is fine — only the first 64 bytes are written.
    /// </remarks>
    /// <returns>
    /// <c>null</c> on success, otherwise why the point failed to deserialize, or <see cref="Errors.Failed"/> on a length
    /// mismatch, a scalar that fails to decode, or a serialization failure.
    /// </returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string? Mul(byte[] output, ReadOnlySpan<byte> input)
    {
        const int chunkSize = 64;
        const int scalarSize = 32;

        if (input.Length != chunkSize + scalarSize || output.Length < chunkSize)
            return Errors.Failed;

        fixed (byte* data = &MemoryMarshal.GetReference(input))
        {
            string? error = DeserializeG1(data, out mclBnG1 x, out _);
            if (error is not null)
                return error;

            Unsafe.SkipInit(out mclBnFr y);
            if (mclBnFr_setBigEndianMod(ref y, data + chunkSize, scalarSize) == -1 || mclBnFr_isValid(y) == 0)
                return Errors.Failed;

            mclBnG1_mul(ref x, x, y);  // x *= y
            mclBnG1_normalize(ref x, x);
            return SerializeG1(x, output) ? null : Errors.Failed;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string? CheckPairing(ReadOnlySpan<byte> input, out bool isOne)
    {
        // Empty input means "true" by convention
        if (input.Length == 0)
        {
            isOne = true;
            return null;
        }

        isOne = false;

        if (input.Length % PairSize != 0)
            return Errors.Bn254PairingInputLength;

        int pairCount = input.Length / PairSize;

        fixed (byte* data = &MemoryMarshal.GetReference(input))
        {
            return pairCount switch
            {
                1 => CheckPairingSingle(data, out isOne),
                _ => CheckPairingVector(data, pairCount, out isOne),
            };
        }
    }

    private static string? CheckPairingSingle(byte* data, out bool isOne)
    {
        isOne = false;
        string? error = DeserializeG1(data, out mclBnG1 g1, out bool g1IsZero);
        if (error is not null)
            return error;

        error = DeserializeG2(data + 64, out mclBnG2 g2, out bool g2IsZero);
        if (error is not null)
            return error;

        if (g1IsZero || g2IsZero)
        {
            isOne = true;
            return null;
        }

        Unsafe.SkipInit(out mclBnGT acc);
        mclBn_millerLoop(ref acc, g1, g2);
        mclBn_finalExp(ref acc, acc);

        isOne = mclBnGT_isOne(acc) == 1;
        return null;
    }

    private static string? CheckPairingVector(byte* data, int pairCount, out bool isOne)
    {
        isOne = false;
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

                string? error = DeserializeG1(data + inputOffset, out mclBnG1 g1, out bool g1IsZero);
                if (error is not null)
                    return error;

                error = DeserializeG2(data + inputOffset + 64, out mclBnG2 g2, out bool g2IsZero);
                if (error is not null)
                    return error;

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
                (mclBnG1*)g1Bytes,
                (mclBnG2*)g2Bytes,
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
            isOne = true;
            return null;
        }

        mclBn_finalExp(ref acc, acc);

        isOne = mclBnGT_isOne(acc) == 1;
        return null;
    }

    private static string? DeserializeG1(byte* data, out mclBnG1 point, out bool isZero)
    {
        const int chunkSize = 32;

        point = default;
        isZero = IsZero64(data);

        // Treat all-zero as point at infinity for your calling convention
        if (isZero)
        {
            return null;
        }

        // Input is big-endian; MCL call below expects little-endian byte order for Fp
        byte* tmp = stackalloc byte[chunkSize];

        // x
        CopyReverse32(data, tmp);
        if (mclBnFp_deserialize(ref point.x, tmp, chunkSize) == nuint.Zero)
            return Errors.InvalidFieldElementEncoding;
        // y
        CopyReverse32(data + chunkSize, tmp);
        if (mclBnFp_deserialize(ref point.y, tmp, chunkSize) == nuint.Zero)
            return Errors.InvalidFieldElementEncoding;

        mclBnFp_setInt32(ref point.z, 1);
        return mclBnG1_isValid(point) == 1 ? null : Errors.Bn254NotOnCurve;
    }

    private static string? DeserializeG2(byte* data, out mclBnG2 point, out bool isZero)
    {
        const int chunkSize = 32;

        point = default;
        isZero = IsZero128(data);

        // Treat all-zero as point at infinity
        if (isZero)
        {
            return null;
        }

        // Input layout: x_im, x_re, y_im, y_re (each 32 bytes, big-endian)
        // MCL Fp2 layout: d0 = re, d1 = im
        byte* tmp = stackalloc byte[chunkSize];

        // x.im
        CopyReverse32(data, tmp);
        if (mclBnFp_deserialize(ref point.x.d1, tmp, chunkSize) == nuint.Zero)
            return Errors.InvalidFieldElementEncoding;

        // x.re
        CopyReverse32(data + chunkSize, tmp);
        if (mclBnFp_deserialize(ref point.x.d0, tmp, chunkSize) == nuint.Zero)
            return Errors.InvalidFieldElementEncoding;

        // y.im
        CopyReverse32(data + chunkSize * 2, tmp);
        if (mclBnFp_deserialize(ref point.y.d1, tmp, chunkSize) == nuint.Zero)
            return Errors.InvalidFieldElementEncoding;

        // y.re
        CopyReverse32(data + chunkSize * 3, tmp);
        if (mclBnFp_deserialize(ref point.y.d0, tmp, chunkSize) == nuint.Zero)
            return Errors.InvalidFieldElementEncoding;

        mclBnFp_setInt32(ref point.z.d0, 1);

        if (mclBnG2_isValid(point) == 0)
            return Errors.Bn254NotOnCurve;

        return IsInG2(point) ? null : Errors.Bn254NotInSubgroup;
    }

    /// <summary>Whether a point on the twist lies in G2, the subgroup of order r.</summary>
    /// <remarks>
    /// Tests [x + 1]Q + ψ([x]Q) + ψ²([x]Q) = ψ³([2x]Q) for the seed x (eprint 2022/348), which needs a 63-bit
    /// scalar multiplication instead of the 254-bit one by r. The test is exact on the whole twist: ψ² = tψ - p there,
    /// so it reduces to (a + bψ)Q = 0, and the degree a² + abt + b²p shares only the prime r with the twist order.
    /// </remarks>
    internal static bool IsInG2(in mclBnG2 point)
    {
        // Double-and-add rather than mclBnG2_mul, which may use endomorphism acceleration that assumes the point is in G2
        mclBnG2 xQ = point;
        for (int bit = BitOperations.Log2(Seed) - 1; bit >= 0; bit--)
        {
            mclBnG2_dbl(ref xQ, xQ);
            if ((Seed >> bit & 1) != 0)
                mclBnG2_add(ref xQ, xQ, point);
        }

        Unsafe.SkipInit(out mclBnG2 left);
        mclBnG2_add(ref left, xQ, point);
        mclBnG2 psi = xQ;
        Psi(ref psi);
        mclBnG2_add(ref left, left, psi);
        Psi(ref psi);
        mclBnG2_add(ref left, left, psi);
        Psi(ref psi);
        mclBnG2_dbl(ref psi, psi);
        return mclBnG2_isEqual(left, psi) == 1;
    }

    /// <summary>
    /// Applies the untwist-Frobenius-twist endomorphism ψ(x, y) = (conj(x)·ξ^((p - 1) / 3), conj(y)·ξ^((p - 1) / 2)).
    /// </summary>
    /// <remarks>Conjugation is a field automorphism, so conjugating z as well keeps it valid in mcl's Jacobian coordinates.</remarks>
    private static void Psi(ref mclBnG2 point)
    {
        mclBnFp_neg(ref point.x.d1, point.x.d1);
        mclBnFp_neg(ref point.y.d1, point.y.d1);
        mclBnFp_neg(ref point.z.d1, point.z.d1);
        mclBnFp2_mul(ref point.x, point.x, PsiCoefficients[0]);
        mclBnFp2_mul(ref point.y, point.y, PsiCoefficients[1]);
    }

    private static bool SerializeG1(in mclBnG1 point, byte[] output)
    {
        const int chunkSize = 32;

        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(output))
        {
            if (mclBnFp_getLittleEndian(ptr, chunkSize, point.x) == nuint.Zero)
                return false;

            if (mclBnFp_getLittleEndian(ptr + chunkSize, chunkSize, point.y) == nuint.Zero)
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
