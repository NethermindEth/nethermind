// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    /// <summary>Hashes eight consecutive 64-byte inputs into eight consecutive 32-byte outputs.</summary>
    /// <remarks>The caller must ensure that AVX-512F is supported and that both buffers have the required fixed size.</remarks>
    [SkipLocalsInit]
    internal static unsafe void ComputeHash64Bytes8Avx512(ref byte input, ref byte output)
    {
        Debug.Assert(Avx512F.IsSupported);

        Vector512<ulong> a0;
        Vector512<ulong> a1;
        Vector512<ulong> a2;
        Vector512<ulong> a3;
        Vector512<ulong> a4;
        Vector512<ulong> a5;
        Vector512<ulong> a6;
        Vector512<ulong> a7;
        fixed (byte* inputPtr = &input)
        {
            a0 = GatherLane(inputPtr, 0);
            a1 = GatherLane(inputPtr, 1);
            a2 = GatherLane(inputPtr, 2);
            a3 = GatherLane(inputPtr, 3);
            a4 = GatherLane(inputPtr, 4);
            a5 = GatherLane(inputPtr, 5);
            a6 = GatherLane(inputPtr, 6);
            a7 = GatherLane(inputPtr, 7);
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

        StoreHash(ref output, 0, a0, a1, a2, a3);
        StoreHash(ref output, 1, a0, a1, a2, a3);
        StoreHash(ref output, 2, a0, a1, a2, a3);
        StoreHash(ref output, 3, a0, a1, a2, a3);
        StoreHash(ref output, 4, a0, a1, a2, a3);
        StoreHash(ref output, 5, a0, a1, a2, a3);
        StoreHash(ref output, 6, a0, a1, a2, a3);
        StoreHash(ref output, 7, a0, a1, a2, a3);
    }

    /// <summary>Gathers Keccak lane <paramref name="lane"/> from each of the eight consecutive 64-byte inputs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector512<ulong> GatherLane(byte* input, int lane)
    {
        ulong* lanePtr = (ulong*)(input + lane * sizeof(ulong));
        Vector256<ulong> lower = Avx2.GatherVector256(lanePtr, Vector256.Create(0L, 8L, 16L, 24L), 8);
        Vector256<ulong> upper = Avx2.GatherVector256(lanePtr, Vector256.Create(32L, 40L, 48L, 56L), 8);
        return Vector512.Create(lower, upper);
    }

    /// <summary>Writes the 32-byte hash of batch element <paramref name="hashIndex"/> from the first four state lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreHash(ref byte output, int hashIndex,
        Vector512<ulong> a0, Vector512<ulong> a1, Vector512<ulong> a2, Vector512<ulong> a3)
    {
        ref byte destination = ref Unsafe.Add(ref output, hashIndex * 32);
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
