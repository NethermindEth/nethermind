// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Keccak-256 of many messages in one buffer, sharing the multi-lane kernels where the CPU has them.</summary>
/// <remarks>Messages are grouped by the number of rate blocks their padding rounds them up to, so the lanes of one
/// kernel call run the same number of permutations.</remarks>
internal static class KeccakBatch
{
    private const int MaxLanes = 8;
    private const int MinimumBatchCount = 2;
    private const int MaxPaddedClass = KeccakHash.MaxBatchablePaddedLength / KeccakHash.RateBlockLength;
    private const int VectorByteLength = 32;

    [InlineArray(MaxLanes * KeccakHash.MaxBatchablePaddedLength / VectorByteLength)]
    private struct Staging
    {
        private Vector256<byte> _element0;
    }

    /// <param name="buffer">Holds every message.</param>
    /// <param name="messages">Where each message lies in <paramref name="buffer"/>.</param>
    /// <param name="hashes">Receives the hash of each message, in the order of <paramref name="messages"/>.</param>
    public static void Hash(ReadOnlySpan<byte> buffer, ReadOnlySpan<(int Start, int Length)> messages, Span<ValueHash256> hashes)
    {
        if (KeccakHash.MaxBatchWidth == 0 || messages.Length < MinimumBatchCount)
        {
            for (int i = 0; i < messages.Length; i++) HashOne(buffer, messages, i, hashes);
            return;
        }

        HashBatched(buffer, messages, hashes);
    }

    [SkipLocalsInit]
    private static void HashBatched(ReadOnlySpan<byte> buffer, ReadOnlySpan<(int Start, int Length)> messages, Span<ValueHash256> hashes)
    {
        int kernelWidth = KeccakHash.MaxBatchWidth;
        Span<int> byClass = stackalloc int[MaxPaddedClass * MaxLanes];
        Span<int> classCount = stackalloc int[MaxPaddedClass];
        classCount.Clear();
        Unsafe.SkipInit(out Staging staging);
        Span<byte> inputs = MemoryMarshal.AsBytes((Span<Vector256<byte>>)staging);
        Span<ValueHash256> laneHashes = stackalloc ValueHash256[MaxLanes];

        for (int i = 0; i < messages.Length; i++)
        {
            if (messages[i].Length >= KeccakHash.MaxBatchablePaddedLength)
            {
                HashOne(buffer, messages, i, hashes);
                continue;
            }

            int classIndex = messages[i].Length / KeccakHash.RateBlockLength;
            Span<int> group = byClass.Slice(classIndex * MaxLanes, MaxLanes);
            group[classCount[classIndex]++] = i;
            if (classCount[classIndex] == kernelWidth)
            {
                HashGroup(buffer, messages, group[..kernelWidth], classIndex + 1, inputs, laneHashes, hashes);
                classCount[classIndex] = 0;
            }
        }

        for (int classIndex = 0; classIndex < MaxPaddedClass; classIndex++)
        {
            HashGroup(buffer, messages, byClass.Slice(classIndex * MaxLanes, classCount[classIndex]), classIndex + 1, inputs, laneHashes, hashes);
        }
    }

    private static void HashOne(ReadOnlySpan<byte> buffer, ReadOnlySpan<(int Start, int Length)> messages, int index, Span<ValueHash256> hashes)
        => hashes[index] = ValueKeccak.Compute(buffer.Slice(messages[index].Start, messages[index].Length));

    private static void HashGroup(ReadOnlySpan<byte> buffer, ReadOnlySpan<(int Start, int Length)> messages, ReadOnlySpan<int> group,
        int paddedClass, Span<byte> staging, Span<ValueHash256> laneHashes, Span<ValueHash256> hashes)
    {
        if (group.Length < MinimumBatchCount)
        {
            foreach (int index in group) HashOne(buffer, messages, index, hashes);
            return;
        }

        // The narrowest kernel that covers the group, so a small one does not permute eight lanes.
        int lanes = KeccakHash.GetBatchWidth(group.Length);
        int paddedLength = paddedClass * KeccakHash.RateBlockLength;
        Span<byte> inputs = staging[..(lanes * paddedLength)];
        inputs.Clear();
        for (int lane = 0; lane < group.Length; lane++)
        {
            (int start, int length) = messages[group[lane]];
            Span<byte> input = inputs.Slice(lane * paddedLength, paddedLength);
            buffer.Slice(start, length).CopyTo(input);
            // Both padding bytes land on the same byte when the message fills all but one byte of its blocks, so they merge.
            input[length] |= 0x01;
            input[^1] |= 0x80;
        }

        KeccakHash.ComputePaddedBatch(ref inputs[0], paddedLength, lanes, ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(laneHashes)));

        for (int lane = 0; lane < group.Length; lane++)
        {
            hashes[group[lane]] = laneHashes[lane];
        }
    }
}
