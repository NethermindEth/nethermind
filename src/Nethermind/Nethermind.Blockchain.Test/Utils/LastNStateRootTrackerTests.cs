// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Utils;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Utils;

[Parallelizable(ParallelScope.All)]
public class LastNStateRootTrackerTests
{
    [Test]
    public void Test_trackLastN()
    {
        System.Collections.Generic.List<Block> blocks = [];
        Block currentBlock = Build.A.Block.Genesis.TestObject;
        blocks.Add(currentBlock);
        for (int i = 0; i < 20; i++)
        {
            currentBlock = Build.A.Block
                .WithParent(currentBlock)
                .WithStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .TestObject;
            blocks.Add(currentBlock);
        }

        BlockTree tree = Build.A.BlockTree().WithBlocks(blocks.ToArray()).TestObject;

        LastNStateRootTracker tracker = new(tree, 10);

        for (int i = 0; i < 30; i++)
        {
            Assert.That(tracker.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray())), Is.EqualTo(i is >= 10 and < 20));
        }
    }

    [Test]
    public void Test_ContinueTrackingAsWeGetNewHead()
    {
        System.Collections.Generic.List<Block> blocks = [];
        Block currentBlock = Build.A.Block.Genesis.TestObject;
        blocks.Add(currentBlock);
        for (int i = 0; i < 20; i++)
        {
            currentBlock = Build.A.Block
                .WithParent(currentBlock)
                .WithStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .TestObject;
            blocks.Add(currentBlock);
        }

        BlockTree tree = Build.A.BlockTree().WithBlocks(blocks.ToArray()).TestObject;

        LastNStateRootTracker tracker = new(tree, 10);

        currentBlock = Build.A.Block
            .WithParent(currentBlock)
            .WithStateRoot(Keccak.Compute(20.ToBigEndianByteArray()))
            .TestObject;
        tree.SuggestBlock(currentBlock);
        tree.TryUpdateMainChain(currentBlock.Header, true, preloadedBlocks: new[] { currentBlock });

        for (int i = 0; i < 30; i++)
        {
            Assert.That(tracker.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray())), Is.EqualTo(i is >= 11 and < 21));
        }
    }

    [Test]
    public void Test_OnReorg_RebuildSet()
    {
        System.Collections.Generic.List<Block> blocks = [];
        Block currentBlock = Build.A.Block.Genesis.TestObject;
        blocks.Add(currentBlock);
        for (int i = 0; i < 20; i++)
        {
            currentBlock = Build.A.Block
                .WithParent(currentBlock)
                .WithStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .TestObject;
            blocks.Add(currentBlock);
        }

        BlockTree tree = Build.A.BlockTree().WithBlocks(blocks.ToArray()).TestObject;

        LastNStateRootTracker tracker = new(tree, 10);

        currentBlock = Build.A.Block
            .WithParent(tree.FindBlock(15, BlockTreeLookupOptions.All)!)
            .WithStateRoot(Keccak.Compute(100.ToBigEndianByteArray()))
            .TestObject;
        tree.SuggestBlock(currentBlock);
        tree.TryUpdateMainChain(currentBlock.Header, true, preloadedBlocks: new[] { currentBlock });

        for (int i = 0; i < 30; i++)
        {
            Assert.That(tracker.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray())), Is.EqualTo(i is >= 6 and < 15));
        }

        Assert.That(tracker.HasStateRoot(Keccak.Compute(100.ToBigEndianByteArray())), Is.True);
    }

    [Test]
    public void Test_TrackLastN_WithCustomDepth()
    {
        System.Collections.Generic.List<Block> blocks = [];
        Block currentBlock = Build.A.Block.Genesis.TestObject;
        blocks.Add(currentBlock);
        for (int i = 0; i < 300; i++)
        {
            currentBlock = Build.A.Block
                .WithParent(currentBlock)
                .WithStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .TestObject;
            blocks.Add(currentBlock);
        }

        BlockTree tree = Build.A.BlockTree().WithBlocks(blocks.ToArray()).TestObject;

        // Test with a custom depth of 256 blocks (useful for networks with fast block times like Arbitrum)
        LastNStateRootTracker tracker = new(tree, 256);

        for (int i = 0; i < 320; i++)
        {
            Assert.That(tracker.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray())), Is.EqualTo(i is >= 44 and < 300));
        }
    }
}
