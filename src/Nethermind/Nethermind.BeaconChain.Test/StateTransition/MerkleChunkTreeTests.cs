// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.Test.StateTransition;

public class MerkleChunkTreeTests
{
    [Test]
    public void Random_grow_shrink_and_updates_match_full_merkleization([Values(17, 12345)] int seed)
    {
        Random random = new(seed);
        MerkleChunkTree tree = new(8);
        UInt256[] expectedLeaves = [];
        for (int iteration = 0; iteration < 100; iteration++)
        {
            int previousCount = expectedLeaves.Length;
            int count = random.Next(0, 200);
            Array.Resize(ref expectedLeaves, count);
            UInt256[] leaves = tree.SetLeafCount(count);
            using IDisposable assertions = Assert.EnterMultipleScope();
            Assert.That(leaves.AsSpan(0, count).ToArray(), Is.EqualTo(expectedLeaves), $"preserved leaves at iteration {iteration}");
            for (int i = 0; i < count; i++)
                leaves[i] = expectedLeaves[i] = (UInt256)(ulong)random.Next(0, 1000);
            if (count < previousCount || iteration % 2 == 0)
                tree.Rebuild();
            else
                tree.Update(Enumerable.Range(0, count).ToArray(), count);
            Merkle.Merkleize(out UInt256 expected, expectedLeaves, 256);
            Assert.That(tree.Root, Is.EqualTo(expected), $"root at iteration {iteration}, count {count}");
        }
    }

    [Test]
    public void Small_appends_reuse_storage_and_hash_only_logical_leaves([Values(63, 64, 65)] int initialCount)
    {
        MerkleChunkTree tree = new(10);
        UInt256[] leaves = tree.SetLeafCount(initialCount);
        for (int i = 0; i < initialCount; i++) leaves[i] = (UInt256)(ulong)(i + 1);
        tree.Rebuild();
        leaves = tree.SetLeafCount(initialCount + 1);
        leaves[initialCount] = (UInt256)123UL;
        tree.Update([initialCount], 1);
        Merkle.Merkleize(out UInt256 expected, leaves.AsSpan(0, initialCount + 1), 1024);
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(tree.Root, Is.EqualTo(expected));
        Assert.That(tree.SetLeafCount(initialCount + 2), Is.SameAs(leaves));
        Assert.That(leaves.Length, Is.LessThan(initialCount * 2));
    }

    [Test]
    public void Shrink_then_regrow_clears_removed_leaves([Values(0, 1, 5)] int retained)
    {
        MerkleChunkTree tree = new(5);
        UInt256[] leaves = tree.SetLeafCount(17);
        leaves.AsSpan(0, 17).Fill((UInt256)7UL);
        tree.Rebuild();
        tree.SetLeafCount(retained);
        tree.Rebuild();
        leaves = tree.SetLeafCount(17);
        tree.Rebuild();
        UInt256[] expectedLeaves = new UInt256[17];
        expectedLeaves.AsSpan(0, retained).Fill((UInt256)7UL);
        Merkle.Merkleize(out UInt256 expected, expectedLeaves, 32);
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(leaves.AsSpan(0, 17).ToArray(), Is.EqualTo(expectedLeaves));
        Assert.That(tree.Root, Is.EqualTo(expected));
    }
}
