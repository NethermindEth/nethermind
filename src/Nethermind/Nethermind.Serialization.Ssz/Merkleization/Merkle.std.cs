// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    /// <summary>Hashes <paramref name="data"/> into <paramref name="output"/> with SHA-256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sha256(ReadOnlySpan<byte> data, Span<byte> output) => SHA256.HashData(data, output);

    private static void MerkleizeChunks(out UInt256 root, ReadOnlySpan<UInt256> chunks, ReadOnlySpan<UInt256> lastChunk, int depth)
    {
        const int parallelThreshold = 32768;
        ulong count = (ulong)chunks.Length + (ulong)lastChunk.Length;
        if (count < parallelThreshold || Environment.ProcessorCount <= 1)
        {
            MerkleizeSequential(out root, chunks, lastChunk, depth);
            return;
        }

        MerkleizeParallel(out root, chunks, lastChunk, NextPowerOfTwoExponent(count), depth);
    }

    private static unsafe void MerkleizeParallel(out UInt256 root, ReadOnlySpan<UInt256> chunks, ReadOnlySpan<UInt256> lastChunk, int dataDepth, int depth)
    {
        const int workerCount = 4;
        int subtreeDepth = dataDepth - 2;
        int subtreeWidth = 1 << subtreeDepth;
        int chunkCount = chunks.Length;
        bool hasLastChunk = !lastChunk.IsEmpty;
        UInt256[] subtrees = new UInt256[workerCount];

        // Both borrowed spans remain pinned and live until Parallel.For joins every worker,
        // including on failure. Workers only read input and write their own result slot.
        fixed (UInt256* chunksPointer = chunks)
        fixed (UInt256* lastChunkPointer = lastChunk)
        {
            nint chunksAddress = (nint)chunksPointer;
            nint lastChunkAddress = (nint)lastChunkPointer;
            Parallel.For(0, workerCount, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(workerCount, Environment.ProcessorCount) }, i =>
            {
                int offset = i * subtreeWidth;
                int count = Math.Clamp(chunkCount - offset, 0, subtreeWidth);
                ReadOnlySpan<UInt256> subtree = count == 0
                    ? ReadOnlySpan<UInt256>.Empty
                    : new ReadOnlySpan<UInt256>((UInt256*)chunksAddress + offset, count);
                ReadOnlySpan<UInt256> tail = hasLastChunk && chunkCount >= offset && chunkCount - offset < subtreeWidth
                    ? new ReadOnlySpan<UInt256>((void*)lastChunkAddress, 1)
                    : ReadOnlySpan<UInt256>.Empty;
                MerkleizeSequential(out subtrees[i], subtree, tail, subtreeDepth);
            });
        }

        UInt256 left = HashConcatenation(subtrees[0], subtrees[1], subtreeDepth);
        UInt256 right = HashConcatenation(subtrees[2], subtrees[3], subtreeDepth);
        root = HashConcatenation(left, right, subtreeDepth + 1);
        for (int level = dataDepth; level < depth; level++)
        {
            root = HashConcatenation(root, ZeroHash(level), level);
        }
    }
}
