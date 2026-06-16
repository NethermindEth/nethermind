// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.State.Flat.Sync.Snap;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync.Snap;

[TestFixture]
public class FlatStateRootIndexTests
{
    private static List<Block> BuildChain(int length)
    {
        List<Block> blocks = [Build.A.Block.Genesis.TestObject];
        Block current = blocks[0];
        for (int i = 0; i < length; i++)
        {
            current = Build.A.Block
                .WithParent(current)
                .WithStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .TestObject;
            blocks.Add(current);
        }
        return blocks;
    }

    private static (BlockTree tree, List<Block> blocks, FlatStateRootIndex index) BuildIndex(int chainLength, int lastN = 10)
    {
        List<Block> blocks = BuildChain(chainLength);
        BlockTree tree = Build.A.BlockTree().WithBlocks(blocks.ToArray()).TestObject;
        return (tree, blocks, new FlatStateRootIndex(tree, lastN));
    }

    private static void AppendBlock(BlockTree tree, Block parent, int rootSeed)
    {
        Block next = Build.A.Block
            .WithParent(parent)
            .WithStateRoot(Keccak.Compute(rootSeed.ToBigEndianByteArray()))
            .TestObject;
        tree.SuggestBlock(next);
        tree.TryUpdateMainChain(next.Header, true, preloadedBlocks: new[] { next });
    }

    [Test]
    public void HasStateRoot_TracksLastN_OnConstruction()
    {
        (_, _, FlatStateRootIndex index) = BuildIndex(20);
        using (index)
        {
            for (int i = 0; i < 30; i++)
            {
                Assert.That(index.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray())),
                    Is.EqualTo(i is >= 10 and < 20), $"state root index {i}");
            }
        }
    }

    [Test]
    public void TryGetStateId_ReturnsStateIdForKnownRoot()
    {
        (_, List<Block> blocks, FlatStateRootIndex index) = BuildIndex(5);
        using (index)
        {
            Hash256 root = blocks[^1].StateRoot!;
            Assert.That(index.TryGetStateId(root, out StateId stateId), Is.True);
            Assert.That(stateId.StateRoot, Is.EqualTo(root));
            Assert.That(stateId.BlockNumber, Is.EqualTo(blocks[^1].Number));
        }
    }

    [Test]
    public void TryGetStateId_ReturnsFalseForUnknownRoot()
    {
        (_, _, FlatStateRootIndex index) = BuildIndex(5);
        using (index)
        {
            Assert.That(index.TryGetStateId(Keccak.Compute(Bytes.FromHexString("deadbeef")), out _), Is.False);
        }
    }

    [Test]
    public void NewHeadAppendingToTip_KeepsQueueIntact_AndEvictsOldestBeyondLastN()
    {
        (BlockTree tree, List<Block> blocks, FlatStateRootIndex index) = BuildIndex(20);
        using (index)
        {
            AppendBlock(tree, blocks[^1], 20);

            for (int i = 0; i < 30; i++)
            {
                Assert.That(index.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray())),
                    Is.EqualTo(i is >= 11 and < 21), $"state root index {i}");
            }
        }
    }

    [Test]
    public void NewHeadOnReorg_RebuildsQueueFromAncestors()
    {
        (BlockTree tree, _, FlatStateRootIndex index) = BuildIndex(20);
        using (index)
        {
            AppendBlock(tree, tree.FindBlock(15, BlockTreeLookupOptions.All)!, 100);

            for (int i = 0; i < 30; i++)
            {
                Assert.That(index.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray())),
                    Is.EqualTo(i is >= 6 and < 15), $"state root index {i}");
            }
            Assert.That(index.HasStateRoot(Keccak.Compute(100.ToBigEndianByteArray())), Is.True);
        }
    }

    [Test]
    public void Dispose_UnsubscribesFromBlockTreeEvent()
    {
        (BlockTree tree, List<Block> blocks, FlatStateRootIndex index) = BuildIndex(5);
        index.Dispose();

        AppendBlock(tree, blocks[^1], 999);

        Assert.That(index.HasStateRoot(Keccak.Compute(999.ToBigEndianByteArray())), Is.False);
    }
}
