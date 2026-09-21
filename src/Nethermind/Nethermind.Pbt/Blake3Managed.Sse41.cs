// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Pbt;

/// <summary>
/// Single-block BLAKE3 compression on 128-bit rows, following the reference implementation's
/// <c>blake3_sse41.c</c> (https://github.com/BLAKE3-team/BLAKE3).
/// </summary>
public static partial class Blake3Managed
{
    // _MM_SHUFFLE(z, y, x, w): output lanes 0..3 take source lanes w, x, y, z.
    private const byte Shuffle2020 = (2 << 6) | (0 << 4) | (2 << 2) | 0;
    private const byte Shuffle3131 = (3 << 6) | (1 << 4) | (3 << 2) | 1;
    private const byte Shuffle2103 = (2 << 6) | (1 << 4) | (0 << 2) | 3;
    private const byte Shuffle1032 = (1 << 6) | (0 << 4) | (3 << 2) | 2;
    private const byte Shuffle0321 = (0 << 6) | (3 << 4) | (2 << 2) | 1;
    private const byte Shuffle3112 = (3 << 6) | (1 << 4) | (1 << 2) | 2;
    private const byte Shuffle3322 = (3 << 6) | (3 << 4) | (2 << 2) | 2;
    private const byte Shuffle0033 = (0 << 6) | (0 << 4) | (3 << 2) | 3;
    private const byte Shuffle1320 = (1 << 6) | (3 << 4) | (2 << 2) | 0;
    private const byte Shuffle0132 = (0 << 6) | (1 << 4) | (3 << 2) | 2;

    // 16-bit blend masks selecting 32-bit lanes 1 and 3, and lane 3 only, from the second operand.
    private const byte BlendLanes1And3 = 0xCC;
    private const byte BlendLane3 = 0xC0;

    /// <summary>
    /// <see cref="Compress{TShape}"/> on SSE4.1: the state is four row vectors and the message four word
    /// vectors that are permuted in registers between rounds, so no round touches memory.
    /// </summary>
    /// <remarks>
    /// The diagonal step rotates rows 0, 2 and 3 rather than 1, 2 and 3 because row 1 is the last one a
    /// column step produces, so those shuffles overlap with its tail. Rotations use AVX-512VL's
    /// <c>vprord</c> when present, else byte shuffles for multiples of eight and shift pairs otherwise.
    /// </remarks>
    private static void CompressSse41<TShape>(Span<uint> cv, ref byte lowRef, ref byte highRef, ulong counter, uint blockLength, uint flags)
        where TShape : IBlockShape
    {
        Debug.Assert(Sse41.IsSupported);

        ref uint cvRef = ref MemoryMarshal.GetReference(cv);
        Vector128<uint> row0 = Vector128.LoadUnsafe(ref cvRef);
        Vector128<uint> row1 = Vector128.LoadUnsafe(ref cvRef, 4);
        Vector128<uint> row2 = Vector128.Create(Iv0, Iv1, Iv2, Iv3);
        Vector128<uint> row3 = Vector128.Create((uint)counter, (uint)(counter >> 32), blockLength, flags);

        Vector128<uint> m0 = TShape.LowIsZero ? Vector128<uint>.Zero : Unsafe.ReadUnaligned<Vector128<uint>>(ref lowRef);
        Vector128<uint> m1 = TShape.LowIsZero ? Vector128<uint>.Zero : Unsafe.ReadUnaligned<Vector128<uint>>(ref Unsafe.Add(ref lowRef, 16));
        Vector128<uint> m2 = TShape.HighIsZero ? Vector128<uint>.Zero : Unsafe.ReadUnaligned<Vector128<uint>>(ref highRef);
        Vector128<uint> m3 = TShape.HighIsZero ? Vector128<uint>.Zero : Unsafe.ReadUnaligned<Vector128<uint>>(ref Unsafe.Add(ref highRef, 16));

        FirstMessageGroups(m0, m1, m2, m3, out Vector128<uint> t0, out Vector128<uint> t1, out Vector128<uint> t2, out Vector128<uint> t3);
        Round(ref row0, ref row1, ref row2, ref row3, t0, t1, t2, t3);

        // Every later round applies the same fixed permutation to the previous round's groups.
        for (int round = 1; round < 7; round++)
        {
            NextMessageGroups(t0, t1, t2, t3, out t0, out t1, out t2, out t3);
            Round(ref row0, ref row1, ref row2, ref row3, t0, t1, t2, t3);
        }

        Vector128.StoreUnsafe(row0 ^ row2, ref cvRef);
        Vector128.StoreUnsafe(row1 ^ row3, ref cvRef, 4);
    }

