// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Crypto.Blake2;

/// <summary>
///     Code adapted from Blake2Fast (https://github.com/saucecontrol/Blake2Fast)
/// </summary>
public unsafe partial class Blake2Compression
{
    // SIMD algorithm described in https://eprint.iacr.org/2012/275.pdf
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [SkipLocalsInit]
    private static void ComputeAvx2(ulong* sh, ulong* m, uint rounds)
    {
        // Rotate shuffle masks. We can safely convert the ref to a pointer because the compiler guarantees the
        // data is in a fixed location, and the ref itself is converted from a pointer. Same for the IV below.
        byte* prm = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Rormask));
        var r24 = Avx2.BroadcastVector128ToVector256(prm);
        var r16 = Avx2.BroadcastVector128ToVector256(prm + Vector128<byte>.Count);

        var row1 = Avx.LoadVector256(sh);
        var row2 = Avx.LoadVector256(sh + Vector256<ulong>.Count);

        ulong* piv = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Ivle));
        var row3 = Avx.LoadVector256(piv);
        var row4 = Avx.LoadVector256(piv + Vector256<ulong>.Count);

        row4 = Avx2.Xor(row4, Avx.LoadVector256(sh + Vector256<ulong>.Count * 2)); // t[] and f[]

        var m0 = Avx2.BroadcastVector128ToVector256(m);
        var m1 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count);
        var m2 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 2);
        var m3 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 3);
        var m4 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 4);
        var m5 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 5);
        var m6 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 6);
        var m7 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 7);
        Vector256<ulong> t0;
        Vector256<ulong> t1;
        Vector256<ulong> b0;

        for (uint i = 0; i < rounds; i++)
        {
            //ROUND 1
            t0 = Avx2.UnpackLow(m0, m1);
            t1 = Avx2.UnpackLow(m2, m3);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackHigh(m0, m1);
            t1 = Avx2.UnpackHigh(m2, m3);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.UnpackLow(m7, m4);
            t1 = Avx2.UnpackLow(m5, m6);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackHigh(m7, m4);
            t1 = Avx2.UnpackHigh(m5, m6);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);

            if (++i == rounds) break;

            //ROUND 2
            t0 = Avx2.UnpackLow(m7, m2);
            t1 = Avx2.UnpackHigh(m4, m6);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackLow(m5, m4);
            t1 = Avx2.AlignRight(m3, m7, 8);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.UnpackHigh(m2, m0);
            t1 = Avx2.Blend(m0.AsUInt32(), m5.AsUInt32(), 0b_1100_1100).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.AlignRight(m6, m1, 8);
            t1 = Avx2.Blend(m1.AsUInt32(), m3.AsUInt32(), 0b_1100_1100).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);

            if (++i == rounds) break;

            //ROUND 3
            t0 = Avx2.AlignRight(m6, m5, 8);
            t1 = Avx2.UnpackHigh(m2, m7);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackLow(m4, m0);
            t1 = Avx2.Blend(m1.AsUInt32(), m6.AsUInt32(), 0b_1100_1100).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.AlignRight(m5, m4, 8);
            t1 = Avx2.UnpackHigh(m1, m3);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackLow(m2, m7);
            t1 = Avx2.Blend(m3.AsUInt32(), m0.AsUInt32(), 0b_1100_1100).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);

            if (++i == rounds) break;

            //ROUND 4
            t0 = Avx2.UnpackHigh(m3, m1);
            t1 = Avx2.UnpackHigh(m6, m5);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackHigh(m4, m0);
            t1 = Avx2.UnpackLow(m6, m7);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.AlignRight(m1, m7, 8);
            t1 = Avx2.Shuffle(m2.AsUInt32(), 0b_01_00_11_10).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackLow(m4, m3);
            t1 = Avx2.UnpackLow(m5, m0);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);

            if (++i == rounds) break;

            //ROUND 5
            t0 = Avx2.UnpackHigh(m4, m2);
            t1 = Avx2.UnpackLow(m1, m5);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.Blend(m0.AsUInt32(), m3.AsUInt32(), 0b_1100_1100).AsUInt64();
            t1 = Avx2.Blend(m2.AsUInt32(), m7.AsUInt32(), 0b_1100_1100).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.AlignRight(m7, m1, 8);
            t1 = Avx2.AlignRight(m3, m5, 8);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackHigh(m6, m0);
            t1 = Avx2.UnpackLow(m6, m4);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);

            if (++i == rounds) break;

            //ROUND 6
            t0 = Avx2.UnpackLow(m1, m3);
            t1 = Avx2.UnpackLow(m0, m4);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackLow(m6, m5);
            t1 = Avx2.UnpackHigh(m5, m1);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.AlignRight(m2, m0, 8);
            t1 = Avx2.UnpackHigh(m3, m7);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackHigh(m4, m6);
            t1 = Avx2.AlignRight(m7, m2, 8);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);

            if (++i == rounds) break;

            //ROUND 7
            t0 = Avx2.Blend(m6.AsUInt32(), m0.AsUInt32(), 0b_1100_1100).AsUInt64();
            t1 = Avx2.UnpackLow(m7, m2);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackHigh(m2, m7);
            t1 = Avx2.AlignRight(m5, m6, 8);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.UnpackLow(m4, m0);
            t1 = Avx2.Blend(m3.AsUInt32(), m4.AsUInt32(), 0b_1100_1100).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackHigh(m5, m3);
            t1 = Avx2.Shuffle(m1.AsUInt32(), 0b_01_00_11_10).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);

            if (++i == rounds) break;

            //ROUND 8
            t0 = Avx2.UnpackHigh(m6, m3);
            t1 = Avx2.Blend(m6.AsUInt32(), m1.AsUInt32(), 0b_1100_1100).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.AlignRight(m7, m5, 8);
            t1 = Avx2.UnpackHigh(m0, m4);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.Blend(m1.AsUInt32(), m2.AsUInt32(), 0b_1100_1100).AsUInt64();
            t1 = Avx2.AlignRight(m4, m7, 8);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackLow(m5, m0);
            t1 = Avx2.UnpackLow(m2, m3);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);

            if (++i == rounds) break;

            //ROUND 9
            t0 = Avx2.UnpackLow(m3, m7);
            t1 = Avx2.AlignRight(m0, m5, 8);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackHigh(m7, m4);
            t1 = Avx2.AlignRight(m4, m1, 8);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.UnpackLow(m5, m6);
            t1 = Avx2.UnpackHigh(m6, m0);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.AlignRight(m1, m2, 8);
            t1 = Avx2.AlignRight(m2, m3, 8);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);

            if (++i == rounds) break;

            //ROUND 10
            t0 = Avx2.UnpackLow(m5, m4);
            t1 = Avx2.UnpackHigh(m3, m0);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.UnpackLow(m1, m2);
            t1 = Avx2.Blend(m3.AsUInt32(), m2.AsUInt32(), 0b_1100_1100).AsUInt64();
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //DIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_10_01_00_11);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_00_11_10_01);

            t0 = Avx2.UnpackHigh(m6, m7);
            t1 = Avx2.UnpackHigh(m4, m1);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G1
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Shuffle(row2.AsByte(), r24).AsUInt64();

            t0 = Avx2.Blend(m0.AsUInt32(), m5.AsUInt32(), 0b_1100_1100).AsUInt64();
            t1 = Avx2.UnpackLow(m7, m6);
            b0 = Avx2.Blend(t0.AsUInt32(), t1.AsUInt32(), 0b_1111_0000).AsUInt64();

            //G2
            row1 = Avx2.Add(Avx2.Add(row1, b0), row2);
            row4 = Avx2.Xor(row4, row1);
            row4 = Avx2.Shuffle(row4.AsByte(), r16).AsUInt64();

            row3 = Avx2.Add(row3, row4);
            row2 = Avx2.Xor(row2, row3);
            row2 = Avx2.Xor(Avx2.ShiftRightLogical(row2, 63), Avx2.Add(row2, row2));

            //UNDIAGONALIZE
            row1 = Avx2.Permute4x64(row1, 0b_00_11_10_01);
            row4 = Avx2.Permute4x64(row4, 0b_01_00_11_10);
            row3 = Avx2.Permute4x64(row3, 0b_10_01_00_11);
        }

        row1 = Avx2.Xor(row1, row3);
        row2 = Avx2.Xor(row2, row4);
        row1 = Avx2.Xor(row1, Avx.LoadVector256(sh));
        row2 = Avx2.Xor(row2, Avx.LoadVector256(sh + Vector256<ulong>.Count));

        Avx.Store(sh, row1);
        Avx.Store(sh + Vector256<ulong>.Count, row2);
    }
}
