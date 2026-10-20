// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Tests for the lazily filled three-nibble path table in <c>HexPrefix.zkevm.cs</c>.</summary>
/// <remarks>
/// The table is process-wide and filled without synchronization. A racing fill only hands out equal-content
/// duplicates, but that breaks the reference identity asserted here, hence NonParallelizable. Each slot is
/// created on its own first use, so every path is checked, and the entry point that creates it rotates so each
/// of them is seen filling a slot.
/// </remarks>
[NonParallelizable]
public class GuestHexPrefixTests
{
    [Test]
    public void Every_three_nibble_path_is_one_shared_array_across_entry_points()
    {
        for (int index = 0; index < 4096; index++)
        {
            byte v0 = (byte)(index >> 8), v1 = (byte)((index >> 4) & 0xF), v2 = (byte)(index & 0xF);
            byte[] first = (index & 3) switch
            {
                0 => HexPrefix.GetArray([v0, v1, v2]),
                1 => HexPrefix.FromBytes([(byte)(0x10 | v0), (byte)((v1 << 4) | v2)]).key,
                2 => HexPrefix.PrependNibble(v0, [v1, v2]),
                _ => HexPrefix.ConcatNibbles([v0], [v1, v2])
            };

            Assert.That(first, Is.EqualTo(new[] { v0, v1, v2 }), $"path {index:x3}");
            Assert.That(HexPrefix.GetArray([v0, v1, v2]), Is.SameAs(first), $"GetArray {index:x3}");
            Assert.That(HexPrefix.FromBytes([(byte)(0x30 | v0), (byte)((v1 << 4) | v2)]).key, Is.SameAs(first), $"FromBytes {index:x3}");
            Assert.That(HexPrefix.PrependNibble(v0, [v1, v2]), Is.SameAs(first), $"PrependNibble {index:x3}");
            Assert.That(HexPrefix.ConcatNibbles([v0, v1], [v2]), Is.SameAs(first), $"ConcatNibbles {index:x3}");
        }
    }
}
