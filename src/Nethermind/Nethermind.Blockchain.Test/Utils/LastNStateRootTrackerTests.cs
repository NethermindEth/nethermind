// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Utils;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Utils;

public class LastNStateRootTrackerTests
{
    [Test]
    public void Test_trackLastN()
    {
        System.Collections.Generic.List<Block> blocks = new();
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

        LastNStateRootTracker tracker = new LastNStateRootTracker(tree, 10);

        for (int i = 0; i < 30; i++)
        {
            tracker.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .Should().Be(i is >= 10 and < 20);
        }
    }

    [Test]
    public void Test_ContinueTrackingAsWeGetNewHead()
    {
        System.Collections.Generic.List<Block> blocks = new();
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

        LastNStateRootTracker tracker = new LastNStateRootTracker(tree, 10);

        currentBlock = Build.A.Block
            .WithParent(currentBlock)
            .WithStateRoot(Keccak.Compute(20.ToBigEndianByteArray()))
            .TestObject;
        tree.SuggestBlock(currentBlock);
        tree.UpdateMainChain(new[] { currentBlock }, true);

        for (int i = 0; i < 30; i++)
        {
            tracker.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .Should().Be(i is >= 11 and < 21);
        }
    }

    [Test]
    public void Test_OnReorg_RebuildSet()
    {
        System.Collections.Generic.List<Block> blocks = new();
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

        LastNStateRootTracker tracker = new LastNStateRootTracker(tree, 10);

        currentBlock = Build.A.Block
            .WithParent(tree.FindBlock(15, BlockTreeLookupOptions.All)!)
            .WithStateRoot(Keccak.Compute(100.ToBigEndianByteArray()))
            .TestObject;
        tree.SuggestBlock(currentBlock);
        tree.UpdateMainChain(new[] { currentBlock }, true);

        for (int i = 0; i < 30; i++)
        {
            tracker.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .Should().Be(i is >= 6 and < 15);
        }

        tracker.HasStateRoot(Keccak.Compute(100.ToBigEndianByteArray())).Should().BeTrue();
    }
}
