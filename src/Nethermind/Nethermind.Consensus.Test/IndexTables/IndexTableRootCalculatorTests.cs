// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexTableRootCalculatorTests
{
    /// <summary>
    /// Recomputes the SSZ <c>List[Hash32, N]</c> root from raw bytes, independently of
    /// <c>Merkle</c>, so that a byte order applied consistently on both the write and the
    /// verification path cannot pass. A reversed leaf chunk would not match another client's root.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(5)]
    [TestCase(8)]
    public void ComputeRoot_matches_ssz_list_root_computed_over_raw_bytes(int entryCount)
    {
        List<IndexEntry> entries = new(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            entries.Add(IndexEntry.CreateTransaction(TestItem.Keccaks[i], (ulong)i, (uint)i, 0));
        }

        entries.Sort();

        // Leaf chunks are the SHA-256 digests of the encoded entries, in digest byte order.
        List<byte[]> level = new(entryCount);
        foreach (IndexEntry entry in entries)
        {
            byte[] encoded = new byte[entry.EncodedLength];
            entry.Encode(encoded);
            level.Add(SHA256.HashData(encoded));
        }

        // Pad to a power of two with zero chunks, then hash pairwise up to the root.
        while (level.Count < (1 << Depth(entryCount)))
        {
            level.Add(new byte[32]);
        }

        while (level.Count > 1)
        {
            List<byte[]> parent = new(level.Count / 2);
            for (int i = 0; i < level.Count; i += 2)
            {
                parent.Add(Sha256Pair(level[i], level[i + 1]));
            }

            level = parent;
        }

        // SSZ list length mix-in: the count as a little-endian uint256 chunk.
        byte[] lengthChunk = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(lengthChunk, (ulong)entryCount);
        byte[] expected = Sha256Pair(level[0], lengthChunk);

        byte[] actual = new byte[32];
        IndexTableRootCalculator.ComputeRoot(entries).ToLittleEndian(actual);

        Assert.That(actual, Is.EqualTo(expected));
    }

    private static int Depth(int count)
    {
        int depth = 0;
        while ((1 << depth) < count)
        {
            depth++;
        }

        return depth;
    }

    private static byte[] Sha256Pair(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Span<byte> buffer = stackalloc byte[64];
        left.CopyTo(buffer);
        right.CopyTo(buffer[32..]);
        return SHA256.HashData(buffer);
    }
}
