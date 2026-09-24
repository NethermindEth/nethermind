// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    /// <summary>Hashes four padded 136-byte Keccak rate blocks into four consecutive 32-byte outputs.</summary>
    /// <remarks>The caller must ensure that AVX2 is supported and provide 544 input bytes with Keccak-256 padding already applied, and 128 output bytes.</remarks>
    internal static void ComputePaddedBlocks4Avx2(ref byte input, ref byte output)
        => ComputePaddedBlocks4Avx2<OffFlag>(ref input, ref output, HASH_DATA_AREA);

    /// <summary>Hashes four equally sized, padded messages into four consecutive 32-byte outputs.</summary>
    /// <remarks>The caller must ensure AVX2 support, a positive padded length divisible by 136, four complete padded inputs, and 128 output bytes.</remarks>
    internal static void ComputePaddedMultiBlocks4Avx2(ref byte input, int paddedLength, ref byte output)
        => ComputePaddedBlocks4Avx2<OnFlag>(ref input, ref output, paddedLength);

    [SkipLocalsInit]
    private static unsafe void ComputePaddedBlocks4Avx2<TMultipleBlocks>(ref byte input, ref byte output, int paddedLength)
        where TMultipleBlocks : struct, IFlag
    {
        Debug.Assert(Avx2.IsSupported);
        Debug.Assert(paddedLength > 0 && paddedLength % HASH_DATA_AREA == 0);
        int stride = TMultipleBlocks.IsActive ? paddedLength / sizeof(ulong) : HASH_DATA_AREA / sizeof(ulong);
        Vector256<long> offsets = Vector256.Create(0L, (long)stride, 2L * stride, 3L * stride);

        Vector256<ulong> a0;
        Vector256<ulong> a1;
        Vector256<ulong> a2;
        Vector256<ulong> a3;
        Vector256<ulong> a4;
        Vector256<ulong> a5;
        Vector256<ulong> a6;
        Vector256<ulong> a7;
        Vector256<ulong> a8;
        Vector256<ulong> a9;
        Vector256<ulong> a10;
        Vector256<ulong> a11;
        Vector256<ulong> a12;
        Vector256<ulong> a13;
        Vector256<ulong> a14;
        Vector256<ulong> a15;
        Vector256<ulong> a16;
        // Each gather stays within the caller's four complete padded messages.
        fixed (byte* inputPtr = &input)
        {
            a0 = GatherRateLane4(inputPtr, 0, offsets);
            a1 = GatherRateLane4(inputPtr, 1, offsets);
            a2 = GatherRateLane4(inputPtr, 2, offsets);
            a3 = GatherRateLane4(inputPtr, 3, offsets);
            a4 = GatherRateLane4(inputPtr, 4, offsets);
            a5 = GatherRateLane4(inputPtr, 5, offsets);
            a6 = GatherRateLane4(inputPtr, 6, offsets);
            a7 = GatherRateLane4(inputPtr, 7, offsets);
            a8 = GatherRateLane4(inputPtr, 8, offsets);
            a9 = GatherRateLane4(inputPtr, 9, offsets);
            a10 = GatherRateLane4(inputPtr, 10, offsets);
            a11 = GatherRateLane4(inputPtr, 11, offsets);
            a12 = GatherRateLane4(inputPtr, 12, offsets);
            a13 = GatherRateLane4(inputPtr, 13, offsets);
            a14 = GatherRateLane4(inputPtr, 14, offsets);
            a15 = GatherRateLane4(inputPtr, 15, offsets);
            a16 = GatherRateLane4(inputPtr, 16, offsets);
        }
        Vector256<ulong> a17 = Vector256<ulong>.Zero;
        Vector256<ulong> a18 = Vector256<ulong>.Zero;
        Vector256<ulong> a19 = Vector256<ulong>.Zero;
        Vector256<ulong> a20 = Vector256<ulong>.Zero;
        Vector256<ulong> a21 = Vector256<ulong>.Zero;
        Vector256<ulong> a22 = Vector256<ulong>.Zero;
        Vector256<ulong> a23 = Vector256<ulong>.Zero;
        Vector256<ulong> a24 = Vector256<ulong>.Zero;

        ref ulong roundConstants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);
        int offset = 0;
        while (true)
        {
            for (int round = 0; round < ROUNDS; round++)
            {
                // Theta: column parities C[x] = A[x,0] ^ A[x,1] ^ A[x,2] ^ A[x,3] ^ A[x,4].
                Vector256<ulong> c0 = Avx2.Xor(Avx2.Xor(Avx2.Xor(Avx2.Xor(a0, a5), a10), a15), a20);
                Vector256<ulong> c1 = Avx2.Xor(Avx2.Xor(Avx2.Xor(Avx2.Xor(a1, a6), a11), a16), a21);
                Vector256<ulong> c2 = Avx2.Xor(Avx2.Xor(Avx2.Xor(Avx2.Xor(a2, a7), a12), a17), a22);
                Vector256<ulong> c3 = Avx2.Xor(Avx2.Xor(Avx2.Xor(Avx2.Xor(a3, a8), a13), a18), a23);
                Vector256<ulong> c4 = Avx2.Xor(Avx2.Xor(Avx2.Xor(Avx2.Xor(a4, a9), a14), a19), a24);

                Vector256<ulong> d = Avx2.Xor(c4, RotateLeft4(c1, 1));
                a0 = Avx2.Xor(a0, d);
                a5 = Avx2.Xor(a5, d);
                a10 = Avx2.Xor(a10, d);
                a15 = Avx2.Xor(a15, d);
                a20 = Avx2.Xor(a20, d);

                d = Avx2.Xor(c0, RotateLeft4(c2, 1));
                a1 = Avx2.Xor(a1, d);
                a6 = Avx2.Xor(a6, d);
                a11 = Avx2.Xor(a11, d);
                a16 = Avx2.Xor(a16, d);
                a21 = Avx2.Xor(a21, d);

                d = Avx2.Xor(c1, RotateLeft4(c3, 1));
                a2 = Avx2.Xor(a2, d);
                a7 = Avx2.Xor(a7, d);
                a12 = Avx2.Xor(a12, d);
                a17 = Avx2.Xor(a17, d);
                a22 = Avx2.Xor(a22, d);

                d = Avx2.Xor(c2, RotateLeft4(c4, 1));
                a3 = Avx2.Xor(a3, d);
                a8 = Avx2.Xor(a8, d);
                a13 = Avx2.Xor(a13, d);
                a18 = Avx2.Xor(a18, d);
                a23 = Avx2.Xor(a23, d);

                d = Avx2.Xor(c3, RotateLeft4(c0, 1));
                a4 = Avx2.Xor(a4, d);
                a9 = Avx2.Xor(a9, d);
                a14 = Avx2.Xor(a14, d);
                a19 = Avx2.Xor(a19, d);
                a24 = Avx2.Xor(a24, d);

                // Rho + Pi: walk the 24-lane Pi cycle in place with source and displaced-lane temporaries.
                Vector256<ulong> source = a1;
                Vector256<ulong> displaced;
                displaced = a10;
                a10 = RotateLeft4(source, 1);
                source = displaced;
                displaced = a7;
                a7 = RotateLeft4(source, 3);
                source = displaced;
                displaced = a11;
                a11 = RotateLeft4(source, 6);
                source = displaced;
                displaced = a17;
                a17 = RotateLeft4(source, 10);
                source = displaced;
                displaced = a18;
                a18 = RotateLeft4(source, 15);
                source = displaced;
                displaced = a3;
                a3 = RotateLeft4(source, 21);
                source = displaced;
                displaced = a5;
                a5 = RotateLeft4(source, 28);
                source = displaced;
                displaced = a16;
                a16 = RotateLeft4(source, 36);
                source = displaced;
                displaced = a8;
                a8 = RotateLeft4(source, 45);
                source = displaced;
                displaced = a21;
                a21 = RotateLeft4(source, 55);
                source = displaced;
                displaced = a24;
                a24 = RotateLeft4(source, 2);
                source = displaced;
                displaced = a4;
                a4 = RotateLeft4(source, 14);
                source = displaced;
                displaced = a15;
                a15 = RotateLeft4(source, 27);
                source = displaced;
                displaced = a23;
                a23 = RotateLeft4(source, 41);
                source = displaced;
                displaced = a19;
                a19 = RotateLeft4(source, 56);
                source = displaced;
                displaced = a13;
                a13 = RotateLeft4(source, 8);
                source = displaced;
                displaced = a12;
                a12 = RotateLeft4(source, 25);
                source = displaced;
                displaced = a2;
                a2 = RotateLeft4(source, 43);
                source = displaced;
                displaced = a20;
                a20 = RotateLeft4(source, 62);
                source = displaced;
                displaced = a14;
                a14 = RotateLeft4(source, 18);
                source = displaced;
                displaced = a22;
                a22 = RotateLeft4(source, 39);
                source = displaced;
                displaced = a9;
                a9 = RotateLeft4(source, 61);
                source = displaced;
                displaced = a6;
                a6 = RotateLeft4(source, 20);
                source = displaced;
                a1 = RotateLeft4(source, 44);

                ChiRowX4(ref a0, ref a1, ref a2, ref a3, ref a4);
                ChiRowX4(ref a5, ref a6, ref a7, ref a8, ref a9);
                ChiRowX4(ref a10, ref a11, ref a12, ref a13, ref a14);
                ChiRowX4(ref a15, ref a16, ref a17, ref a18, ref a19);
                ChiRowX4(ref a20, ref a21, ref a22, ref a23, ref a24);
                a0 = Avx2.Xor(a0, Vector256.Create(Unsafe.Add(ref roundConstants, round)));
            }

            if (!TMultipleBlocks.IsActive) break;
            offset += HASH_DATA_AREA;
            if (offset == paddedLength) break;

            // Offset covers a complete rate block inside each padded message.
            fixed (byte* inputPtr = &input)
            {
                a0 ^= GatherRateLane4(inputPtr + offset, 0, offsets);
                a1 ^= GatherRateLane4(inputPtr + offset, 1, offsets);
                a2 ^= GatherRateLane4(inputPtr + offset, 2, offsets);
                a3 ^= GatherRateLane4(inputPtr + offset, 3, offsets);
                a4 ^= GatherRateLane4(inputPtr + offset, 4, offsets);
                a5 ^= GatherRateLane4(inputPtr + offset, 5, offsets);
                a6 ^= GatherRateLane4(inputPtr + offset, 6, offsets);
                a7 ^= GatherRateLane4(inputPtr + offset, 7, offsets);
                a8 ^= GatherRateLane4(inputPtr + offset, 8, offsets);
                a9 ^= GatherRateLane4(inputPtr + offset, 9, offsets);
                a10 ^= GatherRateLane4(inputPtr + offset, 10, offsets);
                a11 ^= GatherRateLane4(inputPtr + offset, 11, offsets);
                a12 ^= GatherRateLane4(inputPtr + offset, 12, offsets);
                a13 ^= GatherRateLane4(inputPtr + offset, 13, offsets);
                a14 ^= GatherRateLane4(inputPtr + offset, 14, offsets);
                a15 ^= GatherRateLane4(inputPtr + offset, 15, offsets);
                a16 ^= GatherRateLane4(inputPtr + offset, 16, offsets);
            }
        }

        StoreHash4(ref output, 0, a0, a1, a2, a3);
        StoreHash4(ref output, 1, a0, a1, a2, a3);
        StoreHash4(ref output, 2, a0, a1, a2, a3);
        StoreHash4(ref output, 3, a0, a1, a2, a3);
    }

    /// <remarks>
    /// AVX-512 rotates a 64-bit lane in one instruction; without it the rotate costs a shift pair
    /// plus an or. The check folds at JIT time, so the unused arm is never emitted.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> RotateLeft4(Vector256<ulong> value, [ConstantExpected] byte count) =>
        Avx512F.VL.IsSupported
            ? Avx512F.VL.RotateLeft(value, count)
            : (value << count) | (value >> (64 - count));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ChiRowX4(ref Vector256<ulong> a0, ref Vector256<ulong> a1, ref Vector256<ulong> a2,
        ref Vector256<ulong> a3, ref Vector256<ulong> a4)
    {
        Vector256<ulong> b0 = a0;
        Vector256<ulong> b1 = a1;
        a0 = Avx2.Xor(a0, Avx2.AndNot(a1, a2));
        a1 = Avx2.Xor(a1, Avx2.AndNot(a2, a3));
        a2 = Avx2.Xor(a2, Avx2.AndNot(a3, a4));
        a3 = Avx2.Xor(a3, Avx2.AndNot(a4, b0));
        a4 = Avx2.Xor(a4, Avx2.AndNot(b0, b1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Offsets select the same rate lane in each of the four padded messages.
    private static unsafe Vector256<ulong> GatherRateLane4(byte* input, int lane, Vector256<long> offsets) =>
        Avx2.GatherVector256((ulong*)(input + lane * sizeof(ulong)), offsets, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreHash4(ref byte output, int index,
        Vector256<ulong> a0, Vector256<ulong> a1, Vector256<ulong> a2, Vector256<ulong> a3)
    {
        ref byte destination = ref Unsafe.Add(ref output, index * HASH_SIZE);
        Unsafe.WriteUnaligned(ref destination, a0.GetElement(index));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), a1.GetElement(index));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), a2.GetElement(index));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 24), a3.GetElement(index));
    }
}
