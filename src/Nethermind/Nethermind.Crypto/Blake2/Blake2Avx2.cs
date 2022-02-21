//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
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
    private static ReadOnlySpan<byte> ivle => new byte[]
    {
        0x08, 0xC9, 0xBC, 0xF3, 0x67, 0xE6, 0x09, 0x6A, 0x3B, 0xA7, 0xCA, 0x84, 0x85, 0xAE, 0x67, 0xBB, 0x2B, 0xF8,
        0x94, 0xFE, 0x72, 0xF3, 0x6E, 0x3C, 0xF1, 0x36, 0x1D, 0x5F, 0x3A, 0xF5, 0x4F, 0xA5, 0xD1, 0x82, 0xE6, 0xAD,
        0x7F, 0x52, 0x0E, 0x51, 0x1F, 0x6C, 0x3E, 0x2B, 0x8C, 0x68, 0x05, 0x9B, 0x6B, 0xBD, 0x41, 0xFB, 0xAB, 0xD9,
        0x83, 0x1F, 0x79, 0x21, 0x7E, 0x13, 0x19, 0xCD, 0xE0, 0x5B
    };

    private static ReadOnlySpan<byte> rormask => new byte[]
    {
        3, 4, 5, 6, 7, 0, 1, 2, 11, 12, 13, 14, 15, 8, 9, 10, //r24
        2, 3, 4, 5, 6, 7, 0, 1, 10, 11, 12, 13, 14, 15, 8, 9 //r16
    };

    // SIMD algorithm described in https://eprint.iacr.org/2012/275.pdf
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ComputeAvx2(ulong* sh, ulong* m, uint rounds)
    {
        // Rotate shuffle masks. We can safely convert the ref to a pointer because the compiler guarantees the
        // data is in a fixed location, and the ref itself is converted from a pointer. Same for the IV below.
        byte* prm = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(rormask));
        var r24 = Avx2.BroadcastVector128ToVector256(prm);
        var r16 = Avx2.BroadcastVector128ToVector256(prm + Vector128<byte>.Count);

        var row1 = Avx.LoadVector256(sh);
        var row2 = Avx.LoadVector256(sh + Vector256<ulong>.Count);

        ulong* piv = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(ivle));
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
        
        uint fullRounds = rounds / 10;
        uint partialRounds = rounds % 10;

        for (int i = 0; i < fullRounds; i++)
        {
            ComputeFullRound();
        }

        for (uint i = 0; i < partialRounds; i++)
        {
            ComputePartialRound(i % 10);
        }

        row1 = Avx2.Xor(row1, row3);
        row2 = Avx2.Xor(row2, row4);
        row1 = Avx2.Xor(row1, Avx.LoadVector256(sh));
        row2 = Avx2.Xor(row2, Avx.LoadVector256(sh + Vector256<ulong>.Count));

        Avx.Store(sh, row1);
        Avx.Store(sh + Vector256<ulong>.Count, row2);
        
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeFullRound()
        {
            ComputeRound1();
            ComputeRound2();
            ComputeRound3();
            ComputeRound4();
            ComputeRound5();
            ComputeRound6();
            ComputeRound7();
            ComputeRound8();
            ComputeRound9();
            ComputeRound10();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputePartialRound(uint round)
        {
            switch (round)
            {
                case 0:
                {
                    ComputeRound1();
                    break;
                }
                case 1:
                {
                    ComputeRound2();
                    break;
                }
                case 2:
                {
                    ComputeRound3();
                    break;
                }
                case 3:
                {
                    ComputeRound4();
                    break;
                }
                case 4:
                {
                    ComputeRound5();
                    break;
                }
                case 5:
                {
                    ComputeRound6();
                    break;
                }
                case 6:
                {
                    ComputeRound7();
                    break;
                }
                case 7:
                {
                    ComputeRound8();
                    break;
                }
                case 8:
                {
                    ComputeRound9();
                    break;
                }
                case 9:
                {
                    ComputeRound10();
                    break;
                }
                default: break;
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound1()
        {
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

            var m4 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 4);
            var m5 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 5);
            var m6 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 6);
            var m7 = Avx2.BroadcastVector128ToVector256(m + Vector128<ulong>.Count * 7);

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
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound2()
        {
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
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound3()
        {
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
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound4()
        {
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
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound5()
        {
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
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound6()
        {
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
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound7()
        {
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
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound8()
        {
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
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound9()
        {
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
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeRound10()
        {
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
    }
}
