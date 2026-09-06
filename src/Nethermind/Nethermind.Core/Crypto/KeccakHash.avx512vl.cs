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
    // A struct local preserves tiering and dynamic PGO. Stackalloc would pin the hashing method at
    // Tier0 FullOpts and add per-call GS-cookie and stack-probe checks.
    [InlineArray(STATE_LANES)]
    private struct KeccakStateX2
    {
        private Vector128<ulong> _lane0;
    }

    // vpternlog immediates: bit n of the immediate is the output for input bits (a, b, c) = binary n.
    private const byte Xor3 = 0x96; // a ^ b ^ c
    private const byte Chi = 0xD2; // a ^ (~b & c)

    /// <summary>AVX-512VL Keccak-f[1600] permutation.</summary>
    /// <param name="state">Lane 0 of a 25-lane state; all 25 lanes are read and written.</param>
    [SkipLocalsInit]
    internal static void KeccakF1600Avx512VL(ref ulong state)
    {
        Debug.Assert(Avx512F.VL.IsSupported);

        Vector128<ulong> a0 = Vector128.CreateScalarUnsafe(state);
        Vector128<ulong> a1 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 1));
        Vector128<ulong> a2 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 2));
        Vector128<ulong> a3 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 3));
        Vector128<ulong> a4 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 4));
        Vector128<ulong> a5 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 5));
        Vector128<ulong> a6 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 6));
        Vector128<ulong> a7 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 7));
        Vector128<ulong> a8 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 8));
        Vector128<ulong> a9 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 9));
        Vector128<ulong> a10 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 10));
        Vector128<ulong> a11 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 11));
        Vector128<ulong> a12 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 12));
        Vector128<ulong> a13 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 13));
        Vector128<ulong> a14 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 14));
        Vector128<ulong> a15 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 15));
        Vector128<ulong> a16 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 16));
        Vector128<ulong> a17 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 17));
        Vector128<ulong> a18 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 18));
        Vector128<ulong> a19 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 19));
        Vector128<ulong> a20 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 20));
        Vector128<ulong> a21 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 21));
        Vector128<ulong> a22 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 22));
        Vector128<ulong> a23 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 23));
        Vector128<ulong> a24 = Vector128.CreateScalarUnsafe(Unsafe.Add(ref state, 24));

        ref ulong roundConstants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);
        for (int round = 0; round < ROUNDS; round++)
        {
            RoundX1(
                ref a0, ref a1, ref a2, ref a3, ref a4,
                ref a5, ref a6, ref a7, ref a8, ref a9,
                ref a10, ref a11, ref a12, ref a13, ref a14,
                ref a15, ref a16, ref a17, ref a18, ref a19,
                ref a20, ref a21, ref a22, ref a23, ref a24,
                Vector128.CreateScalarUnsafe(Unsafe.Add(ref roundConstants, round)));
        }

        state = a0.GetElement(0);
        Unsafe.Add(ref state, 1) = a1.GetElement(0);
        Unsafe.Add(ref state, 2) = a2.GetElement(0);
        Unsafe.Add(ref state, 3) = a3.GetElement(0);
        Unsafe.Add(ref state, 4) = a4.GetElement(0);
        Unsafe.Add(ref state, 5) = a5.GetElement(0);
        Unsafe.Add(ref state, 6) = a6.GetElement(0);
        Unsafe.Add(ref state, 7) = a7.GetElement(0);
        Unsafe.Add(ref state, 8) = a8.GetElement(0);
        Unsafe.Add(ref state, 9) = a9.GetElement(0);
        Unsafe.Add(ref state, 10) = a10.GetElement(0);
        Unsafe.Add(ref state, 11) = a11.GetElement(0);
        Unsafe.Add(ref state, 12) = a12.GetElement(0);
        Unsafe.Add(ref state, 13) = a13.GetElement(0);
        Unsafe.Add(ref state, 14) = a14.GetElement(0);
        Unsafe.Add(ref state, 15) = a15.GetElement(0);
        Unsafe.Add(ref state, 16) = a16.GetElement(0);
        Unsafe.Add(ref state, 17) = a17.GetElement(0);
        Unsafe.Add(ref state, 18) = a18.GetElement(0);
        Unsafe.Add(ref state, 19) = a19.GetElement(0);
        Unsafe.Add(ref state, 20) = a20.GetElement(0);
        Unsafe.Add(ref state, 21) = a21.GetElement(0);
        Unsafe.Add(ref state, 22) = a22.GetElement(0);
        Unsafe.Add(ref state, 23) = a23.GetElement(0);
        Unsafe.Add(ref state, 24) = a24.GetElement(0);
    }

    /// <summary>Computes Keccak-256 for a supported one-block input with one Keccak lane per AVX-512VL vector.</summary>
    [SkipLocalsInit]
    private static void ComputeHash256Avx512VL(ref byte input, int inputLength, ref byte output)
    {
        Debug.Assert(Avx512F.VL.IsSupported);
        Debug.Assert(inputLength is Address.Size or 32 or 64);

        Vector128<ulong> a0 = LoadScalar128(ref input, 0);
        Vector128<ulong> a1 = LoadScalar128(ref input, 8);
        Vector128<ulong> a2 = Vector128<ulong>.Zero;
        Vector128<ulong> a3 = Vector128<ulong>.Zero;
        Vector128<ulong> a4 = Vector128<ulong>.Zero;
        Vector128<ulong> a5 = Vector128<ulong>.Zero;
        Vector128<ulong> a6 = Vector128<ulong>.Zero;
        Vector128<ulong> a7 = Vector128<ulong>.Zero;
        Vector128<ulong> a8 = Vector128<ulong>.Zero;
        Vector128<ulong> a9 = Vector128<ulong>.Zero;
        Vector128<ulong> a10 = Vector128<ulong>.Zero;
        Vector128<ulong> a11 = Vector128<ulong>.Zero;
        Vector128<ulong> a12 = Vector128<ulong>.Zero;
        Vector128<ulong> a13 = Vector128<ulong>.Zero;
        Vector128<ulong> a14 = Vector128<ulong>.Zero;
        Vector128<ulong> a15 = Vector128<ulong>.Zero;
        // Multi-rate padding (FIPS 202 sec. 5.1): 0x80 at byte 135, the last byte of the
        // 136-byte rate, is the top bit of lane 16; 0x01 goes right after the message below.
        Vector128<ulong> a16 = Vector128.CreateScalarUnsafe(0x8000000000000000UL);
        Vector128<ulong> a17 = Vector128<ulong>.Zero;
        Vector128<ulong> a18 = Vector128<ulong>.Zero;
        Vector128<ulong> a19 = Vector128<ulong>.Zero;
        Vector128<ulong> a20 = Vector128<ulong>.Zero;
        Vector128<ulong> a21 = Vector128<ulong>.Zero;
        Vector128<ulong> a22 = Vector128<ulong>.Zero;
        Vector128<ulong> a23 = Vector128<ulong>.Zero;
        Vector128<ulong> a24 = Vector128<ulong>.Zero;

        switch (inputLength)
        {
            case Address.Size:
                // Lane 2 holds the final 4 address bytes with the 0x01 pad in the byte above them (input offset 20).
                a2 = Vector128.CreateScalarUnsafe(
                    Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref input, 16)) | (1UL << 32));
                break;
            case 32:
                a2 = LoadScalar128(ref input, 16);
                a3 = LoadScalar128(ref input, 24);
                a4 = Vector128.CreateScalarUnsafe(1UL); // 0x01 pad at input offset 32
                break;
            case 64:
                a2 = LoadScalar128(ref input, 16);
                a3 = LoadScalar128(ref input, 24);
                a4 = LoadScalar128(ref input, 32);
                a5 = LoadScalar128(ref input, 40);
                a6 = LoadScalar128(ref input, 48);
                a7 = LoadScalar128(ref input, 56);
                a8 = Vector128.CreateScalarUnsafe(1UL); // 0x01 pad at input offset 64
                break;
        }

        ref ulong roundConstants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);
        for (int round = 0; round < ROUNDS; round++)
        {
            RoundX1(
                ref a0, ref a1, ref a2, ref a3, ref a4,
                ref a5, ref a6, ref a7, ref a8, ref a9,
                ref a10, ref a11, ref a12, ref a13, ref a14,
                ref a15, ref a16, ref a17, ref a18, ref a19,
                ref a20, ref a21, ref a22, ref a23, ref a24,
                Vector128.CreateScalarUnsafe(Unsafe.Add(ref roundConstants, round)));
        }

        Unsafe.WriteUnaligned(ref output, a0.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, 8), a1.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, 16), a2.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, 24), a3.GetElement(0));
    }

    /// <summary>Hashes two 532-byte inputs into two consecutive 32-byte outputs.</summary>
    /// <remarks>The caller must ensure that AVX-512VL is supported and both inputs have the required fixed size.</remarks>
    [SkipLocalsInit]
    internal static void ComputeHash532Bytes2Avx512VL(ref byte input0, ref byte input1, ref byte output)
    {
        Debug.Assert(Avx512F.VL.IsSupported);

        KeccakStateX2 stateBuffer = default;
        ref Vector128<ulong> state = ref stateBuffer[0];
        for (int blockOffset = 0; blockOffset < 3 * HASH_DATA_AREA; blockOffset += HASH_DATA_AREA)
        {
            for (int lane = 0; lane < HASH_DATA_AREA / sizeof(ulong); lane++)
            {
                int offset = blockOffset + lane * sizeof(ulong);
                Unsafe.Add(ref state, lane) ^= LoadPair(ref input0, ref input1, offset);
            }

            KeccakF1600x2Avx512VL(ref state);
        }

        const int finalBlockOffset = 3 * HASH_DATA_AREA;
        for (int lane = 0; lane < 15; lane++)
        {
            int offset = finalBlockOffset + lane * sizeof(ulong);
            Unsafe.Add(ref state, lane) ^= LoadPair(ref input0, ref input1, offset);
        }

        Unsafe.Add(ref state, 15) ^= Vector128.Create(
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref input0, finalBlockOffset + 120)) | (1UL << 32),
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref input1, finalBlockOffset + 120)) | (1UL << 32));
        Unsafe.Add(ref state, 16) ^= Vector128.Create(0x8000000000000000UL);
        KeccakF1600x2Avx512VL(ref state);

        StoreHashPair(ref output, 0, state, Unsafe.Add(ref state, 1), Unsafe.Add(ref state, 2), Unsafe.Add(ref state, 3));
        StoreHashPair(ref output, 1, state, Unsafe.Add(ref state, 1), Unsafe.Add(ref state, 2), Unsafe.Add(ref state, 3));
    }

    [SkipLocalsInit]
    private static void KeccakF1600x2Avx512VL(ref Vector128<ulong> state)
    {
        Vector128<ulong> a0 = state;
        Vector128<ulong> a1 = Unsafe.Add(ref state, 1);
        Vector128<ulong> a2 = Unsafe.Add(ref state, 2);
        Vector128<ulong> a3 = Unsafe.Add(ref state, 3);
        Vector128<ulong> a4 = Unsafe.Add(ref state, 4);
        Vector128<ulong> a5 = Unsafe.Add(ref state, 5);
        Vector128<ulong> a6 = Unsafe.Add(ref state, 6);
        Vector128<ulong> a7 = Unsafe.Add(ref state, 7);
        Vector128<ulong> a8 = Unsafe.Add(ref state, 8);
        Vector128<ulong> a9 = Unsafe.Add(ref state, 9);
        Vector128<ulong> a10 = Unsafe.Add(ref state, 10);
        Vector128<ulong> a11 = Unsafe.Add(ref state, 11);
        Vector128<ulong> a12 = Unsafe.Add(ref state, 12);
        Vector128<ulong> a13 = Unsafe.Add(ref state, 13);
        Vector128<ulong> a14 = Unsafe.Add(ref state, 14);
        Vector128<ulong> a15 = Unsafe.Add(ref state, 15);
        Vector128<ulong> a16 = Unsafe.Add(ref state, 16);
        Vector128<ulong> a17 = Unsafe.Add(ref state, 17);
        Vector128<ulong> a18 = Unsafe.Add(ref state, 18);
        Vector128<ulong> a19 = Unsafe.Add(ref state, 19);
        Vector128<ulong> a20 = Unsafe.Add(ref state, 20);
        Vector128<ulong> a21 = Unsafe.Add(ref state, 21);
        Vector128<ulong> a22 = Unsafe.Add(ref state, 22);
        Vector128<ulong> a23 = Unsafe.Add(ref state, 23);
        Vector128<ulong> a24 = Unsafe.Add(ref state, 24);

        ref ulong roundConstants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);
        for (int round = 0; round < ROUNDS; round++)
        {
            RoundX1(
                ref a0, ref a1, ref a2, ref a3, ref a4,
                ref a5, ref a6, ref a7, ref a8, ref a9,
                ref a10, ref a11, ref a12, ref a13, ref a14,
                ref a15, ref a16, ref a17, ref a18, ref a19,
                ref a20, ref a21, ref a22, ref a23, ref a24,
                Vector128.Create(Unsafe.Add(ref roundConstants, round)));
        }

        state = a0;
        Unsafe.Add(ref state, 1) = a1;
        Unsafe.Add(ref state, 2) = a2;
        Unsafe.Add(ref state, 3) = a3;
        Unsafe.Add(ref state, 4) = a4;
        Unsafe.Add(ref state, 5) = a5;
        Unsafe.Add(ref state, 6) = a6;
        Unsafe.Add(ref state, 7) = a7;
        Unsafe.Add(ref state, 8) = a8;
        Unsafe.Add(ref state, 9) = a9;
        Unsafe.Add(ref state, 10) = a10;
        Unsafe.Add(ref state, 11) = a11;
        Unsafe.Add(ref state, 12) = a12;
        Unsafe.Add(ref state, 13) = a13;
        Unsafe.Add(ref state, 14) = a14;
        Unsafe.Add(ref state, 15) = a15;
        Unsafe.Add(ref state, 16) = a16;
        Unsafe.Add(ref state, 17) = a17;
        Unsafe.Add(ref state, 18) = a18;
        Unsafe.Add(ref state, 19) = a19;
        Unsafe.Add(ref state, 20) = a20;
        Unsafe.Add(ref state, 21) = a21;
        Unsafe.Add(ref state, 22) = a22;
        Unsafe.Add(ref state, 23) = a23;
        Unsafe.Add(ref state, 24) = a24;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> LoadPair(ref byte input0, ref byte input1, int offset) =>
        Vector128.Create(
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input0, offset)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input1, offset)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreHashPair(ref byte output, int hashIndex,
        Vector128<ulong> a0, Vector128<ulong> a1, Vector128<ulong> a2, Vector128<ulong> a3)
    {
        ref byte destination = ref Unsafe.Add(ref output, hashIndex * HASH_SIZE);
        Unsafe.WriteUnaligned(ref destination, a0.GetElement(hashIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), a1.GetElement(hashIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), a2.GetElement(hashIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 24), a3.GetElement(hashIndex));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> LoadScalar128(ref byte input, int offset) =>
        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, offset)));

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RoundX1(
        ref Vector128<ulong> a0, ref Vector128<ulong> a1, ref Vector128<ulong> a2, ref Vector128<ulong> a3, ref Vector128<ulong> a4,
        ref Vector128<ulong> a5, ref Vector128<ulong> a6, ref Vector128<ulong> a7, ref Vector128<ulong> a8, ref Vector128<ulong> a9,
        ref Vector128<ulong> a10, ref Vector128<ulong> a11, ref Vector128<ulong> a12, ref Vector128<ulong> a13, ref Vector128<ulong> a14,
        ref Vector128<ulong> a15, ref Vector128<ulong> a16, ref Vector128<ulong> a17, ref Vector128<ulong> a18, ref Vector128<ulong> a19,
        ref Vector128<ulong> a20, ref Vector128<ulong> a21, ref Vector128<ulong> a22, ref Vector128<ulong> a23, ref Vector128<ulong> a24,
        Vector128<ulong> roundConstant)
    {
        // Theta: column parities C[x] = A[x,0] ^ A[x,1] ^ A[x,2] ^ A[x,3] ^ A[x,4].
        Vector128<ulong> c0 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a0, a5, a10, Xor3), a15, a20, Xor3);
        Vector128<ulong> c1 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a1, a6, a11, Xor3), a16, a21, Xor3);
        Vector128<ulong> c2 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a2, a7, a12, Xor3), a17, a22, Xor3);
        Vector128<ulong> c3 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a3, a8, a13, Xor3), a18, a23, Xor3);
        Vector128<ulong> c4 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a4, a9, a14, Xor3), a19, a24, Xor3);

        // Theta: A[x,y] ^= C[x-1] ^ ROL(C[x+1], 1); both XORs fuse into one ternary op per lane.
        Vector128<ulong> rolC1 = Avx512F.VL.RotateLeft(c1, 1);
        a0 = Avx512F.VL.TernaryLogic(a0, c4, rolC1, Xor3);
        a5 = Avx512F.VL.TernaryLogic(a5, c4, rolC1, Xor3);
        a10 = Avx512F.VL.TernaryLogic(a10, c4, rolC1, Xor3);
        a15 = Avx512F.VL.TernaryLogic(a15, c4, rolC1, Xor3);
        a20 = Avx512F.VL.TernaryLogic(a20, c4, rolC1, Xor3);

        Vector128<ulong> rolC2 = Avx512F.VL.RotateLeft(c2, 1);
        a1 = Avx512F.VL.TernaryLogic(a1, c0, rolC2, Xor3);
        a6 = Avx512F.VL.TernaryLogic(a6, c0, rolC2, Xor3);
        a11 = Avx512F.VL.TernaryLogic(a11, c0, rolC2, Xor3);
        a16 = Avx512F.VL.TernaryLogic(a16, c0, rolC2, Xor3);
        a21 = Avx512F.VL.TernaryLogic(a21, c0, rolC2, Xor3);

        Vector128<ulong> rolC3 = Avx512F.VL.RotateLeft(c3, 1);
        a2 = Avx512F.VL.TernaryLogic(a2, c1, rolC3, Xor3);
        a7 = Avx512F.VL.TernaryLogic(a7, c1, rolC3, Xor3);
        a12 = Avx512F.VL.TernaryLogic(a12, c1, rolC3, Xor3);
        a17 = Avx512F.VL.TernaryLogic(a17, c1, rolC3, Xor3);
        a22 = Avx512F.VL.TernaryLogic(a22, c1, rolC3, Xor3);

        Vector128<ulong> rolC4 = Avx512F.VL.RotateLeft(c4, 1);
        a3 = Avx512F.VL.TernaryLogic(a3, c2, rolC4, Xor3);
        a8 = Avx512F.VL.TernaryLogic(a8, c2, rolC4, Xor3);
        a13 = Avx512F.VL.TernaryLogic(a13, c2, rolC4, Xor3);
        a18 = Avx512F.VL.TernaryLogic(a18, c2, rolC4, Xor3);
        a23 = Avx512F.VL.TernaryLogic(a23, c2, rolC4, Xor3);

        Vector128<ulong> rolC0 = Avx512F.VL.RotateLeft(c0, 1);
        a4 = Avx512F.VL.TernaryLogic(a4, c3, rolC0, Xor3);
        a9 = Avx512F.VL.TernaryLogic(a9, c3, rolC0, Xor3);
        a14 = Avx512F.VL.TernaryLogic(a14, c3, rolC0, Xor3);
        a19 = Avx512F.VL.TernaryLogic(a19, c3, rolC0, Xor3);
        a24 = Avx512F.VL.TernaryLogic(a24, c3, rolC0, Xor3);

        // Rho + Pi: walk the single 24-lane Pi cycle, rotating each lane into its permuted
        // position; lane 0 is the cycle's fixed point. The two temporaries update the lanes
        // in place, which keeps all 25 lanes enregistered; a fresh local per lane instead
        // makes the JIT spill (measured ~2.6x slower).
        Vector128<ulong> source = a1;
        Vector128<ulong> displaced;
        displaced = a10;
        a10 = Avx512F.VL.RotateLeft(source, 1);
        source = displaced;
        displaced = a7;
        a7 = Avx512F.VL.RotateLeft(source, 3);
        source = displaced;
        displaced = a11;
        a11 = Avx512F.VL.RotateLeft(source, 6);
        source = displaced;
        displaced = a17;
        a17 = Avx512F.VL.RotateLeft(source, 10);
        source = displaced;
        displaced = a18;
        a18 = Avx512F.VL.RotateLeft(source, 15);
        source = displaced;
        displaced = a3;
        a3 = Avx512F.VL.RotateLeft(source, 21);
        source = displaced;
        displaced = a5;
        a5 = Avx512F.VL.RotateLeft(source, 28);
        source = displaced;
        displaced = a16;
        a16 = Avx512F.VL.RotateLeft(source, 36);
        source = displaced;
        displaced = a8;
        a8 = Avx512F.VL.RotateLeft(source, 45);
        source = displaced;
        displaced = a21;
        a21 = Avx512F.VL.RotateLeft(source, 55);
        source = displaced;
        displaced = a24;
        a24 = Avx512F.VL.RotateLeft(source, 2);
        source = displaced;
        displaced = a4;
        a4 = Avx512F.VL.RotateLeft(source, 14);
        source = displaced;
        displaced = a15;
        a15 = Avx512F.VL.RotateLeft(source, 27);
        source = displaced;
        displaced = a23;
        a23 = Avx512F.VL.RotateLeft(source, 41);
        source = displaced;
        displaced = a19;
        a19 = Avx512F.VL.RotateLeft(source, 56);
        source = displaced;
        displaced = a13;
        a13 = Avx512F.VL.RotateLeft(source, 8);
        source = displaced;
        displaced = a12;
        a12 = Avx512F.VL.RotateLeft(source, 25);
        source = displaced;
        displaced = a2;
        a2 = Avx512F.VL.RotateLeft(source, 43);
        source = displaced;
        displaced = a20;
        a20 = Avx512F.VL.RotateLeft(source, 62);
        source = displaced;
        displaced = a14;
        a14 = Avx512F.VL.RotateLeft(source, 18);
        source = displaced;
        displaced = a22;
        a22 = Avx512F.VL.RotateLeft(source, 39);
        source = displaced;
        displaced = a9;
        a9 = Avx512F.VL.RotateLeft(source, 61);
        source = displaced;
        displaced = a6;
        a6 = Avx512F.VL.RotateLeft(source, 20);
        source = displaced;
        a1 = Avx512F.VL.RotateLeft(source, 44);

        // Chi: A[x,y] = B[x,y] ^ (~B[x+1,y] & B[x+2,y]), applied in place one row at a time.
        ChiRowX1(ref a0, ref a1, ref a2, ref a3, ref a4);
        ChiRowX1(ref a5, ref a6, ref a7, ref a8, ref a9);
        ChiRowX1(ref a10, ref a11, ref a12, ref a13, ref a14);
        ChiRowX1(ref a15, ref a16, ref a17, ref a18, ref a19);
        ChiRowX1(ref a20, ref a21, ref a22, ref a23, ref a24);
        // Iota: fold the round constant into lane 0.
        a0 = Vector128.Xor(a0, roundConstant);
    }

    /// <summary>Applies the Keccak chi mapping to one row of five lanes in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ChiRowX1(ref Vector128<ulong> a0, ref Vector128<ulong> a1, ref Vector128<ulong> a2,
        ref Vector128<ulong> a3, ref Vector128<ulong> a4)
    {
        Vector128<ulong> b0 = a0;
        Vector128<ulong> b1 = a1;
        a0 = Avx512F.VL.TernaryLogic(a0, a1, a2, Chi);
        a1 = Avx512F.VL.TernaryLogic(a1, a2, a3, Chi);
        a2 = Avx512F.VL.TernaryLogic(a2, a3, a4, Chi);
        a3 = Avx512F.VL.TernaryLogic(a3, a4, b0, Chi);
        a4 = Avx512F.VL.TernaryLogic(a4, b0, b1, Chi);
    }
}
