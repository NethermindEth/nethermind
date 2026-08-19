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
                a2 = Vector128.CreateScalarUnsafe(
                    Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref input, 16)) | (1UL << 32));
                break;
            case 32:
                a2 = LoadScalar128(ref input, 16);
                a3 = LoadScalar128(ref input, 24);
                a4 = Vector128.CreateScalarUnsafe(1UL);
                break;
            case 64:
                a2 = LoadScalar128(ref input, 16);
                a3 = LoadScalar128(ref input, 24);
                a4 = LoadScalar128(ref input, 32);
                a5 = LoadScalar128(ref input, 40);
                a6 = LoadScalar128(ref input, 48);
                a7 = LoadScalar128(ref input, 56);
                a8 = Vector128.CreateScalarUnsafe(1UL);
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
        Vector128<ulong> c0 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a0, a5, a10, 0x96), a15, a20, 0x96);
        Vector128<ulong> c1 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a1, a6, a11, 0x96), a16, a21, 0x96);
        Vector128<ulong> c2 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a2, a7, a12, 0x96), a17, a22, 0x96);
        Vector128<ulong> c3 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a3, a8, a13, 0x96), a18, a23, 0x96);
        Vector128<ulong> c4 = Avx512F.VL.TernaryLogic(Avx512F.VL.TernaryLogic(a4, a9, a14, 0x96), a19, a24, 0x96);

        Vector128<ulong> next = Avx512F.VL.RotateLeft(c1, 1);
        a0 = Avx512F.VL.TernaryLogic(a0, c4, next, 0x96);
        a5 = Avx512F.VL.TernaryLogic(a5, c4, next, 0x96);
        a10 = Avx512F.VL.TernaryLogic(a10, c4, next, 0x96);
        a15 = Avx512F.VL.TernaryLogic(a15, c4, next, 0x96);
        a20 = Avx512F.VL.TernaryLogic(a20, c4, next, 0x96);

        next = Avx512F.VL.RotateLeft(c2, 1);
        a1 = Avx512F.VL.TernaryLogic(a1, c0, next, 0x96);
        a6 = Avx512F.VL.TernaryLogic(a6, c0, next, 0x96);
        a11 = Avx512F.VL.TernaryLogic(a11, c0, next, 0x96);
        a16 = Avx512F.VL.TernaryLogic(a16, c0, next, 0x96);
        a21 = Avx512F.VL.TernaryLogic(a21, c0, next, 0x96);

        next = Avx512F.VL.RotateLeft(c3, 1);
        a2 = Avx512F.VL.TernaryLogic(a2, c1, next, 0x96);
        a7 = Avx512F.VL.TernaryLogic(a7, c1, next, 0x96);
        a12 = Avx512F.VL.TernaryLogic(a12, c1, next, 0x96);
        a17 = Avx512F.VL.TernaryLogic(a17, c1, next, 0x96);
        a22 = Avx512F.VL.TernaryLogic(a22, c1, next, 0x96);

        next = Avx512F.VL.RotateLeft(c4, 1);
        a3 = Avx512F.VL.TernaryLogic(a3, c2, next, 0x96);
        a8 = Avx512F.VL.TernaryLogic(a8, c2, next, 0x96);
        a13 = Avx512F.VL.TernaryLogic(a13, c2, next, 0x96);
        a18 = Avx512F.VL.TernaryLogic(a18, c2, next, 0x96);
        a23 = Avx512F.VL.TernaryLogic(a23, c2, next, 0x96);

        next = Avx512F.VL.RotateLeft(c0, 1);
        a4 = Avx512F.VL.TernaryLogic(a4, c3, next, 0x96);
        a9 = Avx512F.VL.TernaryLogic(a9, c3, next, 0x96);
        a14 = Avx512F.VL.TernaryLogic(a14, c3, next, 0x96);
        a19 = Avx512F.VL.TernaryLogic(a19, c3, next, 0x96);
        a24 = Avx512F.VL.TernaryLogic(a24, c3, next, 0x96);

        Vector128<ulong> current = a1;
        Vector128<ulong> temp = a10; a10 = Avx512F.VL.RotateLeft(current, 1); current = temp;
        temp = a7; a7 = Avx512F.VL.RotateLeft(current, 3); current = temp;
        temp = a11; a11 = Avx512F.VL.RotateLeft(current, 6); current = temp;
        temp = a17; a17 = Avx512F.VL.RotateLeft(current, 10); current = temp;
        temp = a18; a18 = Avx512F.VL.RotateLeft(current, 15); current = temp;
        temp = a3; a3 = Avx512F.VL.RotateLeft(current, 21); current = temp;
        temp = a5; a5 = Avx512F.VL.RotateLeft(current, 28); current = temp;
        temp = a16; a16 = Avx512F.VL.RotateLeft(current, 36); current = temp;
        temp = a8; a8 = Avx512F.VL.RotateLeft(current, 45); current = temp;
        temp = a21; a21 = Avx512F.VL.RotateLeft(current, 55); current = temp;
        temp = a24; a24 = Avx512F.VL.RotateLeft(current, 2); current = temp;
        temp = a4; a4 = Avx512F.VL.RotateLeft(current, 14); current = temp;
        temp = a15; a15 = Avx512F.VL.RotateLeft(current, 27); current = temp;
        temp = a23; a23 = Avx512F.VL.RotateLeft(current, 41); current = temp;
        temp = a19; a19 = Avx512F.VL.RotateLeft(current, 56); current = temp;
        temp = a13; a13 = Avx512F.VL.RotateLeft(current, 8); current = temp;
        temp = a12; a12 = Avx512F.VL.RotateLeft(current, 25); current = temp;
        temp = a2; a2 = Avx512F.VL.RotateLeft(current, 43); current = temp;
        temp = a20; a20 = Avx512F.VL.RotateLeft(current, 62); current = temp;
        temp = a14; a14 = Avx512F.VL.RotateLeft(current, 18); current = temp;
        temp = a22; a22 = Avx512F.VL.RotateLeft(current, 39); current = temp;
        temp = a9; a9 = Avx512F.VL.RotateLeft(current, 61); current = temp;
        temp = a6; a6 = Avx512F.VL.RotateLeft(current, 20); current = temp;
        a1 = Avx512F.VL.RotateLeft(current, 44);

        ChiRowX1(ref a0, ref a1, ref a2, ref a3, ref a4);
        ChiRowX1(ref a5, ref a6, ref a7, ref a8, ref a9);
        ChiRowX1(ref a10, ref a11, ref a12, ref a13, ref a14);
        ChiRowX1(ref a15, ref a16, ref a17, ref a18, ref a19);
        ChiRowX1(ref a20, ref a21, ref a22, ref a23, ref a24);
        a0 = Vector128.Xor(a0, roundConstant);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ChiRowX1(ref Vector128<ulong> a0, ref Vector128<ulong> a1, ref Vector128<ulong> a2,
        ref Vector128<ulong> a3, ref Vector128<ulong> a4)
    {
        Vector128<ulong> b0 = a0;
        Vector128<ulong> b1 = a1;
        a0 = Avx512F.VL.TernaryLogic(a0, a1, a2, 0xD2);
        a1 = Avx512F.VL.TernaryLogic(a1, a2, a3, 0xD2);
        a2 = Avx512F.VL.TernaryLogic(a2, a3, a4, 0xD2);
        a3 = Avx512F.VL.TernaryLogic(a3, a4, b0, 0xD2);
        a4 = Avx512F.VL.TernaryLogic(a4, b0, b1, 0xD2);
    }
}
