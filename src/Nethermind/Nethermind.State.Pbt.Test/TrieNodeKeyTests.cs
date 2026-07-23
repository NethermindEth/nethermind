// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

using Layout = Nethermind.Pbt.PbtClusteredTileLayout;

namespace Nethermind.State.Pbt.Test;

public class TrieNodeKeyTests
{
    /// <summary>A few ascending child slots per level: enough to interleave subtrees without a huge tree.</summary>
    private static readonly int[] Slots = [0, 5, 15];

    private const int Levels = 3;

    [Test]
    public void DbKey_PutsThePathFirstAndTheDepthLast()
    {
        Stem stem = new(Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd"));
        TrieNodeKey key = TrieNodeKey.For(2 * Layout.LevelsPerGroup, stem);

        byte[] dbKey = key.ToDbKey();

        Assert.Multiple(() =>
        {
            Assert.That(dbKey.AsSpan(0, Stem.Length).SequenceEqual(key.Path.Bytes), Is.True, "the path leads");
            Assert.That(dbKey[Stem.Length], Is.EqualTo(key.Depth), "the depth trails");
        });
    }

    /// <summary>
    /// Byte order over the keys is exactly a pre-order descent of the trie, which is the property the
    /// trailing depth exists for: a node sorts immediately before its own subtree, and no foreign key
    /// falls inside that subtree's range.
    /// </summary>
    [Test]
    public void DbKeys_SortAsAPreOrderDescent()
    {
        List<byte[]> preOrder = [];
        Descend(TrieNodeKey.Root, Levels, preOrder);

        byte[][] sorted = preOrder.ToArray();
        Array.Sort(sorted, static (a, b) => a.AsSpan().SequenceCompareTo(b));

        Assert.That(sorted, Is.EqualTo(preOrder));
    }

    private static void Descend(in TrieNodeKey node, int remainingLevels, List<byte[]> into)
    {
        into.Add(node.ToDbKey());
        if (remainingLevels == 0) return;

        foreach (int slot in Slots) Descend(node.ChildGroup(slot, Layout.LevelsPerGroup), remainingLevels - 1, into);
    }
}
