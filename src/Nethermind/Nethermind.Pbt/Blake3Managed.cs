// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Pbt;

/// <summary>Managed, unkeyed BLAKE3 producing a 32-byte digest.</summary>
/// <remarks>Avoids P/Invoke overhead for EIP-8297's single-chunk inputs (at most 1024 bytes, typically
/// under 100). Longer inputs use a correctness-oriented general path. Every stack buffer below is fully
/// written or explicitly cleared before it is read, so the frames skip zero-initialization.</remarks>
[SkipLocalsInit]
public static partial class Blake3Managed
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
        // A single chunk is the root itself: its last block carries ROOT and no merge stack is needed.
        if (input.Length > ChunkLength) HashLong(input, cv);
        else ChunkChainingValue(input, 0, Root, cv);
        WriteWords(cv, output32);
    }

    /// <summary>Hashes an input longer than one chunk through the chunk merge tree.</summary>
    private static void HashLong(ReadOnlySpan<byte> input, Span<uint> cv)
    {
        Debug.Assert(input.Length > ChunkLength);
        // Chaining values of the complete subtrees to the left of the current chunk, each covering a
        // power-of-two run of chunks. A chunk count with n trailing zero bits completes n merges, and the
        // counter is 64-bit, so 54 entries cover every input a caller can hold.
        Span<uint> stack = stackalloc uint[54 * 8];
        int stackLength = 0;
        ulong chunkCounter = 0;

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

        // Only the topmost node, the last parent merged, is compressed with ROOT set.
        ChunkChainingValue(input, chunkCounter, 0, cv);
        while (stackLength > 0)
            MergeParent(stack.Slice(--stackLength * 8, 8), cv, stackLength == 0 ? Root : 0);
    }

    /// <summary>
    /// Writes the digests of two independent inputs, compressing them in lock-step so that their dependency
    /// chains overlap (see <see cref="CompressSse41Two"/>).
    /// </summary>
    /// <remarks>
    /// Both inputs must be single chunks for the lanes to share a block loop; a longer input, or a platform
    /// without SSE4.1, falls back to hashing each input on its own. When the inputs span different numbers of
    /// blocks, the shorter lane's root block runs in the last shared step and the longer lane finishes alone.
    /// </remarks>
    public static void HashTwo(ReadOnlySpan<byte> inputA, Span<byte> outputA32, ReadOnlySpan<byte> inputB, Span<byte> outputB32)
    {
        if (!Sse41.IsSupported || inputA.Length > ChunkLength || inputB.Length > ChunkLength)
        {
            Hash(inputA, outputA32);
            Hash(inputB, outputB32);
            return;
        }

        Span<uint> cvA = stackalloc uint[8];
        Span<uint> cvB = stackalloc uint[8];
        Span<byte> lastA = stackalloc byte[BlockLength];
        Span<byte> lastB = stackalloc byte[BlockLength];
        InitialCv(cvA);
        InitialCv(cvB);
        uint flagsA = ChunkStart, flagsB = ChunkStart;
        // Scoped copies so that the padding buffers may be passed by ref-returning NextBlock alongside them.
        scoped ReadOnlySpan<byte> remainingA = inputA;
        scoped ReadOnlySpan<byte> remainingB = inputB;
        while (true)
        {
            ref byte blockA = ref NextBlock(ref remainingA, lastA, ref flagsA, out uint blockLengthA, out bool isLastA);
            ref byte blockB = ref NextBlock(ref remainingB, lastB, ref flagsB, out uint blockLengthB, out bool isLastB);
            CompressSse41Two(cvA, ref blockA, blockLengthA, flagsA, cvB, ref blockB, blockLengthB, flagsB);
            if (isLastA && isLastB) break;
            if (isLastA)
            {
                ContinueChunk(remainingB, 0, 0, Root, cvB);
                break;
            }
            if (isLastB)
            {
                ContinueChunk(remainingA, 0, 0, Root, cvA);
                break;
            }
            flagsA = 0;
            flagsB = 0;
        }

        WriteWords(cvA, outputA32);
        WriteWords(cvB, outputB32);
    }

    /// <summary>
    /// Takes the next block off <paramref name="input"/>: a full block, or, when at most <see cref="BlockLength"/>
    /// bytes remain, the root block zero-padded into <paramref name="last"/>, in which case <paramref name="flags"/>
    /// gains the chunk end and root flags and <paramref name="isLast"/> is set.
    /// </summary>
    private static ref byte NextBlock(ref ReadOnlySpan<byte> input, Span<byte> last, ref uint flags, out uint blockLength, out bool isLast)
    {
        if (input.Length > BlockLength)
        {
            ref byte block = ref Reference(input);
            blockLength = BlockLength;
            isLast = false;
            input = input[BlockLength..];
            return ref block;
        }

        PadBlock(input, last);
        blockLength = (uint)input.Length;
        isLast = true;
        flags |= ChunkEnd | Root;
        return ref MemoryMarshal.GetReference(last);
    }

    /// <summary>Compresses one whole chunk (at most <see cref="ChunkLength"/> bytes) into its chaining value.</summary>
    private static void ChunkChainingValue(ReadOnlySpan<byte> chunk, ulong counter, uint rootFlag, Span<uint> cv)
    {
        InitialCv(cv);
        ContinueChunk(chunk, counter, ChunkStart, rootFlag, cv);
    }

    /// <summary>Compresses the remaining blocks of a chunk into <paramref name="cv"/>, the first with <paramref name="flags"/>.</summary>
    private static void ContinueChunk(ReadOnlySpan<byte> chunk, ulong counter, uint flags, uint rootFlag, Span<uint> cv)
    {
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
        Debug.Assert(cv.Length == 8);
        ref uint cvRef = ref MemoryMarshal.GetReference(cv);
        Vector128.Create(Iv0, Iv1, Iv2, Iv3).StoreUnsafe(ref cvRef);
        Vector128.Create(Iv4, Iv5, Iv6, Iv7).StoreUnsafe(ref cvRef, 4);
    }

    private static void WriteWords(ReadOnlySpan<uint> words, Span<byte> destination)
    {
        Debug.Assert(words.Length == 8 && destination.Length >= 32);
        if (!BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < 8; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * 4)..], words[i]);
            return;
        }

        ref uint wordsRef = ref MemoryMarshal.GetReference(words);
        ref byte destinationRef = ref MemoryMarshal.GetReference(destination);
        Vector128.LoadUnsafe(ref wordsRef).AsByte().StoreUnsafe(ref destinationRef);
        Vector128.LoadUnsafe(ref wordsRef, 4).AsByte().StoreUnsafe(ref destinationRef, 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadWord(ref byte block, int offset)
    {
        uint word = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, offset));
        return BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word);
    }

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
    /// A half that <typeparamref name="TShape"/> declares zero is never read, so its reference is not
    /// required to be valid. A short final block is zero-padded by the caller and its true length passed as
    /// <paramref name="blockLength"/>. On SSE4.1 the compression runs on 128-bit rows
    /// (<see cref="CompressSse41{TShape}"/>); elsewhere it is held in scalar locals so that it stays in
    /// registers: the state is a fixed 16 words and the round message order is a compile-time permutation,
    /// so the rounds unroll with no indexing and no bounds checks.
    /// </remarks>
    private static void Compress<TShape>(Span<uint> cv, ref byte lowRef, ref byte highRef, ulong counter, uint blockLength, uint flags)
        where TShape : IBlockShape
    {
        if (Sse41.IsSupported) CompressSse41<TShape>(cv, ref lowRef, ref highRef, counter, blockLength, flags);
        else CompressScalar<TShape>(cv, ref lowRef, ref highRef, counter, blockLength, flags);
    }

    // Each half-step adds the message word to the state word before the critical operand, which the
    // previous half-step produced last, so that only one add depends on it.
    private static void CompressScalar<TShape>(Span<uint> cv, ref byte lowRef, ref byte highRef, ulong counter, uint blockLength, uint flags)
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

        v0 = v0 + m0 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 = v0 + m1 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 8); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 = v1 + m2 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 = v1 + m3 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 8); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 = v2 + m4 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 = v2 + m5 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 8); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 = v3 + m6 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 = v3 + m7 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 8); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 = v0 + m8 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 = v0 + m9 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 8); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 = v1 + m10 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 = v1 + m11 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 8); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 = v2 + m12 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 = v2 + m13 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 8); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 = v3 + m14 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 = v3 + m15 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 8); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        v0 = v0 + m2 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 = v0 + m6 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 8); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 = v1 + m3 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 = v1 + m10 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 8); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 = v2 + m7 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 = v2 + m0 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 8); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 = v3 + m4 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 = v3 + m13 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 8); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 = v0 + m1 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 = v0 + m11 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 8); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 = v1 + m12 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 = v1 + m5 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 8); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 = v2 + m9 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 = v2 + m14 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 8); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 = v3 + m15 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 = v3 + m8 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 8); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        v0 = v0 + m3 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 = v0 + m4 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 8); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 = v1 + m10 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 = v1 + m12 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 8); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 = v2 + m13 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 = v2 + m2 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 8); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 = v3 + m7 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 = v3 + m14 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 8); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 = v0 + m6 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 = v0 + m5 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 8); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 = v1 + m9 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 = v1 + m0 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 8); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 = v2 + m11 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 = v2 + m15 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 8); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 = v3 + m8 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 = v3 + m1 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 8); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        v0 = v0 + m10 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 = v0 + m7 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 8); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 = v1 + m12 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 = v1 + m9 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 8); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 = v2 + m14 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 = v2 + m3 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 8); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 = v3 + m13 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 = v3 + m15 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 8); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 = v0 + m4 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 = v0 + m0 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 8); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 = v1 + m11 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 = v1 + m2 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 8); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 = v2 + m5 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 = v2 + m8 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 8); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 = v3 + m1 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 = v3 + m6 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 8); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        v0 = v0 + m12 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 = v0 + m13 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 8); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 = v1 + m9 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 = v1 + m11 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 8); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 = v2 + m15 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 = v2 + m10 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 8); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 = v3 + m14 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 = v3 + m8 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 8); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 = v0 + m7 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 = v0 + m2 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 8); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 = v1 + m5 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 = v1 + m3 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 8); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 = v2 + m0 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 = v2 + m1 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 8); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 = v3 + m6 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 = v3 + m4 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 8); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        v0 = v0 + m9 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 = v0 + m14 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 8); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 = v1 + m11 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 = v1 + m5 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 8); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 = v2 + m8 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 = v2 + m12 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 8); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 = v3 + m15 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 = v3 + m1 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 8); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 = v0 + m13 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 = v0 + m3 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 8); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 = v1 + m0 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 = v1 + m10 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 8); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 = v2 + m2 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 = v2 + m6 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 8); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 = v3 + m4 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 = v3 + m7 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 8); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        v0 = v0 + m11 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
        v0 = v0 + m15 + v4; v12 = BitOperations.RotateRight(v12 ^ v0, 8); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
        v1 = v1 + m5 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
        v1 = v1 + m0 + v5; v13 = BitOperations.RotateRight(v13 ^ v1, 8); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
        v2 = v2 + m1 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
        v2 = v2 + m9 + v6; v14 = BitOperations.RotateRight(v14 ^ v2, 8); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
        v3 = v3 + m8 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
        v3 = v3 + m6 + v7; v15 = BitOperations.RotateRight(v15 ^ v3, 8); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
        v0 = v0 + m14 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
        v0 = v0 + m10 + v5; v15 = BitOperations.RotateRight(v15 ^ v0, 8); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
        v1 = v1 + m2 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
        v1 = v1 + m12 + v6; v12 = BitOperations.RotateRight(v12 ^ v1, 8); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
        v2 = v2 + m3 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
        v2 = v2 + m4 + v7; v13 = BitOperations.RotateRight(v13 ^ v2, 8); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
        v3 = v3 + m7 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
        v3 = v3 + m13 + v4; v14 = BitOperations.RotateRight(v14 ^ v3, 8); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);

        cv[0] = v0 ^ v8; cv[1] = v1 ^ v9; cv[2] = v2 ^ v10; cv[3] = v3 ^ v11;
        cv[4] = v4 ^ v12; cv[5] = v5 ^ v13; cv[6] = v6 ^ v14; cv[7] = v7 ^ v15;
    }
}
