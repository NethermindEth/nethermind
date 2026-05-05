// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
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

    [Test]
    public void HasStateRoot_TracksLastN_OnConstruction()
    {
        BlockTree tree = Build.A.BlockTree().WithBlocks(BuildChain(20).ToArray()).TestObject;
        using FlatStateRootIndex index = new(tree, lastN: 10);

        for (int i = 0; i < 30; i++)
        {
            index.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .Should().Be(i is >= 10 and < 20, $"state root index {i}");
        }
    }

    [Test]
    public void TryGetStateId_ReturnsStateIdForKnownRoot()
    {
        List<Block> blocks = BuildChain(5);
        BlockTree tree = Build.A.BlockTree().WithBlocks(blocks.ToArray()).TestObject;
        using FlatStateRootIndex index = new(tree, lastN: 10);

        Hash256 root = blocks[^1].StateRoot!;
        index.TryGetStateId(root, out StateId stateId).Should().BeTrue();
        stateId.StateRoot.Should().Be(root);
        stateId.BlockNumber.Should().Be(blocks[^1].Number);
    }

    [Test]
    public void TryGetStateId_ReturnsFalseForUnknownRoot()
    {
        BlockTree tree = Build.A.BlockTree().WithBlocks(BuildChain(5).ToArray()).TestObject;
        using FlatStateRootIndex index = new(tree, lastN: 10);

        index.TryGetStateId(Keccak.Compute(Bytes.FromHexString("deadbeef")), out _).Should().BeFalse();
    }

    [Test]
    public void NewHeadAppendingToTip_KeepsQueueIntact_AndEvictsOldestBeyondLastN()
    {
        // Build a base chain of 20 blocks, init tracker with lastN=10 (covers blocks 10..19).
        List<Block> blocks = BuildChain(20);
        BlockTree tree = Build.A.BlockTree().WithBlocks(blocks.ToArray()).TestObject;
        using FlatStateRootIndex index = new(tree, lastN: 10);

        // Append a new block whose parent is the current head — exercises the "queue intact" branch.
        Block next = Build.A.Block
            .WithParent(blocks[^1])
            .WithStateRoot(Keccak.Compute(20.ToBigEndianByteArray()))
            .TestObject;
        tree.SuggestBlock(next);
        tree.UpdateMainChain(new[] { next }, true);

        // Window has shifted: now covers indexes 11..20.
        for (int i = 0; i < 30; i++)
        {
            index.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .Should().Be(i is >= 11 and < 21, $"state root index {i}");
        }
    }

    [Test]
    public void NewHeadOnReorg_RebuildsQueueFromAncestors()
    {
        // Base chain: 20 blocks. Reorg by suggesting a new block with parent at height 15.
        List<Block> blocks = BuildChain(20);
        BlockTree tree = Build.A.BlockTree().WithBlocks(blocks.ToArray()).TestObject;
        using FlatStateRootIndex index = new(tree, lastN: 10);

        Block reorgBlock = Build.A.Block
            .WithParent(tree.FindBlock(15, BlockTreeLookupOptions.All)!)
            .WithStateRoot(Keccak.Compute(100.ToBigEndianByteArray()))
            .TestObject;
        tree.SuggestBlock(reorgBlock);
        tree.UpdateMainChain(new[] { reorgBlock }, true);

        // After reorg, queue is rebuilt walking parents from the new head — covers blocks 6..14 plus the reorg root.
        for (int i = 0; i < 30; i++)
        {
            index.HasStateRoot(Keccak.Compute(i.ToBigEndianByteArray()))
                .Should().Be(i is >= 6 and < 15, $"state root index {i}");
        }
        index.HasStateRoot(Keccak.Compute(100.ToBigEndianByteArray())).Should().BeTrue();
    }

    [Test]
    public void Dispose_UnsubscribesFromBlockTreeEvent()
    {
        List<Block> blocks = BuildChain(5);
        BlockTree tree = Build.A.BlockTree().WithBlocks(blocks.ToArray()).TestObject;
        FlatStateRootIndex index = new(tree, lastN: 10);

        index.Dispose();

        // After dispose, new heads should not affect the (now-stale) index. We assert no exception is thrown
        // and that an unrelated state root added after dispose is not tracked.
        Block next = Build.A.Block
            .WithParent(blocks[^1])
            .WithStateRoot(Keccak.Compute(999.ToBigEndianByteArray()))
            .TestObject;
        tree.SuggestBlock(next);
        tree.UpdateMainChain(new[] { next }, true);

        index.HasStateRoot(Keccak.Compute(999.ToBigEndianByteArray())).Should().BeFalse();
    }
}
