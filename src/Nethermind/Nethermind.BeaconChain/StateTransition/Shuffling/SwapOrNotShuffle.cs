// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.StateTransition.Shuffling;

/// <summary>
/// The "swap-or-not" shuffle used for beacon chain committee assignment and proposer selection.
/// </summary>
/// <remarks>
/// Implements both the per-index <c>compute_shuffled_index</c> from consensus-specs and the
/// optimized whole-list shuffle (credit to @protolambda), ported from Lighthouse's
/// <c>swap_or_not_shuffle</c> crate. The list variant is orders of magnitude faster than calling
/// <see cref="ComputeShuffledIndex"/> per index.
/// </remarks>
public static class SwapOrNotShuffle
{
    private const int SeedSize = 32;
    private const int PivotViewSize = SeedSize + 1;
    private const int TotalSize = SeedSize + 1 + 4;

    /// <summary>The spec's <c>int_to_bytes4(position // 256)</c> limits list sizes to 2^24.</summary>
    private const int MaxListSize = 1 << 24;

    /// <summary>Returns <c>compute_shuffled_index(index)</c>: the shuffled position of <paramref name="index"/>.</summary>
    /// <param name="seed">A 32-byte shuffling seed.</param>
    public static int ComputeShuffledIndex(int index, int listSize, ReadOnlySpan<byte> seed, int rounds = Presets.ShuffleRoundCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, listSize);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listSize, MaxListSize);

        Span<byte> buf = stackalloc byte[TotalSize];
        Span<byte> hash = stackalloc byte[SeedSize];
        seed[..SeedSize].CopyTo(buf);

        for (int round = 0; round < rounds; round++)
        {
            buf[SeedSize] = (byte)round;
            SHA256.HashData(buf[..PivotViewSize], hash);
            int pivot = (int)(BinaryPrimitives.ReadUInt64LittleEndian(hash) % (ulong)listSize);

            int flip = (int)(((long)pivot + listSize - index) % listSize);
            int position = Math.Max(index, flip);
            BinaryPrimitives.WriteInt32LittleEndian(buf[PivotViewSize..], position >> 8);
            SHA256.HashData(buf, hash);

            int bit = (hash[(position & 0xff) >> 3] >> (position & 0x07)) & 0x01;
            index = bit == 1 ? flip : index;
        }

        return index;
    }

    /// <summary>Shuffles (or un-shuffles) <paramref name="input"/> in place.</summary>
    /// <param name="seed">A 32-byte shuffling seed.</param>
    /// <param name="forwards">
    /// <c>false</c> applies the spec permutation — the result equals
    /// <c>[input[ComputeShuffledIndex(i)] for i]</c>, which is what committee shuffling uses;
    /// <c>true</c> applies the inverse, so shuffling forwards then backwards is the identity.
    /// </param>
    public static void ShuffleList(Span<int> input, ReadOnlySpan<byte> seed, bool forwards = false, int rounds = Presets.ShuffleRoundCount)
    {
        int listSize = input.Length;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listSize, MaxListSize);
        if (listSize == 0 || rounds == 0)
            return;

        Span<byte> buf = stackalloc byte[TotalSize];
        Span<byte> source = stackalloc byte[SeedSize];
        seed[..SeedSize].CopyTo(buf);

        int round = forwards ? 0 : rounds - 1;
        while (true)
        {
            buf[SeedSize] = (byte)round;
            SHA256.HashData(buf[..PivotViewSize], source);
            int pivot = (int)(BinaryPrimitives.ReadUInt64LittleEndian(source) % (ulong)listSize);

            // First mirror: swap-or-not the range [0, pivot] around its midpoint.
            SwapOrNotRange(input, buf, source, hi: pivot, count: (pivot + 1) >> 1, lo: 0);
            // Second mirror: swap-or-not the range (pivot, listSize) around its midpoint.
            SwapOrNotRange(input, buf, source, hi: listSize - 1, count: ((pivot + listSize + 1) >> 1) - (pivot + 1), lo: pivot + 1);

            if (forwards)
            {
                if (++round == rounds)
                    break;
            }
            else
            {
                if (round == 0)
                    break;
                round--;
            }
        }
    }

    /// <summary>
    /// Swaps <c>input[lo + k]</c> with <c>input[hi - k]</c> for <c>k in [0, count)</c> whenever the
    /// decision bit for position <c>hi - k</c> is set, fetching a new hash per 256 positions.
    /// </summary>
    private static void SwapOrNotRange(Span<int> input, Span<byte> buf, Span<byte> source, int hi, int count, int lo)
    {
        MixInPosition(buf, hi >> 8);
        SHA256.HashData(buf, source);
        byte byteV = source[(hi & 0xff) >> 3];

        for (int k = 0; k < count; k++)
        {
            int i = lo + k;
            int j = hi - k;

            if ((j & 0xff) == 0xff)
            {
                MixInPosition(buf, j >> 8);
                SHA256.HashData(buf, source);
            }

            if ((j & 0x07) == 0x07)
            {
                byteV = source[(j & 0xff) >> 3];
            }

            if (((byteV >> (j & 0x07)) & 0x01) == 1)
            {
                (input[i], input[j]) = (input[j], input[i]);
            }
        }
    }

    private static void MixInPosition(Span<byte> buf, int position) =>
        BinaryPrimitives.WriteInt32LittleEndian(buf[PivotViewSize..], position);
}
