// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Pbt;

/// <summary>Managed, unkeyed BLAKE3 producing a 32-byte digest.</summary>
/// <remarks>
/// Replaces the native <c>Blake3</c> binding, whose per-call P/Invoke overhead dominates at the input sizes
/// EIP-8297 hashes (all 64 bytes or less). Such an input is a single block of a single chunk that is also the
/// root, so <see cref="Hash"/> reduces to one compression with no chunk state and no allocation. Longer inputs
/// take the general path: 1 KiB chunks combined through a chaining-value stack, per the BLAKE3 spec. That path
/// is single-threaded and compresses one block at a time, so it stays well behind the native library's
/// multi-way SIMD for large inputs; it exists for correctness, not for speed.
/// </remarks>
public static class Blake3Managed
{
    private const int BlockLength = 64;
    private const int ChunkLength = 1024;

    private const uint ChunkStart = 1;
    private const uint ChunkEnd = 2;
    private const uint Parent = 4;
    private const uint Root = 8;

    private const uint Iv0 = 0x6A09E667, Iv1 = 0xBB67AE85, Iv2 = 0x3C6EF372, Iv3 = 0xA54FF53A;
    private const uint Iv4 = 0x510E527F, Iv5 = 0x9B05688C, Iv6 = 0x1F83D9AB, Iv7 = 0x5BE0CD19;

