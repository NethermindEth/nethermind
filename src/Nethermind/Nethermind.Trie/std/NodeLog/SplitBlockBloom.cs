// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Trie.NodeLog;

/// <summary>A concurrent split block bloom filter of well-mixed 64-bit keys.</summary>
/// <remarks>
/// The layout of the Parquet split block bloom filter with 512-bit blocks of eight 64-bit words: the high half of a key picks the block
/// and the low half, multiplied by one salt per word, sets one bit in each word. Inserts are atomic, so they may race with each other and with lookups.
/// </remarks>
/// <param name="keyCapacity">The number of keys the filter is sized for, at <see cref="BitsPerKey"/> bits each.</param>
internal sealed class SplitBlockBloom(int keyCapacity)
{
    public const int BitsPerKey = 14;

    private const int WordsPerBlock = 8;
    private const int BlockBits = WordsPerBlock * 64;

    private static ReadOnlySpan<uint> Salts => [0x47b6137b, 0x44974d91, 0x8824ad5b, 0xa2b7289d, 0x705495c7, 0x2df1424b, 0x9efc4947, 0x5c6bfb31];

    private readonly ulong[] _words = new ulong[Math.Max(1, ((long)keyCapacity * BitsPerKey + BlockBits - 1) / BlockBits) * WordsPerBlock];

    public void Insert(ulong key)
    {
        Span<ulong> block = Block(key);
        for (int i = 0; i < WordsPerBlock; i++)
        {
            Interlocked.Or(ref block[i], Mask(key, i));
        }
    }

    public bool Contains(ulong key)
    {
        Span<ulong> block = Block(key);
        for (int i = 0; i < WordsPerBlock; i++)
        {
            ulong mask = Mask(key, i);
            if ((Volatile.Read(ref block[i]) & mask) != mask) return false;
        }

        return true;
    }

    private Span<ulong> Block(ulong key)
    {
        ulong blockCount = (ulong)(_words.Length / WordsPerBlock);
        return _words.AsSpan((int)(((key >> 32) * blockCount) >> 32) * WordsPerBlock, WordsPerBlock);
    }

    private static ulong Mask(ulong key, int word) => 1UL << (int)((uint)key * Salts[word] >> 26);
}