    /// <summary>
    /// <see cref="CompressSse41{TShape}"/> for two independent full blocks at once, both at chunk counter 0.
    /// </summary>
    /// <remarks>
    /// A single compression is one dependency chain of vector operations, whose latency leaves most of the
    /// core's vector units idle. Interleaving the two blocks' steps gives the scheduler two independent
    /// chains, so the pair finishes in well under twice the time of one.
    /// </remarks>
    private static void CompressSse41Two(Span<uint> cvA, ref byte blockA, uint blockLengthA, uint flagsA,
        Span<uint> cvB, ref byte blockB, uint blockLengthB, uint flagsB)
    {
        Debug.Assert(Sse41.IsSupported);

        ref uint cvRefA = ref MemoryMarshal.GetReference(cvA);
        ref uint cvRefB = ref MemoryMarshal.GetReference(cvB);
        Vector128<uint> rowA0 = Vector128.LoadUnsafe(ref cvRefA);
        Vector128<uint> rowA1 = Vector128.LoadUnsafe(ref cvRefA, 4);
        Vector128<uint> rowA2 = Vector128.Create(Iv0, Iv1, Iv2, Iv3);
        Vector128<uint> rowA3 = Vector128.Create(0u, 0u, blockLengthA, flagsA);
        Vector128<uint> rowB0 = Vector128.LoadUnsafe(ref cvRefB);
        Vector128<uint> rowB1 = Vector128.LoadUnsafe(ref cvRefB, 4);
        Vector128<uint> rowB2 = rowA2;
        Vector128<uint> rowB3 = Vector128.Create(0u, 0u, blockLengthB, flagsB);

        FirstMessageGroups(
            Unsafe.ReadUnaligned<Vector128<uint>>(ref blockA),
            Unsafe.ReadUnaligned<Vector128<uint>>(ref Unsafe.Add(ref blockA, 16)),
            Unsafe.ReadUnaligned<Vector128<uint>>(ref Unsafe.Add(ref blockA, 32)),
            Unsafe.ReadUnaligned<Vector128<uint>>(ref Unsafe.Add(ref blockA, 48)),
            out Vector128<uint> tA0, out Vector128<uint> tA1, out Vector128<uint> tA2, out Vector128<uint> tA3);
        FirstMessageGroups(
            Unsafe.ReadUnaligned<Vector128<uint>>(ref blockB),
            Unsafe.ReadUnaligned<Vector128<uint>>(ref Unsafe.Add(ref blockB, 16)),
            Unsafe.ReadUnaligned<Vector128<uint>>(ref Unsafe.Add(ref blockB, 32)),
            Unsafe.ReadUnaligned<Vector128<uint>>(ref Unsafe.Add(ref blockB, 48)),
            out Vector128<uint> tB0, out Vector128<uint> tB1, out Vector128<uint> tB2, out Vector128<uint> tB3);
        RoundTwo(ref rowA0, ref rowA1, ref rowA2, ref rowA3, tA0, tA1, tA2, tA3, ref rowB0, ref rowB1, ref rowB2, ref rowB3, tB0, tB1, tB2, tB3);

        for (int round = 1; round < 7; round++)
        {
            NextMessageGroups(tA0, tA1, tA2, tA3, out tA0, out tA1, out tA2, out tA3);
            NextMessageGroups(tB0, tB1, tB2, tB3, out tB0, out tB1, out tB2, out tB3);
            RoundTwo(ref rowA0, ref rowA1, ref rowA2, ref rowA3, tA0, tA1, tA2, tA3, ref rowB0, ref rowB1, ref rowB2, ref rowB3, tB0, tB1, tB2, tB3);
        }

        Vector128.StoreUnsafe(rowA0 ^ rowA2, ref cvRefA);
        Vector128.StoreUnsafe(rowA1 ^ rowA3, ref cvRefA, 4);
        Vector128.StoreUnsafe(rowB0 ^ rowB2, ref cvRefB);
        Vector128.StoreUnsafe(rowB1 ^ rowB3, ref cvRefB, 4);
    }

