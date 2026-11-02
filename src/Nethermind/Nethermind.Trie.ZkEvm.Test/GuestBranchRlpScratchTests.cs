// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Tests for the per-depth scratch the SP1 guest encodes branches into.</summary>
/// <remarks>The scratch is static, so these tests do not run in parallel.</remarks>
[NonParallelizable]
public class GuestBranchRlpScratchTests
{
    [Test]
    public void Rents_the_same_scratch_at_a_depth_until_a_longer_one_replaces_it()
    {
        TreePath path = TreePath.FromNibble([1, 2, 3]);

        CappedArray<byte> first = TrieNode.RentBranchRlpScratch(100, path);
        CappedArray<byte> second = TrieNode.RentBranchRlpScratch(200, path);
        CappedArray<byte> longer = TrieNode.RentBranchRlpScratch(600, path);
        CappedArray<byte> afterLonger = TrieNode.RentBranchRlpScratch(100, path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.UnderlyingArray, Is.SameAs(first.UnderlyingArray));
            Assert.That(longer.UnderlyingArray, Is.Not.SameAs(first.UnderlyingArray));
            Assert.That(longer.Length, Is.EqualTo(600));
            Assert.That(afterLonger.UnderlyingArray, Is.SameAs(longer.UnderlyingArray));
        }
    }
}
