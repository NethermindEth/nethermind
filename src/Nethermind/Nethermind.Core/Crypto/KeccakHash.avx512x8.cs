// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;

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
            a0 = Gather64(inputPtr, 0);
            a1 = Gather64(inputPtr, 1);
            a2 = Gather64(inputPtr, 2);
            a3 = Gather64(inputPtr, 3);
            a4 = Gather64(inputPtr, 4);
            a5 = Gather64(inputPtr, 5);
            a6 = Gather64(inputPtr, 6);
            a7 = Gather64(inputPtr, 7);
        }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector512<ulong> Gather64(byte* input, int lane)
    {
        ulong* lanePtr = (ulong*)(input + lane * sizeof(ulong));
        Vector256<ulong> lower = Avx2.GatherVector256(lanePtr, Vector256.Create(0L, 8L, 16L, 24L), 8);
        Vector256<ulong> upper = Avx2.GatherVector256(lanePtr, Vector256.Create(32L, 40L, 48L, 56L), 8);
        return Vector512.Create(lower, upper);
    }

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
        Vector512<ulong> c0 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a0, a5, a10, 0x96), a15, a20, 0x96);
        Vector512<ulong> c1 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a1, a6, a11, 0x96), a16, a21, 0x96);
        Vector512<ulong> c2 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a2, a7, a12, 0x96), a17, a22, 0x96);
        Vector512<ulong> c3 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a3, a8, a13, 0x96), a18, a23, 0x96);
        Vector512<ulong> c4 = Avx512F.TernaryLogic(Avx512F.TernaryLogic(a4, a9, a14, 0x96), a19, a24, 0x96);

        Vector512<ulong> next = Avx512F.RotateLeft(c1, 1);
        a0 = Avx512F.TernaryLogic(a0, c4, next, 0x96);
        a5 = Avx512F.TernaryLogic(a5, c4, next, 0x96);
        a10 = Avx512F.TernaryLogic(a10, c4, next, 0x96);
        a15 = Avx512F.TernaryLogic(a15, c4, next, 0x96);
        a20 = Avx512F.TernaryLogic(a20, c4, next, 0x96);

        next = Avx512F.RotateLeft(c2, 1);
        a1 = Avx512F.TernaryLogic(a1, c0, next, 0x96);
        a6 = Avx512F.TernaryLogic(a6, c0, next, 0x96);
        a11 = Avx512F.TernaryLogic(a11, c0, next, 0x96);
        a16 = Avx512F.TernaryLogic(a16, c0, next, 0x96);
        a21 = Avx512F.TernaryLogic(a21, c0, next, 0x96);

        next = Avx512F.RotateLeft(c3, 1);
        a2 = Avx512F.TernaryLogic(a2, c1, next, 0x96);
        a7 = Avx512F.TernaryLogic(a7, c1, next, 0x96);
        a12 = Avx512F.TernaryLogic(a12, c1, next, 0x96);
        a17 = Avx512F.TernaryLogic(a17, c1, next, 0x96);
        a22 = Avx512F.TernaryLogic(a22, c1, next, 0x96);

        next = Avx512F.RotateLeft(c4, 1);
        a3 = Avx512F.TernaryLogic(a3, c2, next, 0x96);
        a8 = Avx512F.TernaryLogic(a8, c2, next, 0x96);
        a13 = Avx512F.TernaryLogic(a13, c2, next, 0x96);
        a18 = Avx512F.TernaryLogic(a18, c2, next, 0x96);
        a23 = Avx512F.TernaryLogic(a23, c2, next, 0x96);

        next = Avx512F.RotateLeft(c0, 1);
        a4 = Avx512F.TernaryLogic(a4, c3, next, 0x96);
        a9 = Avx512F.TernaryLogic(a9, c3, next, 0x96);
        a14 = Avx512F.TernaryLogic(a14, c3, next, 0x96);
        a19 = Avx512F.TernaryLogic(a19, c3, next, 0x96);
        a24 = Avx512F.TernaryLogic(a24, c3, next, 0x96);

        Vector512<ulong> current = a1;
        Vector512<ulong> temp = a10; a10 = Avx512F.RotateLeft(current, 1); current = temp;
        temp = a7; a7 = Avx512F.RotateLeft(current, 3); current = temp;
        temp = a11; a11 = Avx512F.RotateLeft(current, 6); current = temp;
        temp = a17; a17 = Avx512F.RotateLeft(current, 10); current = temp;
        temp = a18; a18 = Avx512F.RotateLeft(current, 15); current = temp;
        temp = a3; a3 = Avx512F.RotateLeft(current, 21); current = temp;
        temp = a5; a5 = Avx512F.RotateLeft(current, 28); current = temp;
        temp = a16; a16 = Avx512F.RotateLeft(current, 36); current = temp;
        temp = a8; a8 = Avx512F.RotateLeft(current, 45); current = temp;
        temp = a21; a21 = Avx512F.RotateLeft(current, 55); current = temp;
        temp = a24; a24 = Avx512F.RotateLeft(current, 2); current = temp;
        temp = a4; a4 = Avx512F.RotateLeft(current, 14); current = temp;
        temp = a15; a15 = Avx512F.RotateLeft(current, 27); current = temp;
        temp = a23; a23 = Avx512F.RotateLeft(current, 41); current = temp;
        temp = a19; a19 = Avx512F.RotateLeft(current, 56); current = temp;
        temp = a13; a13 = Avx512F.RotateLeft(current, 8); current = temp;
        temp = a12; a12 = Avx512F.RotateLeft(current, 25); current = temp;
        temp = a2; a2 = Avx512F.RotateLeft(current, 43); current = temp;
        temp = a20; a20 = Avx512F.RotateLeft(current, 62); current = temp;
        temp = a14; a14 = Avx512F.RotateLeft(current, 18); current = temp;
        temp = a22; a22 = Avx512F.RotateLeft(current, 39); current = temp;
        temp = a9; a9 = Avx512F.RotateLeft(current, 61); current = temp;
        temp = a6; a6 = Avx512F.RotateLeft(current, 20); current = temp;
        a1 = Avx512F.RotateLeft(current, 44);

        ChiRow(ref a0, ref a1, ref a2, ref a3, ref a4);
        ChiRow(ref a5, ref a6, ref a7, ref a8, ref a9);
        ChiRow(ref a10, ref a11, ref a12, ref a13, ref a14);
        ChiRow(ref a15, ref a16, ref a17, ref a18, ref a19);
        ChiRow(ref a20, ref a21, ref a22, ref a23, ref a24);
        a0 = Avx512F.Xor(a0, roundConstant);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ChiRow(ref Vector512<ulong> a0, ref Vector512<ulong> a1, ref Vector512<ulong> a2,
        ref Vector512<ulong> a3, ref Vector512<ulong> a4)
    {
        Vector512<ulong> b0 = a0;
        Vector512<ulong> b1 = a1;
        a0 = Avx512F.TernaryLogic(a0, a1, a2, 0xD2);
        a1 = Avx512F.TernaryLogic(a1, a2, a3, 0xD2);
        a2 = Avx512F.TernaryLogic(a2, a3, a4, 0xD2);
        a3 = Avx512F.TernaryLogic(a3, a4, b0, 0xD2);
        a4 = Avx512F.TernaryLogic(a4, b0, b1, 0xD2);
    }
}