    // Round 1 gathers the words into the groups each half-step mixes: the even and odd words for the
    // column steps, then the even and odd words of the second half, rotated to the diagonal row order.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FirstMessageGroups(Vector128<uint> m0, Vector128<uint> m1, Vector128<uint> m2, Vector128<uint> m3,
        out Vector128<uint> t0, out Vector128<uint> t1, out Vector128<uint> t2, out Vector128<uint> t3)
    {
        t0 = Sse.Shuffle(m0.AsSingle(), m1.AsSingle(), Shuffle2020).AsUInt32();
        t1 = Sse.Shuffle(m0.AsSingle(), m1.AsSingle(), Shuffle3131).AsUInt32();
        t2 = Sse2.Shuffle(Sse.Shuffle(m2.AsSingle(), m3.AsSingle(), Shuffle2020).AsUInt32(), Shuffle2103);
        t3 = Sse2.Shuffle(Sse.Shuffle(m2.AsSingle(), m3.AsSingle(), Shuffle3131).AsUInt32(), Shuffle2103);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NextMessageGroups(Vector128<uint> m0, Vector128<uint> m1, Vector128<uint> m2, Vector128<uint> m3,
        out Vector128<uint> t0, out Vector128<uint> t1, out Vector128<uint> t2, out Vector128<uint> t3)
    {
        t0 = Sse2.Shuffle(Sse.Shuffle(m0.AsSingle(), m1.AsSingle(), Shuffle3112).AsUInt32(), Shuffle0321);
        t1 = Sse41.Blend(Sse2.Shuffle(m0, Shuffle0033).AsUInt16(), Sse.Shuffle(m2.AsSingle(), m3.AsSingle(), Shuffle3322).AsUInt16(), BlendLanes1And3).AsUInt32();
        t2 = Sse2.Shuffle(Sse41.Blend(Sse2.UnpackLow(m3.AsUInt64(), m1.AsUInt64()).AsUInt16(), m2.AsUInt16(), BlendLane3).AsUInt32(), Shuffle1320);
        t3 = Sse2.Shuffle(Sse2.UnpackLow(m2, Sse2.UnpackHigh(m1, m3)), Shuffle0132);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RoundTwo(
        ref Vector128<uint> rowA0, ref Vector128<uint> rowA1, ref Vector128<uint> rowA2, ref Vector128<uint> rowA3,
        Vector128<uint> tA0, Vector128<uint> tA1, Vector128<uint> tA2, Vector128<uint> tA3,
        ref Vector128<uint> rowB0, ref Vector128<uint> rowB1, ref Vector128<uint> rowB2, ref Vector128<uint> rowB3,
        Vector128<uint> tB0, Vector128<uint> tB1, Vector128<uint> tB2, Vector128<uint> tB3)
    {
        G1(ref rowA0, ref rowA1, ref rowA2, ref rowA3, tA0);
        G1(ref rowB0, ref rowB1, ref rowB2, ref rowB3, tB0);
        G2(ref rowA0, ref rowA1, ref rowA2, ref rowA3, tA1);
        G2(ref rowB0, ref rowB1, ref rowB2, ref rowB3, tB1);
        rowA0 = Sse2.Shuffle(rowA0, Shuffle2103);
        rowA3 = Sse2.Shuffle(rowA3, Shuffle1032);
        rowA2 = Sse2.Shuffle(rowA2, Shuffle0321);
        rowB0 = Sse2.Shuffle(rowB0, Shuffle2103);
        rowB3 = Sse2.Shuffle(rowB3, Shuffle1032);
        rowB2 = Sse2.Shuffle(rowB2, Shuffle0321);
        G1(ref rowA0, ref rowA1, ref rowA2, ref rowA3, tA2);
        G1(ref rowB0, ref rowB1, ref rowB2, ref rowB3, tB2);
        G2(ref rowA0, ref rowA1, ref rowA2, ref rowA3, tA3);
        G2(ref rowB0, ref rowB1, ref rowB2, ref rowB3, tB3);
        rowA0 = Sse2.Shuffle(rowA0, Shuffle0321);
        rowA3 = Sse2.Shuffle(rowA3, Shuffle1032);
        rowA2 = Sse2.Shuffle(rowA2, Shuffle2103);
        rowB0 = Sse2.Shuffle(rowB0, Shuffle0321);
        rowB3 = Sse2.Shuffle(rowB3, Shuffle1032);
        rowB2 = Sse2.Shuffle(rowB2, Shuffle2103);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Round(ref Vector128<uint> row0, ref Vector128<uint> row1, ref Vector128<uint> row2, ref Vector128<uint> row3,
        Vector128<uint> t0, Vector128<uint> t1, Vector128<uint> t2, Vector128<uint> t3)
    {
        G1(ref row0, ref row1, ref row2, ref row3, t0);
        G2(ref row0, ref row1, ref row2, ref row3, t1);
        row0 = Sse2.Shuffle(row0, Shuffle2103);
        row3 = Sse2.Shuffle(row3, Shuffle1032);
        row2 = Sse2.Shuffle(row2, Shuffle0321);
        G1(ref row0, ref row1, ref row2, ref row3, t2);
        G2(ref row0, ref row1, ref row2, ref row3, t3);
        row0 = Sse2.Shuffle(row0, Shuffle0321);
        row3 = Sse2.Shuffle(row3, Shuffle1032);
        row2 = Sse2.Shuffle(row2, Shuffle2103);
    }

    // (row0 + m) + row1: row1 is the last value the previous half-step produces, so only one add
    // depends on it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G1(ref Vector128<uint> row0, ref Vector128<uint> row1, ref Vector128<uint> row2, ref Vector128<uint> row3, Vector128<uint> m)
    {
        row0 = row0 + m + row1;
        row3 = RotateRight16(row3 ^ row0);
        row2 += row3;
        row1 = RotateRight12(row1 ^ row2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G2(ref Vector128<uint> row0, ref Vector128<uint> row1, ref Vector128<uint> row2, ref Vector128<uint> row3, Vector128<uint> m)
    {
        row0 = row0 + m + row1;
        row3 = RotateRight8(row3 ^ row0);
        row2 += row3;
        row1 = RotateRight7(row1 ^ row2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> RotateRight16(Vector128<uint> value) => Avx512F.VL.IsSupported
        ? Avx512F.VL.RotateRight(value, 16)
        : Ssse3.Shuffle(value.AsByte(), Vector128.Create((byte)2, 3, 0, 1, 6, 7, 4, 5, 10, 11, 8, 9, 14, 15, 12, 13)).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> RotateRight8(Vector128<uint> value) => Avx512F.VL.IsSupported
        ? Avx512F.VL.RotateRight(value, 8)
        : Ssse3.Shuffle(value.AsByte(), Vector128.Create((byte)1, 2, 3, 0, 5, 6, 7, 4, 9, 10, 11, 8, 13, 14, 15, 12)).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> RotateRight12(Vector128<uint> value) => Avx512F.VL.IsSupported
        ? Avx512F.VL.RotateRight(value, 12)
        : Sse2.ShiftRightLogical(value, 12) | Sse2.ShiftLeftLogical(value, 20);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> RotateRight7(Vector128<uint> value) => Avx512F.VL.IsSupported
        ? Avx512F.VL.RotateRight(value, 7)
        : Sse2.ShiftRightLogical(value, 7) | Sse2.ShiftLeftLogical(value, 25);
}
