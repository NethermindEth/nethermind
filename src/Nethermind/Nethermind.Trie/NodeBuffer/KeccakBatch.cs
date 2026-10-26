// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Keccak-256 of many messages in one buffer, sharing the multi-lane kernels where the CPU has them.</summary>
/// <remarks>Messages are grouped by the number of rate blocks their padding rounds them up to, so the lanes of one
/// kernel call run the same number of permutations.</remarks>
internal static class KeccakBatch
{
    private const int Avx512Lanes = 8;
    private const int Avx2Lanes = 4;
    private const int MinimumBatchCount = 2;
    private const int MaxPaddedClass = KeccakHash.MaxBatchablePaddedLength / KeccakHash.RateBlockLength;
    private const int VectorByteLength = 32;

    [InlineArray(Avx512Lanes * KeccakHash.MaxBatchablePaddedLength / VectorByteLength)]
    private struct Staging
    {
        private Vector256<byte> _element0;
    }

    /// <param name="buffer">Holds every message.</param>
    /// <param name="messages">Where each message lies in <paramref name="buffer"/>.</param>
    /// <param name="hashes">Receives the hash of each message, in the order of <paramref name="messages"/>.</param>
    public static void Hash(ReadOnlySpan<byte> buffer, ReadOnlySpan<(int Start, int Length)> messages, Span<ValueHash256> hashes)
    {
        if (!Avx2.IsSupported || messages.Length < MinimumBatchCount)
        {
            for (int i = 0; i < messages.Length; i++) HashOne(buffer, messages, i, hashes);
            return;
        }

        int kernelWidth = Avx512F.IsSupported ? Avx512Lanes : Avx2Lanes;
        Span<int> byClass = stackalloc int[MaxPaddedClass * Avx512Lanes];
        Span<int> classCount = stackalloc int[MaxPaddedClass];
        classCount.Clear();
        Unsafe.SkipInit(out Staging staging);
        Span<byte> inputs = MemoryMarshal.AsBytes((Span<Vector256<byte>>)staging);
        Span<ValueHash256> laneHashes = stackalloc ValueHash256[Avx512Lanes];

        for (int i = 0; i < messages.Length; i++)
        {
            if (messages[i].Length >= KeccakHash.MaxBatchablePaddedLength)
            {
                HashOne(buffer, messages, i, hashes);
                continue;
            }

            int classIndex = messages[i].Length / KeccakHash.RateBlockLength;
            Span<int> group = byClass.Slice(classIndex * Avx512Lanes, Avx512Lanes);
            group[classCount[classIndex]++] = i;
            if (classCount[classIndex] == kernelWidth)
            {
                HashGroup(buffer, messages, group[..kernelWidth], classIndex + 1, inputs, laneHashes, hashes);
                classCount[classIndex] = 0;
            }
        }

        for (int classIndex = 0; classIndex < MaxPaddedClass; classIndex++)
        {
            HashGroup(buffer, messages, byClass.Slice(classIndex * Avx512Lanes, classCount[classIndex]), classIndex + 1, inputs, laneHashes, hashes);
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
        int lanes = Avx512F.IsSupported && group.Length > Avx2Lanes ? Avx512Lanes : Avx2Lanes;
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

        ComputePadded(inputs, paddedLength, lanes == Avx512Lanes, ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(laneHashes)));

        for (int lane = 0; lane < group.Length; lane++)
        {
            hashes[group[lane]] = laneHashes[lane];
        }
    }

    /// <summary>Hashes padded messages of <paramref name="paddedLength"/> bytes each, lying back to back in <paramref name="inputs"/>, in one kernel call.</summary>
    /// <param name="eightLanes">Whether the AVX-512 kernel hashes eight messages, rather than the AVX2 one four.</param>
    /// <param name="output">Receives the 32-byte hash of each lane, cleared lanes included.</param>
    internal static void ComputePadded(Span<byte> inputs, int paddedLength, bool eightLanes, ref byte output)
    {
        if (paddedLength == KeccakHash.RateBlockLength)
        {
            if (eightLanes)
                KeccakHash.ComputePaddedBlocks8Avx512(ref inputs[0], ref output);
            else
                KeccakHash.ComputePaddedBlocks4Avx2(ref inputs[0], ref output);
        }
        else if (eightLanes)
        {
            KeccakHash.ComputePaddedMultiBlocks8Avx512(ref inputs[0], paddedLength, ref output);
        }
        else
        {
            KeccakHash.ComputePaddedMultiBlocks4Avx2(ref inputs[0], paddedLength, ref output);
        }
    }
}
