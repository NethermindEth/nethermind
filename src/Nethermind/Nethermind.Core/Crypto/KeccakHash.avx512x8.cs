// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    /// <summary>Hashes eight consecutive 532-byte inputs into eight consecutive 32-byte outputs.</summary>
    /// <remarks>The caller must ensure AVX-512F support and provide 4256 input bytes and 256 output bytes.</remarks>
    [SkipLocalsInit]
    internal static unsafe void ComputeHash532Bytes8Avx512(ref byte input, ref byte output)
    {
        Debug.Assert(Avx512F.IsSupported);
        Debug.Assert(Avx2.IsSupported);

        Vector512<ulong> a0 = Vector512<ulong>.Zero;
        Vector512<ulong> a1 = Vector512<ulong>.Zero;
        Vector512<ulong> a2 = Vector512<ulong>.Zero;
        Vector512<ulong> a3 = Vector512<ulong>.Zero;
        Vector512<ulong> a4 = Vector512<ulong>.Zero;
        Vector512<ulong> a5 = Vector512<ulong>.Zero;
        Vector512<ulong> a6 = Vector512<ulong>.Zero;
        Vector512<ulong> a7 = Vector512<ulong>.Zero;
        Vector512<ulong> a8 = Vector512<ulong>.Zero;
        Vector512<ulong> a9 = Vector512<ulong>.Zero;
        Vector512<ulong> a10 = Vector512<ulong>.Zero;
        Vector512<ulong> a11 = Vector512<ulong>.Zero;
        Vector512<ulong> a12 = Vector512<ulong>.Zero;
        Vector512<ulong> a13 = Vector512<ulong>.Zero;
        Vector512<ulong> a14 = Vector512<ulong>.Zero;
        Vector512<ulong> a15 = Vector512<ulong>.Zero;
        Vector512<ulong> a16 = Vector512<ulong>.Zero;
        Vector512<ulong> a17 = Vector512<ulong>.Zero;
        Vector512<ulong> a18 = Vector512<ulong>.Zero;
        Vector512<ulong> a19 = Vector512<ulong>.Zero;
        Vector512<ulong> a20 = Vector512<ulong>.Zero;
        Vector512<ulong> a21 = Vector512<ulong>.Zero;
        Vector512<ulong> a22 = Vector512<ulong>.Zero;
        Vector512<ulong> a23 = Vector512<ulong>.Zero;
        Vector512<ulong> a24 = Vector512<ulong>.Zero;
        ref ulong constants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);
        // Gathers stay within eight complete 532-byte inputs; the final lane reads only its four remaining bytes.
        fixed (byte* inputPtr = &input)
        {
            for (int offset = 0; offset <= 3 * HASH_DATA_AREA; offset += HASH_DATA_AREA)
            {
                a0 ^= GatherLane(inputPtr + offset, 0, 532);
                a1 ^= GatherLane(inputPtr + offset, 1, 532);
                a2 ^= GatherLane(inputPtr + offset, 2, 532);
                a3 ^= GatherLane(inputPtr + offset, 3, 532);
                a4 ^= GatherLane(inputPtr + offset, 4, 532);
                a5 ^= GatherLane(inputPtr + offset, 5, 532);
                a6 ^= GatherLane(inputPtr + offset, 6, 532);
                a7 ^= GatherLane(inputPtr + offset, 7, 532);
                a8 ^= GatherLane(inputPtr + offset, 8, 532);
                a9 ^= GatherLane(inputPtr + offset, 9, 532);
                a10 ^= GatherLane(inputPtr + offset, 10, 532);
                a11 ^= GatherLane(inputPtr + offset, 11, 532);
                a12 ^= GatherLane(inputPtr + offset, 12, 532);
                a13 ^= GatherLane(inputPtr + offset, 13, 532);
                a14 ^= GatherLane(inputPtr + offset, 14, 532);
                if (offset == 3 * HASH_DATA_AREA)
                {
                    a15 ^= Vector512.Create(
                        Unsafe.ReadUnaligned<uint>(inputPtr + 528) | (1UL << 32),
                        Unsafe.ReadUnaligned<uint>(inputPtr + 1060) | (1UL << 32),
                        Unsafe.ReadUnaligned<uint>(inputPtr + 1592) | (1UL << 32),
                        Unsafe.ReadUnaligned<uint>(inputPtr + 2124) | (1UL << 32),
                        Unsafe.ReadUnaligned<uint>(inputPtr + 2656) | (1UL << 32),
                        Unsafe.ReadUnaligned<uint>(inputPtr + 3188) | (1UL << 32),
                        Unsafe.ReadUnaligned<uint>(inputPtr + 3720) | (1UL << 32),
                        Unsafe.ReadUnaligned<uint>(inputPtr + 4252) | (1UL << 32));
                    a16 ^= Vector512.Create(0x8000000000000000UL);
                }
                else
                {
                    a15 ^= GatherLane(inputPtr + offset, 15, 532);
                    a16 ^= GatherLane(inputPtr + offset, 16, 532);
                }

                for (int round = 0; round < ROUNDS; round++)
                {
                    RoundX8(
                        ref a0, ref a1, ref a2, ref a3, ref a4,
                        ref a5, ref a6, ref a7, ref a8, ref a9,
                        ref a10, ref a11, ref a12, ref a13, ref a14,
                        ref a15, ref a16, ref a17, ref a18, ref a19,
                        ref a20, ref a21, ref a22, ref a23, ref a24,
                        Vector512.Create(Unsafe.Add(ref constants, round)));
                }
            }
        }

        StoreHashes8(ref output, a0, a1, a2, a3);
    }

    /// <summary>Hashes eight equally sized, padded inputs into eight consecutive 32-byte outputs.</summary>
    /// <remarks>The caller must ensure AVX-512F support, a positive padded length divisible by 136, eight complete inputs, and 256 output bytes.</remarks>
    [SkipLocalsInit]
    internal static unsafe void ComputePaddedMultiBlocks8Avx512(ref byte input, int paddedLength, ref byte output)
    {
        Debug.Assert(paddedLength > 0 && paddedLength % HASH_DATA_AREA == 0);
        Debug.Assert(Avx512F.IsSupported);
        Debug.Assert(Avx2.IsSupported);

        Vector512<ulong> a0 = Vector512<ulong>.Zero;
        Vector512<ulong> a1 = Vector512<ulong>.Zero;
        Vector512<ulong> a2 = Vector512<ulong>.Zero;
        Vector512<ulong> a3 = Vector512<ulong>.Zero;
        Vector512<ulong> a4 = Vector512<ulong>.Zero;
        Vector512<ulong> a5 = Vector512<ulong>.Zero;
        Vector512<ulong> a6 = Vector512<ulong>.Zero;
        Vector512<ulong> a7 = Vector512<ulong>.Zero;
        Vector512<ulong> a8 = Vector512<ulong>.Zero;
        Vector512<ulong> a9 = Vector512<ulong>.Zero;
        Vector512<ulong> a10 = Vector512<ulong>.Zero;
        Vector512<ulong> a11 = Vector512<ulong>.Zero;
        Vector512<ulong> a12 = Vector512<ulong>.Zero;
        Vector512<ulong> a13 = Vector512<ulong>.Zero;
        Vector512<ulong> a14 = Vector512<ulong>.Zero;
        Vector512<ulong> a15 = Vector512<ulong>.Zero;
        Vector512<ulong> a16 = Vector512<ulong>.Zero;
        Vector512<ulong> a17 = Vector512<ulong>.Zero;
        Vector512<ulong> a18 = Vector512<ulong>.Zero;
        Vector512<ulong> a19 = Vector512<ulong>.Zero;
        Vector512<ulong> a20 = Vector512<ulong>.Zero;
        Vector512<ulong> a21 = Vector512<ulong>.Zero;
        Vector512<ulong> a22 = Vector512<ulong>.Zero;
        Vector512<ulong> a23 = Vector512<ulong>.Zero;
        Vector512<ulong> a24 = Vector512<ulong>.Zero;
        ref ulong constants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);
        // Every gather stays within the eight complete padded inputs supplied by the caller.
        fixed (byte* inputPtr = &input)
        {
            Vector256<long> lowerOffsets = Vector256.Create(0L, paddedLength, 2L * paddedLength, 3L * paddedLength);
            Vector256<long> upperOffsets = Vector256.Create(4L * paddedLength, 5L * paddedLength, 6L * paddedLength, 7L * paddedLength);
            for (int offset = 0; offset < paddedLength; offset += HASH_DATA_AREA)
            {
                a0 ^= GatherLane(inputPtr + offset, 0, lowerOffsets, upperOffsets);
                a1 ^= GatherLane(inputPtr + offset, 1, lowerOffsets, upperOffsets);
                a2 ^= GatherLane(inputPtr + offset, 2, lowerOffsets, upperOffsets);
                a3 ^= GatherLane(inputPtr + offset, 3, lowerOffsets, upperOffsets);
                a4 ^= GatherLane(inputPtr + offset, 4, lowerOffsets, upperOffsets);
                a5 ^= GatherLane(inputPtr + offset, 5, lowerOffsets, upperOffsets);
                a6 ^= GatherLane(inputPtr + offset, 6, lowerOffsets, upperOffsets);
                a7 ^= GatherLane(inputPtr + offset, 7, lowerOffsets, upperOffsets);
                a8 ^= GatherLane(inputPtr + offset, 8, lowerOffsets, upperOffsets);
                a9 ^= GatherLane(inputPtr + offset, 9, lowerOffsets, upperOffsets);
                a10 ^= GatherLane(inputPtr + offset, 10, lowerOffsets, upperOffsets);
                a11 ^= GatherLane(inputPtr + offset, 11, lowerOffsets, upperOffsets);
                a12 ^= GatherLane(inputPtr + offset, 12, lowerOffsets, upperOffsets);
                a13 ^= GatherLane(inputPtr + offset, 13, lowerOffsets, upperOffsets);
                a14 ^= GatherLane(inputPtr + offset, 14, lowerOffsets, upperOffsets);
                a15 ^= GatherLane(inputPtr + offset, 15, lowerOffsets, upperOffsets);
                a16 ^= GatherLane(inputPtr + offset, 16, lowerOffsets, upperOffsets);

                for (int round = 0; round < ROUNDS; round++)
                {
                    RoundX8(
                        ref a0, ref a1, ref a2, ref a3, ref a4,
                        ref a5, ref a6, ref a7, ref a8, ref a9,
                        ref a10, ref a11, ref a12, ref a13, ref a14,
                        ref a15, ref a16, ref a17, ref a18, ref a19,
                        ref a20, ref a21, ref a22, ref a23, ref a24,
                        Vector512.Create(Unsafe.Add(ref constants, round)));
                }
            }
        }

        StoreHashes8(ref output, a0, a1, a2, a3);
    }

    /// <summary>Hashes eight consecutive 32-byte inputs into eight consecutive 32-byte outputs.</summary>
    /// <remarks>The caller must ensure AVX-512F support and provide 256 input bytes and 256 output bytes.</remarks>
    [SkipLocalsInit]
    internal static unsafe void ComputeHash32Bytes8Avx512(ref byte input, ref byte output)
    {
        Debug.Assert(Avx512F.IsSupported);
        Debug.Assert(Avx2.IsSupported);

        Vector512<ulong> a0, a1, a2, a3;
        // Each gather stays within eight complete 32-byte inputs.
        fixed (byte* inputPtr = &input)
        {
            a0 = GatherLane(inputPtr, 0, 32);
            a1 = GatherLane(inputPtr, 1, 32);
            a2 = GatherLane(inputPtr, 2, 32);
            a3 = GatherLane(inputPtr, 3, 32);
        }

        PermuteAndStore8(
            a0, a1, a2, a3, Vector512.Create(1UL),
            Vector512<ulong>.Zero, Vector512<ulong>.Zero, Vector512<ulong>.Zero, Vector512<ulong>.Zero, Vector512<ulong>.Zero,
            Vector512<ulong>.Zero, Vector512<ulong>.Zero, Vector512<ulong>.Zero, Vector512<ulong>.Zero, Vector512<ulong>.Zero,
            Vector512<ulong>.Zero, Vector512.Create(0x8000000000000000UL), ref output);
    }

    /// <summary>Hashes eight consecutive 64-byte inputs into eight consecutive 32-byte outputs.</summary>
    /// <remarks>The caller must ensure that AVX-512F is supported and provide 512 input bytes and 256 output bytes.</remarks>
    [SkipLocalsInit]
    internal static unsafe void ComputeHash64Bytes8Avx512(ref byte input, ref byte output)
    {
        Debug.Assert(Avx512F.IsSupported);
        Debug.Assert(Avx2.IsSupported);

        Vector512<ulong> a0;
        Vector512<ulong> a1;
        Vector512<ulong> a2;
        Vector512<ulong> a3;
        Vector512<ulong> a4;
        Vector512<ulong> a5;
        Vector512<ulong> a6;
        Vector512<ulong> a7;
        // Each gather reads one lane from all eight 64-byte inputs; lane 7 reaches the final input byte.
        fixed (byte* inputPtr = &input)
        {
            a0 = GatherLane(inputPtr, 0, 64);
            a1 = GatherLane(inputPtr, 1, 64);
            a2 = GatherLane(inputPtr, 2, 64);
            a3 = GatherLane(inputPtr, 3, 64);
            a4 = GatherLane(inputPtr, 4, 64);
            a5 = GatherLane(inputPtr, 5, 64);
            a6 = GatherLane(inputPtr, 6, 64);
            a7 = GatherLane(inputPtr, 7, 64);
        }
        // Multi-rate padding (FIPS 202 sec. 5.1): 0x01 right after each 64-byte input, and 0x80
        // at byte 135, the last byte of the 136-byte rate, which is the top bit of lane 16.
        Vector512<ulong> a8 = Vector512.Create(1UL);
        Vector512<ulong> a9 = Vector512<ulong>.Zero;
        Vector512<ulong> a10 = Vector512<ulong>.Zero;
        Vector512<ulong> a11 = Vector512<ulong>.Zero;
        Vector512<ulong> a12 = Vector512<ulong>.Zero;
        Vector512<ulong> a13 = Vector512<ulong>.Zero;
        Vector512<ulong> a14 = Vector512<ulong>.Zero;
        Vector512<ulong> a15 = Vector512<ulong>.Zero;
        Vector512<ulong> a16 = Vector512.Create(0x8000000000000000UL);

        PermuteAndStore8(
            a0, a1, a2, a3, a4,
            a5, a6, a7, a8, a9,
            a10, a11, a12, a13, a14,
            a15, a16,
            ref output);
    }

    /// <summary>Hashes eight padded 136-byte Keccak rate blocks into eight consecutive 32-byte outputs.</summary>
    /// <remarks>The caller must ensure that AVX-512F is supported and provide 1088 input bytes with Keccak-256 padding already applied, and 256 output bytes.</remarks>
    [SkipLocalsInit]
    internal static unsafe void ComputePaddedBlocks8Avx512(ref byte input, ref byte output)
    {
        Debug.Assert(Avx512F.IsSupported);
        Debug.Assert(Avx2.IsSupported);

        Vector512<ulong> a0;
        Vector512<ulong> a1;
        Vector512<ulong> a2;
        Vector512<ulong> a3;
        Vector512<ulong> a4;
        Vector512<ulong> a5;
        Vector512<ulong> a6;
        Vector512<ulong> a7;
        Vector512<ulong> a8;
        Vector512<ulong> a9;
        Vector512<ulong> a10;
        Vector512<ulong> a11;
        Vector512<ulong> a12;
        Vector512<ulong> a13;
        Vector512<ulong> a14;
        Vector512<ulong> a15;
        Vector512<ulong> a16;
        // Each gather stays within the caller's eight complete 136-byte rate blocks.
        fixed (byte* inputPtr = &input)
        {
            a0 = GatherLane(inputPtr, 0, HASH_DATA_AREA);
            a1 = GatherLane(inputPtr, 1, HASH_DATA_AREA);
            a2 = GatherLane(inputPtr, 2, HASH_DATA_AREA);
            a3 = GatherLane(inputPtr, 3, HASH_DATA_AREA);
            a4 = GatherLane(inputPtr, 4, HASH_DATA_AREA);
            a5 = GatherLane(inputPtr, 5, HASH_DATA_AREA);
            a6 = GatherLane(inputPtr, 6, HASH_DATA_AREA);
            a7 = GatherLane(inputPtr, 7, HASH_DATA_AREA);
            a8 = GatherLane(inputPtr, 8, HASH_DATA_AREA);
            a9 = GatherLane(inputPtr, 9, HASH_DATA_AREA);
            a10 = GatherLane(inputPtr, 10, HASH_DATA_AREA);
            a11 = GatherLane(inputPtr, 11, HASH_DATA_AREA);
            a12 = GatherLane(inputPtr, 12, HASH_DATA_AREA);
            a13 = GatherLane(inputPtr, 13, HASH_DATA_AREA);
            a14 = GatherLane(inputPtr, 14, HASH_DATA_AREA);
            a15 = GatherLane(inputPtr, 15, HASH_DATA_AREA);
            a16 = GatherLane(inputPtr, 16, HASH_DATA_AREA);
        }

        PermuteAndStore8(
            a0, a1, a2, a3, a4,
            a5, a6, a7, a8, a9,
            a10, a11, a12, a13, a14,
            a15, a16,
            ref output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PermuteAndStore8(
        Vector512<ulong> a0, Vector512<ulong> a1, Vector512<ulong> a2, Vector512<ulong> a3, Vector512<ulong> a4,
        Vector512<ulong> a5, Vector512<ulong> a6, Vector512<ulong> a7, Vector512<ulong> a8, Vector512<ulong> a9,
        Vector512<ulong> a10, Vector512<ulong> a11, Vector512<ulong> a12, Vector512<ulong> a13, Vector512<ulong> a14,
        Vector512<ulong> a15, Vector512<ulong> a16,
        ref byte output)
    {
        Vector512<ulong> a17 = Vector512<ulong>.Zero;
        Vector512<ulong> a18 = Vector512<ulong>.Zero;
        Vector512<ulong> a19 = Vector512<ulong>.Zero;
        Vector512<ulong> a20 = Vector512<ulong>.Zero;
        Vector512<ulong> a21 = Vector512<ulong>.Zero;
        Vector512<ulong> a22 = Vector512<ulong>.Zero;
        Vector512<ulong> a23 = Vector512<ulong>.Zero;
        Vector512<ulong> a24 = Vector512<ulong>.Zero;

        ref ulong roundConstants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);
        for (int round = 0; round < ROUNDS; round++)
        {
            RoundX8(
                ref a0, ref a1, ref a2, ref a3, ref a4,
                ref a5, ref a6, ref a7, ref a8, ref a9,
                ref a10, ref a11, ref a12, ref a13, ref a14,
                ref a15, ref a16, ref a17, ref a18, ref a19,
                ref a20, ref a21, ref a22, ref a23, ref a24,
                Vector512.Create(Unsafe.Add(ref roundConstants, round)));
        }

        StoreHashes8(ref output, a0, a1, a2, a3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector512<ulong> GatherLane(byte* input, int lane, Vector256<long> lowerOffsets, Vector256<long> upperOffsets)
    {
        // The caller supplies eight complete padded inputs and offsets to the same lane in each.
        byte* lanePtr = input + lane * sizeof(ulong);
        Vector256<ulong> lower = Avx2.GatherVector256((ulong*)lanePtr, lowerOffsets, 1);
        Vector256<ulong> upper = Avx2.GatherVector256((ulong*)lanePtr, upperOffsets, 1);
        return lower.ToVector512Unsafe().WithUpper(upper);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector512<ulong> GatherLane(byte* input, int lane, int stride)
    {
        // Callers supply eight complete inputs and constrain lane to an in-bounds eight-byte read.
        byte* lanePtr = input + lane * sizeof(ulong);
        Vector256<ulong> lower = Avx2.GatherVector256((ulong*)lanePtr, Vector256.Create(0L, stride, 2L * stride, 3L * stride), 1);
        Vector256<ulong> upper = Avx2.GatherVector256((ulong*)lanePtr, Vector256.Create(4L * stride, 5L * stride, 6L * stride, 7L * stride), 1);
        return Vector512.Create(lower, upper);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreHashes8(ref byte output,
        Vector512<ulong> a0, Vector512<ulong> a1, Vector512<ulong> a2, Vector512<ulong> a3)
    {
        StoreHash(ref output, 0, a0, a1, a2, a3);
        StoreHash(ref output, 1, a0, a1, a2, a3);
        StoreHash(ref output, 2, a0, a1, a2, a3);
        StoreHash(ref output, 3, a0, a1, a2, a3);
        StoreHash(ref output, 4, a0, a1, a2, a3);
        StoreHash(ref output, 5, a0, a1, a2, a3);
        StoreHash(ref output, 6, a0, a1, a2, a3);
        StoreHash(ref output, 7, a0, a1, a2, a3);
    }

    /// <summary>Writes the 32-byte hash of batch element <paramref name="hashIndex"/> from the first four state lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreHash(ref byte output, int hashIndex,
        Vector512<ulong> a0, Vector512<ulong> a1, Vector512<ulong> a2, Vector512<ulong> a3)
    {
        ref byte destination = ref Unsafe.Add(ref output, hashIndex * HASH_SIZE);
        Unsafe.WriteUnaligned(ref destination, a0.GetElement(hashIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), a1.GetElement(hashIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), a2.GetElement(hashIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 24), a3.GetElement(hashIndex));
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RoundX8(
        ref Vector512<ulong> a0, ref Vector512<ulong> a1, ref Vector512<ulong> a2, ref Vector512<ulong> a3, ref Vector512<ulong> a4,
        ref Vector512<ulong> a5, ref Vector512<ulong> a6, ref Vector512<ulong> a7, ref Vector512<ulong> a8, ref Vector512<ulong> a9,
        ref Vector512<ulong> a10, ref Vector512<ulong> a11, ref Vector512<ulong> a12, ref Vector512<ulong> a13, ref Vector512<ulong> a14,
        ref Vector512<ulong> a15, ref Vector512<ulong> a16, ref Vector512<ulong> a17, ref Vector512<ulong> a18, ref Vector512<ulong> a19,
        ref Vector512<ulong> a20, ref Vector512<ulong> a21, ref Vector512<ulong> a22, ref Vector512<ulong> a23, ref Vector512<ulong> a24,
        Vector512<ulong> roundConstant)
    {
        // Theta: column parities C[x] = A[x,0] ^ A[x,1] ^ A[x,2] ^ A[x,3] ^ A[x,4].
        Vector512<ulong> c0 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a0, a5, a10, Xor3), a15, a20, Xor3);
        Vector512<ulong> c1 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a1, a6, a11, Xor3), a16, a21, Xor3);
        Vector512<ulong> c2 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a2, a7, a12, Xor3), a17, a22, Xor3);
        Vector512<ulong> c3 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a3, a8, a13, Xor3), a18, a23, Xor3);
        Vector512<ulong> c4 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a4, a9, a14, Xor3), a19, a24, Xor3);

        // Theta: A[x,y] ^= C[x-1] ^ ROL(C[x+1], 1); both XORs fuse into one ternary op per lane.
        Vector512<ulong> rolC1 = Avx512F.RotateLeft(c1, 1);
        a0 = Avx512F.TernaryLogic(a0, c4, rolC1, Xor3);
        a5 = Avx512F.TernaryLogic(a5, c4, rolC1, Xor3);
        a10 = Avx512F.TernaryLogic(a10, c4, rolC1, Xor3);
        a15 = Avx512F.TernaryLogic(a15, c4, rolC1, Xor3);
        a20 = Avx512F.TernaryLogic(a20, c4, rolC1, Xor3);

        Vector512<ulong> rolC2 = Avx512F.RotateLeft(c2, 1);
        a1 = Avx512F.TernaryLogic(a1, c0, rolC2, Xor3);
        a6 = Avx512F.TernaryLogic(a6, c0, rolC2, Xor3);
        a11 = Avx512F.TernaryLogic(a11, c0, rolC2, Xor3);
        a16 = Avx512F.TernaryLogic(a16, c0, rolC2, Xor3);
        a21 = Avx512F.TernaryLogic(a21, c0, rolC2, Xor3);

        Vector512<ulong> rolC3 = Avx512F.RotateLeft(c3, 1);
        a2 = Avx512F.TernaryLogic(a2, c1, rolC3, Xor3);
        a7 = Avx512F.TernaryLogic(a7, c1, rolC3, Xor3);
        a12 = Avx512F.TernaryLogic(a12, c1, rolC3, Xor3);
        a17 = Avx512F.TernaryLogic(a17, c1, rolC3, Xor3);
        a22 = Avx512F.TernaryLogic(a22, c1, rolC3, Xor3);

        Vector512<ulong> rolC4 = Avx512F.RotateLeft(c4, 1);
        a3 = Avx512F.TernaryLogic(a3, c2, rolC4, Xor3);
        a8 = Avx512F.TernaryLogic(a8, c2, rolC4, Xor3);
        a13 = Avx512F.TernaryLogic(a13, c2, rolC4, Xor3);
        a18 = Avx512F.TernaryLogic(a18, c2, rolC4, Xor3);
        a23 = Avx512F.TernaryLogic(a23, c2, rolC4, Xor3);

        Vector512<ulong> rolC0 = Avx512F.RotateLeft(c0, 1);
        a4 = Avx512F.TernaryLogic(a4, c3, rolC0, Xor3);
        a9 = Avx512F.TernaryLogic(a9, c3, rolC0, Xor3);
        a14 = Avx512F.TernaryLogic(a14, c3, rolC0, Xor3);
        a19 = Avx512F.TernaryLogic(a19, c3, rolC0, Xor3);
        a24 = Avx512F.TernaryLogic(a24, c3, rolC0, Xor3);

        // Rho + Pi: walk the single 24-lane Pi cycle, rotating each lane into its permuted
        // position; lane 0 is the cycle's fixed point. The two temporaries update the lanes
        // in place, which keeps all 25 lanes enregistered; a fresh local per lane instead
        // makes the JIT spill (measured ~2.6x slower).
        Vector512<ulong> source = a1;
        Vector512<ulong> displaced;
        displaced = a10;
        a10 = Avx512F.RotateLeft(source, 1);
        source = displaced;
        displaced = a7;
        a7 = Avx512F.RotateLeft(source, 3);
        source = displaced;
        displaced = a11;
        a11 = Avx512F.RotateLeft(source, 6);
        source = displaced;
        displaced = a17;
        a17 = Avx512F.RotateLeft(source, 10);
        source = displaced;
        displaced = a18;
        a18 = Avx512F.RotateLeft(source, 15);
        source = displaced;
        displaced = a3;
        a3 = Avx512F.RotateLeft(source, 21);
        source = displaced;
        displaced = a5;
        a5 = Avx512F.RotateLeft(source, 28);
        source = displaced;
        displaced = a16;
        a16 = Avx512F.RotateLeft(source, 36);
        source = displaced;
        displaced = a8;
        a8 = Avx512F.RotateLeft(source, 45);
        source = displaced;
        displaced = a21;
        a21 = Avx512F.RotateLeft(source, 55);
        source = displaced;
        displaced = a24;
        a24 = Avx512F.RotateLeft(source, 2);
        source = displaced;
        displaced = a4;
        a4 = Avx512F.RotateLeft(source, 14);
        source = displaced;
        displaced = a15;
        a15 = Avx512F.RotateLeft(source, 27);
        source = displaced;
        displaced = a23;
        a23 = Avx512F.RotateLeft(source, 41);
        source = displaced;
        displaced = a19;
        a19 = Avx512F.RotateLeft(source, 56);
        source = displaced;
        displaced = a13;
        a13 = Avx512F.RotateLeft(source, 8);
        source = displaced;
        displaced = a12;
        a12 = Avx512F.RotateLeft(source, 25);
        source = displaced;
        displaced = a2;
        a2 = Avx512F.RotateLeft(source, 43);
        source = displaced;
        displaced = a20;
        a20 = Avx512F.RotateLeft(source, 62);
        source = displaced;
        displaced = a14;
        a14 = Avx512F.RotateLeft(source, 18);
        source = displaced;
        displaced = a22;
        a22 = Avx512F.RotateLeft(source, 39);
        source = displaced;
        displaced = a9;
        a9 = Avx512F.RotateLeft(source, 61);
        source = displaced;
        displaced = a6;
        a6 = Avx512F.RotateLeft(source, 20);
        source = displaced;
        a1 = Avx512F.RotateLeft(source, 44);

        // Chi: A[x,y] = B[x,y] ^ (~B[x+1,y] & B[x+2,y]), applied in place one row at a time.
        ChiRow(ref a0, ref a1, ref a2, ref a3, ref a4);
        ChiRow(ref a5, ref a6, ref a7, ref a8, ref a9);
        ChiRow(ref a10, ref a11, ref a12, ref a13, ref a14);
        ChiRow(ref a15, ref a16, ref a17, ref a18, ref a19);
        ChiRow(ref a20, ref a21, ref a22, ref a23, ref a24);
        // Iota: fold the round constant into lane 0.
        a0 = Avx512F.Xor(a0, roundConstant);
    }

    /// <summary>Applies the Keccak chi mapping to one row of five lanes in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ChiRow(ref Vector512<ulong> a0, ref Vector512<ulong> a1, ref Vector512<ulong> a2,
        ref Vector512<ulong> a3, ref Vector512<ulong> a4)
    {
        Vector512<ulong> b0 = a0;
        Vector512<ulong> b1 = a1;
        a0 = Avx512F.TernaryLogic(a0, a1, a2, Chi);
        a1 = Avx512F.TernaryLogic(a1, a2, a3, Chi);
        a2 = Avx512F.TernaryLogic(a2, a3, a4, Chi);
        a3 = Avx512F.TernaryLogic(a3, a4, b0, Chi);
        a4 = Avx512F.TernaryLogic(a4, b0, b1, Chi);
    }
}
