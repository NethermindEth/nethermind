// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Pbt;

public static partial class Blake3Managed
{
    private const int OutputLength = 32;
    private const int MaxLanes = 16;

    /// <summary>The message word order of each of the seven rounds.</summary>
    private static ReadOnlySpan<byte> MessageSchedule =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8,
        3, 4, 10, 12, 13, 2, 7, 14, 6, 5, 9, 0, 11, 15, 8, 1,
        10, 7, 12, 9, 14, 3, 13, 15, 4, 0, 11, 2, 5, 8, 1, 6,
        12, 13, 9, 11, 15, 10, 14, 8, 7, 2, 5, 3, 0, 1, 6, 4,
        9, 14, 11, 5, 8, 12, 15, 1, 13, 3, 0, 10, 2, 6, 4, 7,
        11, 15, 5, 0, 1, 9, 8, 6, 14, 10, 2, 12, 3, 4, 7, 13,
    ];

    /// <summary>
    /// Writes the digests of <paramref name="inputs"/>, back-to-back inputs of <paramref name="inputLength"/> bytes each,
    /// into <paramref name="outputs"/>, one 32-byte digest per input in the same order.
    /// </summary>
    /// <remarks>
    /// Single-chunk inputs are hashed 16, 8 or 4 at a time with one input per vector lane (the reference
    /// implementation's <c>hash_many</c> layout), so the lanes share every instruction instead of each input
    /// waiting on its own dependency chain; what is left over goes through <see cref="HashTwo"/> and <see cref="Hash"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="inputs"/> does not hold exactly one input per digest in <paramref name="outputs"/>.</exception>
    public static void HashMany(ReadOnlySpan<byte> inputs, int inputLength, Span<byte> outputs)
    {
        int count = outputs.Length / OutputLength;
        if (outputs.Length % OutputLength != 0 || inputLength < 0 || inputs.Length != count * inputLength)
            throw new ArgumentException("Inputs and outputs must hold the same number of hashes.");

        int index = 0;
        if (inputLength <= ChunkLength)
        {
            ref byte inputsRef = ref MemoryMarshal.GetReference(inputs);
            ref byte outputsRef = ref MemoryMarshal.GetReference(outputs);
            if (Avx512F.IsSupported && Vector512.IsHardwareAccelerated)
            {
                for (; count - index >= 16; index += 16)
                    HashLanes<Vector512<uint>, Lanes16>(ref Unsafe.Add(ref inputsRef, index * inputLength), inputLength, ref Unsafe.Add(ref outputsRef, index * OutputLength));
            }
            if (Vector256.IsHardwareAccelerated)
            {
                for (; count - index >= 8; index += 8)
                    HashLanes<Vector256<uint>, Lanes8>(ref Unsafe.Add(ref inputsRef, index * inputLength), inputLength, ref Unsafe.Add(ref outputsRef, index * OutputLength));
            }
            if (Vector128.IsHardwareAccelerated)
            {
                for (; count - index >= 4; index += 4)
                    HashLanes<Vector128<uint>, Lanes4>(ref Unsafe.Add(ref inputsRef, index * inputLength), inputLength, ref Unsafe.Add(ref outputsRef, index * OutputLength));
            }
            for (; count - index >= 2; index += 2)
            {
                HashTwo(inputs.Slice(index * inputLength, inputLength), outputs.Slice(index * OutputLength, OutputLength),
                    inputs.Slice((index + 1) * inputLength, inputLength), outputs.Slice((index + 1) * OutputLength, OutputLength));
            }
        }
        for (; index < count; index++)
            Hash(inputs.Slice(index * inputLength, inputLength), outputs.Slice(index * OutputLength, OutputLength));
    }

    /// <summary>Hashes <c>TLanes.Count</c> back-to-back single-chunk inputs of <paramref name="inputLength"/> bytes, one per vector lane.</summary>
    /// <remarks>Kept out of line with the state in locals so the lanes stay register-resident across the whole chunk.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HashLanes<TVector, TLanes>(ref byte inputs, int inputLength, ref byte outputs)
        where TVector : struct
        where TLanes : ILanes<TVector>
    {
        int lanes = TLanes.Count;
        // Word w of lane l's block lives at message[w * lanes + l], so each round operand is one vector load.
        Span<uint> message = stackalloc uint[16 * MaxLanes];
        Span<byte> padded = stackalloc byte[MaxLanes * BlockLength];
        ref uint messageRef = ref MemoryMarshal.GetReference(message);
        ref byte scheduleRef = ref MemoryMarshal.GetReference(MessageSchedule);

        TVector cv0 = TLanes.Broadcast(Iv0), cv1 = TLanes.Broadcast(Iv1), cv2 = TLanes.Broadcast(Iv2), cv3 = TLanes.Broadcast(Iv3);
        TVector cv4 = TLanes.Broadcast(Iv4), cv5 = TLanes.Broadcast(Iv5), cv6 = TLanes.Broadcast(Iv6), cv7 = TLanes.Broadcast(Iv7);
        uint flags = ChunkStart;
        int offset = 0;
        while (true)
        {
            int remaining = inputLength - offset;
            bool isLast = remaining <= BlockLength;
            if (isLast)
            {
                flags |= ChunkEnd | Root;
                padded.Clear();
                for (int lane = 0; lane < lanes; lane++)
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref inputs, lane * inputLength + offset), remaining).CopyTo(padded[(lane * BlockLength)..]);
                Transpose(ref MemoryMarshal.GetReference(padded), BlockLength, lanes, message);
            }
            else
            {
                Transpose(ref Unsafe.Add(ref inputs, offset), inputLength, lanes, message);
            }

            TVector v0 = cv0, v1 = cv1, v2 = cv2, v3 = cv3, v4 = cv4, v5 = cv5, v6 = cv6, v7 = cv7;
            TVector v8 = TLanes.Broadcast(Iv0), v9 = TLanes.Broadcast(Iv1), v10 = TLanes.Broadcast(Iv2), v11 = TLanes.Broadcast(Iv3);
            TVector v12 = TLanes.Broadcast(0), v13 = TLanes.Broadcast(0);
            TVector v14 = TLanes.Broadcast(isLast ? (uint)remaining : BlockLength), v15 = TLanes.Broadcast(flags);
            for (int round = 0; round < 7; round++)
            {
                ref byte order = ref Unsafe.Add(ref scheduleRef, round * 16);
                G<TVector, TLanes>(ref v0, ref v4, ref v8, ref v12, Word<TVector, TLanes>(ref messageRef, ref order, 0), Word<TVector, TLanes>(ref messageRef, ref order, 1));
                G<TVector, TLanes>(ref v1, ref v5, ref v9, ref v13, Word<TVector, TLanes>(ref messageRef, ref order, 2), Word<TVector, TLanes>(ref messageRef, ref order, 3));
                G<TVector, TLanes>(ref v2, ref v6, ref v10, ref v14, Word<TVector, TLanes>(ref messageRef, ref order, 4), Word<TVector, TLanes>(ref messageRef, ref order, 5));
                G<TVector, TLanes>(ref v3, ref v7, ref v11, ref v15, Word<TVector, TLanes>(ref messageRef, ref order, 6), Word<TVector, TLanes>(ref messageRef, ref order, 7));
                G<TVector, TLanes>(ref v0, ref v5, ref v10, ref v15, Word<TVector, TLanes>(ref messageRef, ref order, 8), Word<TVector, TLanes>(ref messageRef, ref order, 9));
                G<TVector, TLanes>(ref v1, ref v6, ref v11, ref v12, Word<TVector, TLanes>(ref messageRef, ref order, 10), Word<TVector, TLanes>(ref messageRef, ref order, 11));
                G<TVector, TLanes>(ref v2, ref v7, ref v8, ref v13, Word<TVector, TLanes>(ref messageRef, ref order, 12), Word<TVector, TLanes>(ref messageRef, ref order, 13));
                G<TVector, TLanes>(ref v3, ref v4, ref v9, ref v14, Word<TVector, TLanes>(ref messageRef, ref order, 14), Word<TVector, TLanes>(ref messageRef, ref order, 15));
            }
            cv0 = TLanes.Xor(v0, v8); cv1 = TLanes.Xor(v1, v9); cv2 = TLanes.Xor(v2, v10); cv3 = TLanes.Xor(v3, v11);
            cv4 = TLanes.Xor(v4, v12); cv5 = TLanes.Xor(v5, v13); cv6 = TLanes.Xor(v6, v14); cv7 = TLanes.Xor(v7, v15);

            if (isLast) break;
            offset += BlockLength;
            flags = 0;
        }

        // The digest words go out through the message buffer, word-major, and are gathered back per lane.
        TLanes.Store(cv0, ref messageRef);
        TLanes.Store(cv1, ref Unsafe.Add(ref messageRef, lanes));
        TLanes.Store(cv2, ref Unsafe.Add(ref messageRef, 2 * lanes));
        TLanes.Store(cv3, ref Unsafe.Add(ref messageRef, 3 * lanes));
        TLanes.Store(cv4, ref Unsafe.Add(ref messageRef, 4 * lanes));
        TLanes.Store(cv5, ref Unsafe.Add(ref messageRef, 5 * lanes));
        TLanes.Store(cv6, ref Unsafe.Add(ref messageRef, 6 * lanes));
        TLanes.Store(cv7, ref Unsafe.Add(ref messageRef, 7 * lanes));
        for (int lane = 0; lane < lanes; lane++)
        {
            ref byte output = ref Unsafe.Add(ref outputs, lane * OutputLength);
            for (int word = 0; word < 8; word++)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, word * 4), Unsafe.Add(ref messageRef, word * lanes + lane));
        }
    }

    /// <summary>Loads the 64-byte block of each of <paramref name="lanes"/> lanes, <paramref name="stride"/> bytes apart, word-major into <paramref name="message"/>.</summary>
    private static void Transpose(ref byte blocks, int stride, int lanes, Span<uint> message)
    {
        ref uint messageRef = ref MemoryMarshal.GetReference(message);
        for (int lane = 0; lane < lanes; lane++)
        {
            ref byte block = ref Unsafe.Add(ref blocks, lane * stride);
            for (int word = 0; word < 16; word++)
                Unsafe.Add(ref messageRef, word * lanes + lane) = ReadWord(ref block, word * 4);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TVector Word<TVector, TLanes>(ref uint message, ref byte order, int index)
        where TVector : struct
        where TLanes : ILanes<TVector>
        => TLanes.Load(ref Unsafe.Add(ref message, Unsafe.Add(ref order, index) * TLanes.Count));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G<TVector, TLanes>(ref TVector a, ref TVector b, ref TVector c, ref TVector d, TVector mx, TVector my)
        where TVector : struct
        where TLanes : ILanes<TVector>
    {
        a = TLanes.Add(TLanes.Add(a, b), mx); d = TLanes.RotateRight16(TLanes.Xor(d, a)); c = TLanes.Add(c, d); b = TLanes.RotateRight12(TLanes.Xor(b, c));
        a = TLanes.Add(TLanes.Add(a, b), my); d = TLanes.RotateRight8(TLanes.Xor(d, a)); c = TLanes.Add(c, d); b = TLanes.RotateRight7(TLanes.Xor(b, c));
    }

    /// <summary>The vector operations of <see cref="HashLanes{TVector, TLanes}"/>, one BLAKE3 state word per 32-bit lane.</summary>
    /// <remarks>Implemented by empty structs used as type arguments, so each vector width gets its own JIT instantiation.</remarks>
    private interface ILanes<TVector> where TVector : struct
    {
        static abstract int Count { get; }
        static abstract TVector Broadcast(uint value);
        static abstract TVector Load(ref uint source);
        static abstract void Store(TVector value, ref uint destination);
        static abstract TVector Add(TVector left, TVector right);
        static abstract TVector Xor(TVector left, TVector right);
        static abstract TVector RotateRight16(TVector value);
        static abstract TVector RotateRight12(TVector value);
        static abstract TVector RotateRight8(TVector value);
        static abstract TVector RotateRight7(TVector value);
    }

    private readonly struct Lanes4 : ILanes<Vector128<uint>>
    {
        public static int Count => 4;
        public static Vector128<uint> Broadcast(uint value) => Vector128.Create(value);
        public static Vector128<uint> Load(ref uint source) => Vector128.LoadUnsafe(ref source);
        public static void Store(Vector128<uint> value, ref uint destination) => value.StoreUnsafe(ref destination);
        public static Vector128<uint> Add(Vector128<uint> left, Vector128<uint> right) => left + right;
        public static Vector128<uint> Xor(Vector128<uint> left, Vector128<uint> right) => left ^ right;
        public static Vector128<uint> RotateRight16(Vector128<uint> value) => Avx512F.VL.IsSupported
            ? Avx512F.VL.RotateRight(value, 16)
            : Vector128.ShiftRightLogical(value, 16) | Vector128.ShiftLeft(value, 16);
        public static Vector128<uint> RotateRight12(Vector128<uint> value) => Avx512F.VL.IsSupported
            ? Avx512F.VL.RotateRight(value, 12)
            : Vector128.ShiftRightLogical(value, 12) | Vector128.ShiftLeft(value, 20);
        public static Vector128<uint> RotateRight8(Vector128<uint> value) => Avx512F.VL.IsSupported
            ? Avx512F.VL.RotateRight(value, 8)
            : Vector128.ShiftRightLogical(value, 8) | Vector128.ShiftLeft(value, 24);
        public static Vector128<uint> RotateRight7(Vector128<uint> value) => Avx512F.VL.IsSupported
            ? Avx512F.VL.RotateRight(value, 7)
            : Vector128.ShiftRightLogical(value, 7) | Vector128.ShiftLeft(value, 25);
    }

    private readonly struct Lanes8 : ILanes<Vector256<uint>>
    {
        public static int Count => 8;
        public static Vector256<uint> Broadcast(uint value) => Vector256.Create(value);
        public static Vector256<uint> Load(ref uint source) => Vector256.LoadUnsafe(ref source);
        public static void Store(Vector256<uint> value, ref uint destination) => value.StoreUnsafe(ref destination);
        public static Vector256<uint> Add(Vector256<uint> left, Vector256<uint> right) => left + right;
        public static Vector256<uint> Xor(Vector256<uint> left, Vector256<uint> right) => left ^ right;
        public static Vector256<uint> RotateRight16(Vector256<uint> value) => Avx512F.VL.IsSupported
            ? Avx512F.VL.RotateRight(value, 16)
            : Vector256.ShiftRightLogical(value, 16) | Vector256.ShiftLeft(value, 16);
        public static Vector256<uint> RotateRight12(Vector256<uint> value) => Avx512F.VL.IsSupported
            ? Avx512F.VL.RotateRight(value, 12)
            : Vector256.ShiftRightLogical(value, 12) | Vector256.ShiftLeft(value, 20);
        public static Vector256<uint> RotateRight8(Vector256<uint> value) => Avx512F.VL.IsSupported
            ? Avx512F.VL.RotateRight(value, 8)
            : Vector256.ShiftRightLogical(value, 8) | Vector256.ShiftLeft(value, 24);
        public static Vector256<uint> RotateRight7(Vector256<uint> value) => Avx512F.VL.IsSupported
            ? Avx512F.VL.RotateRight(value, 7)
            : Vector256.ShiftRightLogical(value, 7) | Vector256.ShiftLeft(value, 25);
    }

    private readonly struct Lanes16 : ILanes<Vector512<uint>>
    {
        public static int Count => 16;
        public static Vector512<uint> Broadcast(uint value) => Vector512.Create(value);
        public static Vector512<uint> Load(ref uint source) => Vector512.LoadUnsafe(ref source);
        public static void Store(Vector512<uint> value, ref uint destination) => value.StoreUnsafe(ref destination);
        public static Vector512<uint> Add(Vector512<uint> left, Vector512<uint> right) => left + right;
        public static Vector512<uint> Xor(Vector512<uint> left, Vector512<uint> right) => left ^ right;
        public static Vector512<uint> RotateRight16(Vector512<uint> value) => Avx512F.RotateRight(value, 16);
        public static Vector512<uint> RotateRight12(Vector512<uint> value) => Avx512F.RotateRight(value, 12);
        public static Vector512<uint> RotateRight8(Vector512<uint> value) => Avx512F.RotateRight(value, 8);
        public static Vector512<uint> RotateRight7(Vector512<uint> value) => Avx512F.RotateRight(value, 7);
    }
}
