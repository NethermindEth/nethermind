// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public partial class TreePathTests
{
    [TestCase(0u)]
    [TestCase(0x55555555u)]
    [TestCase(uint.MaxValue)]
    public void Tiny_path_chaining_breaks_raw_crc_collision_family(uint seed)
    {
        const int count = 256;
        // Shifted copies of the reflected CRC32C generator leave the checksum unchanged.
        const ulong crcKernel = 0x105EC76F1UL;
        HashSet<int> rawHashes = [];
        HashSet<int> chainedHashes = [];
        for (int value = 0; value < count; value++)
        {
            ulong data = (ulong)TinyTreePath.MaxNibbleLength << 56;
            for (int bit = 0; bit < 8; bit++)
                if ((value & (1 << bit)) != 0) data ^= crcKernel << bit;

            ValueHash256 hash = Keccak.Zero;
            BinaryPrimitives.WriteUInt64LittleEndian(hash.BytesAsSpan, data);
            TinyTreePath path = new(new TreePath(hash, TinyTreePath.MaxNibbleLength));
            rawHashes.Add(SpanExtensions.CombineHash(seed, data));
            chainedHashes.Add(path.GetChainedHashCode(seed));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rawHashes.Count, Is.EqualTo(1));
            Assert.That(chainedHashes.Count, Is.GreaterThan(count - 4));
        }
    }

}