    /// <summary>Writes the 32-byte BLAKE3 digest of <paramref name="input"/> into <paramref name="output32"/>.</summary>
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output32)
    {
        Span<uint> cv = stackalloc uint[8];
        if (input.Length > BlockLength)
        {
            HashLong(input, cv);
        }
        else
        {
            Span<byte> block = stackalloc byte[BlockLength];
            PadBlock(input, block);
            InitialCv(cv);
            CompressBlock<FullBlock>(cv, block, 0, (uint)input.Length, ChunkStart | ChunkEnd | Root);
        }

        WriteWords(cv, output32);
    }

    private static void HashLong(ReadOnlySpan<byte> input, Span<uint> cv)
    {
        // Chaining values of the complete subtrees to the left of the current chunk, each covering a
        // power-of-two run of chunks. A chunk count with n trailing zero bits completes n merges, and the
        // counter is 64-bit, so 54 entries cover every input a caller can hold.
        Span<uint> stack = stackalloc uint[54 * 8];
        int stackLength = 0;
        ulong chunkCounter = 0;

        // every chunk but the last is complete, so the last is left over for the root handling below
        while (input.Length > ChunkLength)
        {
            ChunkChainingValue(input[..ChunkLength], chunkCounter, 0, cv);
            input = input[ChunkLength..];
            chunkCounter++;

            for (int merges = BitOperations.TrailingZeroCount(chunkCounter); merges > 0; merges--)
                MergeParent(stack.Slice(--stackLength * 8, 8), cv, 0);

            cv.CopyTo(stack.Slice(stackLength * 8, 8));
            stackLength++;
        }

        // Only the topmost node is compressed with ROOT set. With a single chunk that node is the chunk's
        // last block, otherwise it is the last parent merged.
        ChunkChainingValue(input, chunkCounter, stackLength == 0 ? Root : 0, cv);
        while (stackLength > 0)
            MergeParent(stack.Slice(--stackLength * 8, 8), cv, stackLength == 0 ? Root : 0);
    }

    /// <summary>Compresses one whole chunk (at most <see cref="ChunkLength"/> bytes) into its chaining value.</summary>
    private static void ChunkChainingValue(ReadOnlySpan<byte> chunk, ulong counter, uint rootFlag, Span<uint> cv)
    {
        InitialCv(cv);
        uint flags = ChunkStart;
        while (chunk.Length > BlockLength)
        {
            CompressBlock<FullBlock>(cv, chunk[..BlockLength], counter, BlockLength, flags);
            chunk = chunk[BlockLength..];
            flags = 0;
        }

        Span<byte> last = stackalloc byte[BlockLength];
        PadBlock(chunk, last);
        CompressBlock<FullBlock>(cv, last, counter, (uint)chunk.Length, flags | ChunkEnd | rootFlag);
    }

    /// <summary>Replaces <paramref name="cv"/> with the chaining value of the parent whose children are <paramref name="left"/> and <paramref name="cv"/>.</summary>
    private static void MergeParent(ReadOnlySpan<uint> left, Span<uint> cv, uint rootFlag)
    {
        Span<byte> block = stackalloc byte[BlockLength];
        WriteWords(left, block);
        WriteWords(cv, block[32..]);
        InitialCv(cv);
        CompressBlock<FullBlock>(cv, block, 0, BlockLength, Parent | rootFlag);
    }

    private static void PadBlock(ReadOnlySpan<byte> source, Span<byte> block)
    {
        block.Clear();
        source.CopyTo(block);
    }

    private static void InitialCv(Span<uint> cv)
    {
        cv[0] = Iv0; cv[1] = Iv1; cv[2] = Iv2; cv[3] = Iv3;
        cv[4] = Iv4; cv[5] = Iv5; cv[6] = Iv6; cv[7] = Iv7;
    }

    private static void WriteWords(ReadOnlySpan<uint> words, Span<byte> destination)
    {
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * 4)..], words[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadWord(ref byte block, int offset)
    {
        uint word = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, offset));
        return BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word);
    }

    /// <summary>
    /// Writes the 32-byte BLAKE3 digest of <paramref name="low32"/> concatenated with
    /// <paramref name="high32"/> into <paramref name="output32"/>.
    /// </summary>
    /// <remarks>
    /// Specialized for the EIP-8297 node hash, where one half is very often all zeroes (an empty subtree).
    /// An all-zero half is detected once and then baked into the compression as a type argument, so its
    /// eight message words fold to constants and the round message additions using them disappear. It also
    /// avoids assembling the two halves into a contiguous block, since the words are read from each half
    /// directly.
    /// </remarks>
    public static void HashPair(ReadOnlySpan<byte> low32, ReadOnlySpan<byte> high32, Span<byte> output32)
    {
        Span<uint> cv = stackalloc uint[8];
        InitialCv(cv);

        if (IsZero(low32)) Compress<LowZeroBlock>(cv, ref Reference(high32), ref Reference(high32), 0, BlockLength, ChunkStart | ChunkEnd | Root);
        else if (IsZero(high32)) Compress<HighZeroBlock>(cv, ref Reference(low32), ref Reference(low32), 0, BlockLength, ChunkStart | ChunkEnd | Root);
        else Compress<FullBlock>(cv, ref Reference(low32), ref Reference(high32), 0, BlockLength, ChunkStart | ChunkEnd | Root);

        WriteWords(cv, output32);
    }

    private static bool IsZero(ReadOnlySpan<byte> value) => value.IndexOfAnyExcept((byte)0) < 0;

    private static ref byte Reference(ReadOnlySpan<byte> value) => ref MemoryMarshal.GetReference(value);

    /// <summary>Which halves of a compression's message block are known to be all zeroes.</summary>
    /// <remarks>
    /// Implemented by empty structs used as type arguments, so each shape gets its own JIT instantiation in
    /// which the properties below are constants and the zeroed message words fold away.
    /// </remarks>
    private interface IBlockShape
    {
        static abstract bool LowIsZero { get; }
        static abstract bool HighIsZero { get; }
    }

    private readonly struct FullBlock : IBlockShape
    {
        public static bool LowIsZero => false;
        public static bool HighIsZero => false;
    }

    private readonly struct LowZeroBlock : IBlockShape
    {
        public static bool LowIsZero => true;
        public static bool HighIsZero => false;
    }

    private readonly struct HighZeroBlock : IBlockShape
    {
        public static bool LowIsZero => false;
        public static bool HighIsZero => true;
    }

    /// <summary>Compresses a contiguous <see cref="BlockLength"/>-byte block; see <see cref="Compress{TShape}"/>.</summary>
    private static void CompressBlock<TShape>(Span<uint> cv, ReadOnlySpan<byte> block, ulong counter, uint blockLength, uint flags)
        where TShape : IBlockShape
    {
        ref byte blockRef = ref MemoryMarshal.GetReference(block);
        Compress<TShape>(cv, ref blockRef, ref Unsafe.Add(ref blockRef, 32), counter, blockLength, flags);
    }

    /// <summary>
    /// Compresses the 64-byte block formed by <paramref name="lowRef"/>'s 32 bytes followed by
    /// <paramref name="highRef"/>'s into <paramref name="cv"/>, replacing it with the result's chaining
    /// value — which for a ROOT compression is the digest itself.
    /// </summary>
    /// <remarks>
    /// The whole compression is held in locals so that it stays in registers: the state is a fixed 16 words
    /// and the round message order is a compile-time permutation, so the rounds unroll with no indexing and
    /// no bounds checks. A half that <typeparamref name="TShape"/> declares zero is never read, so its
    /// reference is not required to be valid. A short final block is zero-padded by the caller and its true
    /// length passed as <paramref name="blockLength"/>.
    /// </remarks>
    private static void Compress<TShape>(Span<uint> cv, ref byte lowRef, ref byte highRef, ulong counter, uint blockLength, uint flags)
        where TShape : IBlockShape
    {
        uint m0 = TShape.LowIsZero ? 0 : ReadWord(ref lowRef, 0);
        uint m1 = TShape.LowIsZero ? 0 : ReadWord(ref lowRef, 4);
        uint m2 = TShape.LowIsZero ? 0 : ReadWord(ref lowRef, 8);
        uint m3 = TShape.LowIsZero ? 0 : ReadWord(ref lowRef, 12);
        uint m4 = TShape.LowIsZero ? 0 : ReadWord(ref lowRef, 16);
        uint m5 = TShape.LowIsZero ? 0 : ReadWord(ref lowRef, 20);
        uint m6 = TShape.LowIsZero ? 0 : ReadWord(ref lowRef, 24);
        uint m7 = TShape.LowIsZero ? 0 : ReadWord(ref lowRef, 28);
        uint m8 = TShape.HighIsZero ? 0 : ReadWord(ref highRef, 0);
        uint m9 = TShape.HighIsZero ? 0 : ReadWord(ref highRef, 4);
        uint m10 = TShape.HighIsZero ? 0 : ReadWord(ref highRef, 8);
        uint m11 = TShape.HighIsZero ? 0 : ReadWord(ref highRef, 12);
        uint m12 = TShape.HighIsZero ? 0 : ReadWord(ref highRef, 16);
        uint m13 = TShape.HighIsZero ? 0 : ReadWord(ref highRef, 20);
        uint m14 = TShape.HighIsZero ? 0 : ReadWord(ref highRef, 24);
        uint m15 = TShape.HighIsZero ? 0 : ReadWord(ref highRef, 28);

        uint v0 = cv[0], v1 = cv[1], v2 = cv[2], v3 = cv[3];
        uint v4 = cv[4], v5 = cv[5], v6 = cv[6], v7 = cv[7];
        uint v8 = Iv0, v9 = Iv1, v10 = Iv2, v11 = Iv3;
        uint v12 = (uint)counter, v13 = (uint)(counter >> 32), v14 = blockLength, v15 = flags;

        // round 1
        v0 += v4 + m0; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 += v4 + m1; v12 = BitOperations.RotateRight(v12 ^ v0, 8);  v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 += v5 + m2; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 += v5 + m3; v13 = BitOperations.RotateRight(v13 ^ v1, 8);  v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 += v6 + m4; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 += v6 + m5; v14 = BitOperations.RotateRight(v14 ^ v2, 8);  v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 += v7 + m6; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 += v7 + m7; v15 = BitOperations.RotateRight(v15 ^ v3, 8);  v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 += v5 + m8; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 += v5 + m9; v15 = BitOperations.RotateRight(v15 ^ v0, 8);  v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 += v6 + m10; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 += v6 + m11; v12 = BitOperations.RotateRight(v12 ^ v1, 8);  v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 += v7 + m12; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 += v7 + m13; v13 = BitOperations.RotateRight(v13 ^ v2, 8);  v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 += v4 + m14; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 += v4 + m15; v14 = BitOperations.RotateRight(v14 ^ v3, 8);  v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        // round 2
        v0 += v4 + m2; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 += v4 + m6; v12 = BitOperations.RotateRight(v12 ^ v0, 8);  v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 += v5 + m3; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 += v5 + m10; v13 = BitOperations.RotateRight(v13 ^ v1, 8);  v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 += v6 + m7; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 += v6 + m0; v14 = BitOperations.RotateRight(v14 ^ v2, 8);  v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 += v7 + m4; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 += v7 + m13; v15 = BitOperations.RotateRight(v15 ^ v3, 8);  v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 += v5 + m1; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 += v5 + m11; v15 = BitOperations.RotateRight(v15 ^ v0, 8);  v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 += v6 + m12; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 += v6 + m5; v12 = BitOperations.RotateRight(v12 ^ v1, 8);  v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 += v7 + m9; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 += v7 + m14; v13 = BitOperations.RotateRight(v13 ^ v2, 8);  v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 += v4 + m15; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 += v4 + m8; v14 = BitOperations.RotateRight(v14 ^ v3, 8);  v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        // round 3
        v0 += v4 + m3; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 += v4 + m4; v12 = BitOperations.RotateRight(v12 ^ v0, 8);  v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 += v5 + m10; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 += v5 + m12; v13 = BitOperations.RotateRight(v13 ^ v1, 8);  v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 += v6 + m13; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 += v6 + m2; v14 = BitOperations.RotateRight(v14 ^ v2, 8);  v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 += v7 + m7; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 += v7 + m14; v15 = BitOperations.RotateRight(v15 ^ v3, 8);  v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 += v5 + m6; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 += v5 + m5; v15 = BitOperations.RotateRight(v15 ^ v0, 8);  v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 += v6 + m9; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 += v6 + m0; v12 = BitOperations.RotateRight(v12 ^ v1, 8);  v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 += v7 + m11; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 += v7 + m15; v13 = BitOperations.RotateRight(v13 ^ v2, 8);  v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 += v4 + m8; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 += v4 + m1; v14 = BitOperations.RotateRight(v14 ^ v3, 8);  v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        // round 4
        v0 += v4 + m10; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 += v4 + m7; v12 = BitOperations.RotateRight(v12 ^ v0, 8);  v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 += v5 + m12; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 += v5 + m9; v13 = BitOperations.RotateRight(v13 ^ v1, 8);  v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 += v6 + m14; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 += v6 + m3; v14 = BitOperations.RotateRight(v14 ^ v2, 8);  v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 += v7 + m13; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 += v7 + m15; v15 = BitOperations.RotateRight(v15 ^ v3, 8);  v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 += v5 + m4; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 += v5 + m0; v15 = BitOperations.RotateRight(v15 ^ v0, 8);  v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 += v6 + m11; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 += v6 + m2; v12 = BitOperations.RotateRight(v12 ^ v1, 8);  v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 += v7 + m5; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 += v7 + m8; v13 = BitOperations.RotateRight(v13 ^ v2, 8);  v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 += v4 + m1; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 += v4 + m6; v14 = BitOperations.RotateRight(v14 ^ v3, 8);  v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        // round 5
        v0 += v4 + m12; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 += v4 + m13; v12 = BitOperations.RotateRight(v12 ^ v0, 8);  v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 += v5 + m9; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 += v5 + m11; v13 = BitOperations.RotateRight(v13 ^ v1, 8);  v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 += v6 + m15; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 += v6 + m10; v14 = BitOperations.RotateRight(v14 ^ v2, 8);  v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 += v7 + m14; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 += v7 + m8; v15 = BitOperations.RotateRight(v15 ^ v3, 8);  v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 += v5 + m7; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 += v5 + m2; v15 = BitOperations.RotateRight(v15 ^ v0, 8);  v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 += v6 + m5; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 += v6 + m3; v12 = BitOperations.RotateRight(v12 ^ v1, 8);  v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 += v7 + m0; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 += v7 + m1; v13 = BitOperations.RotateRight(v13 ^ v2, 8);  v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 += v4 + m6; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 += v4 + m4; v14 = BitOperations.RotateRight(v14 ^ v3, 8);  v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        // round 6
        v0 += v4 + m9; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 += v4 + m14; v12 = BitOperations.RotateRight(v12 ^ v0, 8);  v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 += v5 + m11; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 += v5 + m5; v13 = BitOperations.RotateRight(v13 ^ v1, 8);  v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 += v6 + m8; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 += v6 + m12; v14 = BitOperations.RotateRight(v14 ^ v2, 8);  v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 += v7 + m15; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 += v7 + m1; v15 = BitOperations.RotateRight(v15 ^ v3, 8);  v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 += v5 + m13; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 += v5 + m3; v15 = BitOperations.RotateRight(v15 ^ v0, 8);  v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 += v6 + m0; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 += v6 + m10; v12 = BitOperations.RotateRight(v12 ^ v1, 8);  v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 += v7 + m2; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 += v7 + m6; v13 = BitOperations.RotateRight(v13 ^ v2, 8);  v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 += v4 + m4; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 += v4 + m7; v14 = BitOperations.RotateRight(v14 ^ v3, 8);  v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        // round 7
        v0 += v4 + m11; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 += v4 + m15; v12 = BitOperations.RotateRight(v12 ^ v0, 8);  v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 += v5 + m5; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 += v5 + m0; v13 = BitOperations.RotateRight(v13 ^ v1, 8);  v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 += v6 + m1; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 += v6 + m9; v14 = BitOperations.RotateRight(v14 ^ v2, 8);  v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 += v7 + m8; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 += v7 + m6; v15 = BitOperations.RotateRight(v15 ^ v3, 8);  v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 += v5 + m14; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 += v5 + m10; v15 = BitOperations.RotateRight(v15 ^ v0, 8);  v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 += v6 + m2; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 += v6 + m12; v12 = BitOperations.RotateRight(v12 ^ v1, 8);  v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 += v7 + m3; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 += v7 + m4; v13 = BitOperations.RotateRight(v13 ^ v2, 8);  v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 += v4 + m7; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 += v4 + m13; v14 = BitOperations.RotateRight(v14 ^ v3, 8);  v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        cv[0] = v0 ^ v8; cv[1] = v1 ^ v9; cv[2] = v2 ^ v10; cv[3] = v3 ^ v11;
        cv[4] = v4 ^ v12; cv[5] = v5 ^ v13; cv[6] = v6 ^ v14; cv[7] = v7 ^ v15;
    }
}
