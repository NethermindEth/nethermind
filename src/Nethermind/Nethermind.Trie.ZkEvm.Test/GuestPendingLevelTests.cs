// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Tests for the check that lets the guest's trie write stop climbing at a level an earlier write left pending.</summary>
/// <remarks>Stopping is only sound where updating the parent would leave its slot and hash as they are.</remarks>
[Parallelizable(ParallelScope.All)]
public class GuestPendingLevelTests
{
    private const int Slot = 3;

    [Test]
    public void Dirty_unhashed_parent_holding_the_unhashed_child_is_pending()
    {
        (TrieNode parent, TrieNode child) = PendingPair();

        Assert.That(parent.IsPendingWith(Slot, child), Is.True);
    }

    [Test]
    public void Parent_not_holding_the_child_in_that_slot_is_not_pending([Values(Slot - 1, Slot + 1)] int slot)
    {
        (TrieNode parent, TrieNode child) = PendingPair();

        Assert.That(parent.IsPendingWith(slot, child), Is.False);
    }

    [Test]
    public void Parent_holding_another_node_is_not_pending()
    {
        (TrieNode parent, _) = PendingPair();

        Assert.That(parent.IsPendingWith(Slot, Leaf()), Is.False);
    }

    [Test]
    public void Hashed_parent_or_child_is_not_pending([Values] bool parentHashed)
    {
        (TrieNode parent, TrieNode child) = PendingPair();
        (parentHashed ? parent : child).Keccak = new Hash256(new byte[Hash256.Size]);

        Assert.That(parent.IsPendingWith(Slot, child), Is.False);
    }

    [Test]
    public void Sealed_parent_is_not_pending()
    {
        (TrieNode parent, TrieNode child) = PendingPair();
        parent.Seal();

        Assert.That(parent.IsPendingWith(Slot, child), Is.False);
    }

    private static (TrieNode Parent, TrieNode Child) PendingPair()
    {
        TrieNode parent = TrieNodeFactory.CreateBranch();
        TrieNode child = Leaf();
        parent.SetChild(Slot, child);
        return (parent, child);
    }

    private static TrieNode Leaf() => TrieNodeFactory.CreateLeaf([1, 2, 3], new CappedArray<byte>([0x42]));
}
