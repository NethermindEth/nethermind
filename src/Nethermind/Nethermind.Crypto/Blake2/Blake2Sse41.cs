// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
    private static void ComputeSse41(ulong* sh, ulong* m, uint rounds)
    {
        ref byte rrm = ref MemoryMarshal.GetReference(Rormask);
        var r24 = Unsafe.As<byte, Vector128<byte>>(ref rrm);
        var r16 = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref rrm, Vector128<byte>.Count));

        var row1l = Sse2.LoadVector128(sh);
        var row1h = Sse2.LoadVector128(sh + 2);
        var row2l = Sse2.LoadVector128(sh + 4);
        var row2h = Sse2.LoadVector128(sh + 6);

        ref byte riv = ref MemoryMarshal.GetReference(Ivle);
        var row3l = Unsafe.As<byte, Vector128<ulong>>(ref riv);
        var row3h = Unsafe.As<byte, Vector128<ulong>>(ref Unsafe.Add(ref riv, 16));
        var row4l = Unsafe.As<byte, Vector128<ulong>>(ref Unsafe.Add(ref riv, 32));
        var row4h = Unsafe.As<byte, Vector128<ulong>>(ref Unsafe.Add(ref riv, 48));

        row4l = Sse2.Xor(row4l, Sse2.LoadVector128(sh + 8)); // t[]
        row4h = Sse2.Xor(row4h, Sse2.LoadVector128(sh + 10)); // f[]

        var m0 = Sse2.LoadVector128(m);
        var m1 = Sse2.LoadVector128(m + 2);
        var m2 = Sse2.LoadVector128(m + 4);
        var m3 = Sse2.LoadVector128(m + 6);
        var m4 = Sse2.LoadVector128(m + 8);
        var m5 = Sse2.LoadVector128(m + 10);
        var m6 = Sse2.LoadVector128(m + 12);
        var m7 = Sse2.LoadVector128(m + 14);
        Vector128<ulong> t0;
        Vector128<ulong> t1;
        Vector128<ulong> b0;
        Vector128<ulong> b1;

        for (uint i = 0; i < rounds; i++)
        {
            //ROUND 1
            b0 = Sse2.UnpackLow(m0, m1);
            b1 = Sse2.UnpackLow(m2, m3);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackHigh(m0, m1);
            b1 = Sse2.UnpackHigh(m2, m3);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = Sse2.UnpackLow(m4, m5);
            b1 = Sse2.UnpackLow(m6, m7);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackHigh(m4, m5);
            b1 = Sse2.UnpackHigh(m6, m7);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;

            if (++i == rounds) break;

            //ROUND 2
            b0 = Sse2.UnpackLow(m7, m2);
            b1 = Sse2.UnpackHigh(m4, m6);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackLow(m5, m4);
            b1 = Ssse3.AlignRight(m3, m7, 8);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = Sse2.Shuffle(m0.AsUInt32(), 0b_01_00_11_10).AsUInt64();
            b1 = Sse2.UnpackHigh(m5, m2);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackLow(m6, m1);
            b1 = Sse2.UnpackHigh(m3, m1);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;

            if (++i == rounds) break;

            //ROUND 3
            b0 = Ssse3.AlignRight(m6, m5, 8);
            b1 = Sse2.UnpackHigh(m2, m7);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackLow(m4, m0);
            b1 = Sse41.Blend(m1.AsUInt16(), m6.AsUInt16(), 0b_1111_0000).AsUInt64();

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = Sse41.Blend(m5.AsUInt16(), m1.AsUInt16(), 0b_1111_0000).AsUInt64();
            b1 = Sse2.UnpackHigh(m3, m4);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackLow(m7, m3);
            b1 = Ssse3.AlignRight(m2, m0, 8);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;

            if (++i == rounds) break;

            //ROUND 4
            b0 = Sse2.UnpackHigh(m3, m1);
            b1 = Sse2.UnpackHigh(m6, m5);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackHigh(m4, m0);
            b1 = Sse2.UnpackLow(m6, m7);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = Sse41.Blend(m1.AsUInt16(), m2.AsUInt16(), 0b_1111_0000).AsUInt64();
            b1 = Sse41.Blend(m2.AsUInt16(), m7.AsUInt16(), 0b_1111_0000).AsUInt64();

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackLow(m3, m5);
            b1 = Sse2.UnpackLow(m0, m4);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;

            if (++i == rounds) break;

            //ROUND 5
            b0 = Sse2.UnpackHigh(m4, m2);
            b1 = Sse2.UnpackLow(m1, m5);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse41.Blend(m0.AsUInt16(), m3.AsUInt16(), 0b_1111_0000).AsUInt64();
            b1 = Sse41.Blend(m2.AsUInt16(), m7.AsUInt16(), 0b_1111_0000).AsUInt64();

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = Sse41.Blend(m7.AsUInt16(), m5.AsUInt16(), 0b_1111_0000).AsUInt64();
            b1 = Sse41.Blend(m3.AsUInt16(), m1.AsUInt16(), 0b_1111_0000).AsUInt64();

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Ssse3.AlignRight(m6, m0, 8);
            b1 = Sse41.Blend(m4.AsUInt16(), m6.AsUInt16(), 0b_1111_0000).AsUInt64();

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;

            if (++i == rounds) break;

            //ROUND 6
            b0 = Sse2.UnpackLow(m1, m3);
            b1 = Sse2.UnpackLow(m0, m4);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackLow(m6, m5);
            b1 = Sse2.UnpackHigh(m5, m1);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = Sse41.Blend(m2.AsUInt16(), m3.AsUInt16(), 0b_1111_0000).AsUInt64();
            b1 = Sse2.UnpackHigh(m7, m0);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackHigh(m6, m2);
            b1 = Sse41.Blend(m7.AsUInt16(), m4.AsUInt16(), 0b_1111_0000).AsUInt64();

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;

            if (++i == rounds) break;

            //ROUND 7
            b0 = Sse41.Blend(m6.AsUInt16(), m0.AsUInt16(), 0b_1111_0000).AsUInt64();
            b1 = Sse2.UnpackLow(m7, m2);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackHigh(m2, m7);
            b1 = Ssse3.AlignRight(m5, m6, 8);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = Sse2.UnpackLow(m0, m3);
            b1 = Sse2.Shuffle(m4.AsUInt32(), 0b_01_00_11_10).AsUInt64();

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackHigh(m3, m1);
            b1 = Sse41.Blend(m1.AsUInt16(), m5.AsUInt16(), 0b_1111_0000).AsUInt64();

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;

            if (++i == rounds) break;

            //ROUND 8
            b0 = Sse2.UnpackHigh(m6, m3);
            b1 = Sse41.Blend(m6.AsUInt16(), m1.AsUInt16(), 0b_1111_0000).AsUInt64();

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Ssse3.AlignRight(m7, m5, 8);
            b1 = Sse2.UnpackHigh(m0, m4);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = Sse2.UnpackHigh(m2, m7);
            b1 = Sse2.UnpackLow(m4, m1);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackLow(m0, m2);
            b1 = Sse2.UnpackLow(m3, m5);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;

            if (++i == rounds) break;

            //ROUND 9
            b0 = Sse2.UnpackLow(m3, m7);
            b1 = Ssse3.AlignRight(m0, m5, 8);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackHigh(m7, m4);
            b1 = Ssse3.AlignRight(m4, m1, 8);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = m6;
            b1 = Ssse3.AlignRight(m5, m0, 8);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse41.Blend(m1.AsUInt16(), m3.AsUInt16(), 0b_1111_0000).AsUInt64();
            b1 = m2;

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;

            if (++i == rounds) break;

            //ROUND 10
            b0 = Sse2.UnpackLow(m5, m4);
            b1 = Sse2.UnpackHigh(m3, m0);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Sse2.UnpackLow(m1, m2);
            b1 = Sse41.Blend(m3.AsUInt16(), m2.AsUInt16(), 0b_1111_0000).AsUInt64();

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //DIAGONALIZE
            t0 = Ssse3.AlignRight(row2h, row2l, 8);
            t1 = Ssse3.AlignRight(row2l, row2h, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4h, row4l, 8);
            t1 = Ssse3.AlignRight(row4l, row4h, 8);
            row4l = t1;
            row4h = t0;

            b0 = Sse2.UnpackHigh(m7, m4);
            b1 = Sse2.UnpackHigh(m1, m6);

            //G1
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Sse2.Shuffle(row4l.AsUInt32(), 0b_10_11_00_01).AsUInt64();
            row4h = Sse2.Shuffle(row4h.AsUInt32(), 0b_10_11_00_01).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Ssse3.Shuffle(row2l.AsByte(), r24).AsUInt64();
            row2h = Ssse3.Shuffle(row2h.AsByte(), r24).AsUInt64();

            b0 = Ssse3.AlignRight(m7, m5, 8);
            b1 = Sse2.UnpackLow(m6, m0);

            //G2
            row1l = Sse2.Add(Sse2.Add(row1l, b0), row2l);
            row1h = Sse2.Add(Sse2.Add(row1h, b1), row2h);

            row4l = Sse2.Xor(row4l, row1l);
            row4h = Sse2.Xor(row4h, row1h);

            row4l = Ssse3.Shuffle(row4l.AsByte(), r16).AsUInt64();
            row4h = Ssse3.Shuffle(row4h.AsByte(), r16).AsUInt64();

            row3l = Sse2.Add(row3l, row4l);
            row3h = Sse2.Add(row3h, row4h);

            row2l = Sse2.Xor(row2l, row3l);
            row2h = Sse2.Xor(row2h, row3h);

            row2l = Sse2.Xor(Sse2.ShiftRightLogical(row2l, 63), Sse2.Add(row2l, row2l));
            row2h = Sse2.Xor(Sse2.ShiftRightLogical(row2h, 63), Sse2.Add(row2h, row2h));

            //UNDIAGONALIZE
            t0 = Ssse3.AlignRight(row2l, row2h, 8);
            t1 = Ssse3.AlignRight(row2h, row2l, 8);
            row2l = t0;
            row2h = t1;

            b0 = row3l;
            row3l = row3h;
            row3h = b0;

            t0 = Ssse3.AlignRight(row4l, row4h, 8);
            t1 = Ssse3.AlignRight(row4h, row4l, 8);
            row4l = t1;
            row4h = t0;
        }

        row1l = Sse2.Xor(row1l, row3l);
        row1h = Sse2.Xor(row1h, row3h);
        row1l = Sse2.Xor(row1l, Sse2.LoadVector128(sh));
        row1h = Sse2.Xor(row1h, Sse2.LoadVector128(sh + 2));
        Sse2.Store(sh, row1l);
        Sse2.Store(sh + 2, row1h);

        row2l = Sse2.Xor(row2l, row4l);
        row2h = Sse2.Xor(row2h, row4h);
        row2l = Sse2.Xor(row2l, Sse2.LoadVector128(sh + 4));
        row2h = Sse2.Xor(row2h, Sse2.LoadVector128(sh + 6));
        Sse2.Store(sh + 4, row2l);
        Sse2.Store(sh + 6, row2h);
    }
}
