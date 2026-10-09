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

                Vector256<ulong> d0 = Avx2.Xor(c4, RotateLeft4(c1, 1));
                Vector256<ulong> d1 = Avx2.Xor(c0, RotateLeft4(c2, 1));
                Vector256<ulong> d2 = Avx2.Xor(c1, RotateLeft4(c3, 1));
                Vector256<ulong> d3 = Avx2.Xor(c2, RotateLeft4(c4, 1));
                Vector256<ulong> d4 = Avx2.Xor(c3, RotateLeft4(c0, 1));
                // Form one rho/pi row at a time to shorten temporary lifetimes before chi.
                Vector256<ulong> b0 = Avx2.Xor(a0, d0);
                Vector256<ulong> b1 = RotateLeft4(Avx2.Xor(a6, d1), 44);
                Vector256<ulong> b2 = RotateLeft4(Avx2.Xor(a12, d2), 43);
                Vector256<ulong> b3 = RotateLeft4(Avx2.Xor(a18, d3), 21);
                Vector256<ulong> b4 = RotateLeft4(Avx2.Xor(a24, d4), 14);
                Vector256<ulong> n0 = Avx2.Xor(b0, Avx2.AndNot(b1, b2));
                Vector256<ulong> n1 = Avx2.Xor(b1, Avx2.AndNot(b2, b3));
                Vector256<ulong> n2 = Avx2.Xor(b2, Avx2.AndNot(b3, b4));
                Vector256<ulong> n3 = Avx2.Xor(b3, Avx2.AndNot(b4, b0));
                Vector256<ulong> n4 = Avx2.Xor(b4, Avx2.AndNot(b0, b1));
                Vector256<ulong> b5 = RotateLeft4(Avx2.Xor(a3, d3), 28);
                Vector256<ulong> b6 = RotateLeft4(Avx2.Xor(a9, d4), 20);
                Vector256<ulong> b7 = RotateLeft4(Avx2.Xor(a10, d0), 3);
                Vector256<ulong> b8 = RotateLeft4(Avx2.Xor(a16, d1), 45);
                Vector256<ulong> b9 = RotateLeft4(Avx2.Xor(a22, d2), 61);
                Vector256<ulong> n5 = Avx2.Xor(b5, Avx2.AndNot(b6, b7));
                Vector256<ulong> n6 = Avx2.Xor(b6, Avx2.AndNot(b7, b8));
                Vector256<ulong> n7 = Avx2.Xor(b7, Avx2.AndNot(b8, b9));
                Vector256<ulong> n8 = Avx2.Xor(b8, Avx2.AndNot(b9, b5));
                Vector256<ulong> n9 = Avx2.Xor(b9, Avx2.AndNot(b5, b6));
                Vector256<ulong> b10 = RotateLeft4(Avx2.Xor(a1, d1), 1);
                Vector256<ulong> b11 = RotateLeft4(Avx2.Xor(a7, d2), 6);
                Vector256<ulong> b12 = RotateLeft4(Avx2.Xor(a13, d3), 25);
                Vector256<ulong> b13 = RotateLeft4(Avx2.Xor(a19, d4), 8);
                Vector256<ulong> b14 = RotateLeft4(Avx2.Xor(a20, d0), 18);
                Vector256<ulong> n10 = Avx2.Xor(b10, Avx2.AndNot(b11, b12));
                Vector256<ulong> n11 = Avx2.Xor(b11, Avx2.AndNot(b12, b13));
                Vector256<ulong> n12 = Avx2.Xor(b12, Avx2.AndNot(b13, b14));
                Vector256<ulong> n13 = Avx2.Xor(b13, Avx2.AndNot(b14, b10));
                Vector256<ulong> n14 = Avx2.Xor(b14, Avx2.AndNot(b10, b11));
                Vector256<ulong> b15 = RotateLeft4(Avx2.Xor(a4, d4), 27);
                Vector256<ulong> b16 = RotateLeft4(Avx2.Xor(a5, d0), 36);
                Vector256<ulong> b17 = RotateLeft4(Avx2.Xor(a11, d1), 10);
                Vector256<ulong> b18 = RotateLeft4(Avx2.Xor(a17, d2), 15);
                Vector256<ulong> b19 = RotateLeft4(Avx2.Xor(a23, d3), 56);
                Vector256<ulong> n15 = Avx2.Xor(b15, Avx2.AndNot(b16, b17));
                Vector256<ulong> n16 = Avx2.Xor(b16, Avx2.AndNot(b17, b18));
                Vector256<ulong> n17 = Avx2.Xor(b17, Avx2.AndNot(b18, b19));
                Vector256<ulong> n18 = Avx2.Xor(b18, Avx2.AndNot(b19, b15));
                Vector256<ulong> n19 = Avx2.Xor(b19, Avx2.AndNot(b15, b16));
                Vector256<ulong> b20 = RotateLeft4(Avx2.Xor(a2, d2), 62);
                Vector256<ulong> b21 = RotateLeft4(Avx2.Xor(a8, d3), 55);
                Vector256<ulong> b22 = RotateLeft4(Avx2.Xor(a14, d4), 39);
                Vector256<ulong> b23 = RotateLeft4(Avx2.Xor(a15, d0), 41);
                Vector256<ulong> b24 = RotateLeft4(Avx2.Xor(a21, d1), 2);
                Vector256<ulong> n20 = Avx2.Xor(b20, Avx2.AndNot(b21, b22));
                Vector256<ulong> n21 = Avx2.Xor(b21, Avx2.AndNot(b22, b23));
                Vector256<ulong> n22 = Avx2.Xor(b22, Avx2.AndNot(b23, b24));
                Vector256<ulong> n23 = Avx2.Xor(b23, Avx2.AndNot(b24, b20));
                Vector256<ulong> n24 = Avx2.Xor(b24, Avx2.AndNot(b20, b21));
                a0 = Avx2.Xor(n0, Vector256.Create(Unsafe.Add(ref roundConstants, round)));
                a1 = n1;
                a2 = n2;
                a3 = n3;
                a4 = n4;
                a5 = n5;
                a6 = n6;
                a7 = n7;
                a8 = n8;
                a9 = n9;
                a10 = n10;
                a11 = n11;
                a12 = n12;
                a13 = n13;
                a14 = n14;
                a15 = n15;
                a16 = n16;
                a17 = n17;
                a18 = n18;
                a19 = n19;
                a20 = n20;
                a21 = n21;
                a22 = n22;
                a23 = n23;
                a24 = n24;
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
