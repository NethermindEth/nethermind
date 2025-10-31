// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class BlockAncestorTrackerTests
{
    [Test]
    public void Can_Track_Past_N()
    {
        int chainLength = 512;
        int depth = 256;
        int lastNBlock = 128;

        IBlockTree tree = Build.A.BlockTree()
            .OfChainLength(chainLength)
            .TestObject;

        using BlockAncestorTracker tracker = new BlockAncestorTracker(tree, maxDepth: depth, keptBlocks: lastNBlock);

        for (int blockNumber = chainLength - lastNBlock; blockNumber < chainLength; blockNumber++)
        {
            BlockHeader currentBlock = tree.FindHeader(blockNumber)!;
            for (int i = 0; i < depth; i++)
            {
                Assert.That(tracker.GetAncestor(currentBlock.Hash!, i), Is.EqualTo(tree.FindHeader(blockNumber - 1 - i)?.Hash!));
            }
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Can_Track_NewBlock(bool ingestSynchronously)
    {
        int chainLength = 512;
        int depth = 256;
        int lastNBlock = 128;

        IBlockTree tree = Build.A.BlockTree()
            .OfChainLength(chainLength)
            .TestObject;

        using BlockAncestorTracker tracker = new BlockAncestorTracker(
            tree,
            maxDepth: depth,
            keptBlocks: lastNBlock,
            ingestSynchronously: ingestSynchronously);

        BlockHeader lastBlock = tree.FindHeader(chainLength - 1)!;
        Block newBlock = Build.A.Block.WithParent(lastBlock).TestObject;

        Assert.That(tracker.GetAncestor(tree.FindHeader(chainLength - lastNBlock - 1)?.Hash!, 0),
            Is.Not.Null, "In last block should be in range");

        // Add block
        tree.SuggestBlock(newBlock);
        tree.UpdateMainChain(newBlock);

        for (int i = 0; i < depth; i++)
        {
            Assert.That(tracker.GetAncestor(newBlock.Hash!, i),
                Is.EqualTo(tree.FindHeader(chainLength - i - 1)?.Hash!));
        }

        Assert.That(() => tracker.GetAncestor(tree.FindHeader(chainLength - lastNBlock - 1)?.Hash!, 0),
            Is.Null.After(1000, 10), "Very old header will get removed");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Can_HandleReorg(bool ingestSynchronously)
    {
        int chainLength = 512;
        int depth = 256;
        int lastNBlock = 128;
        int reorgDepth = 64;

        IBlockTree tree = Build.A.BlockTree()
            .OfChainLength(chainLength)
            .TestObject;

        using BlockAncestorTracker tracker = new BlockAncestorTracker(
            tree,
            maxDepth: depth,
            keptBlocks: lastNBlock,
            ingestSynchronously: ingestSynchronously);

        Hash256[] originalBranch = Enumerable.Range(chainLength - lastNBlock, lastNBlock)
            .Select((blockNumber) => tree.FindHeader(blockNumber, BlockTreeLookupOptions.None)?.Hash!)
            .ToArray();
        BlockHeader originalBranchHead = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;

        BlockHeader branch = tree.FindHeader(chainLength - reorgDepth - 1, BlockTreeLookupOptions.None)!;
        for (int i = 0; i < reorgDepth; i++)
        {
            Block newBlock = Build.A.Block.WithParent(branch).WithNonce((ulong)(i + 10000)).TestObject;
            tree.SuggestBlock(newBlock);
            tree.UpdateMainChain(newBlock);
            branch = newBlock.Header;
        }

        Hash256[] newBranch = Enumerable.Range(chainLength - lastNBlock, lastNBlock)
            .Select((blockNumber) => tree.FindHeader(blockNumber, BlockTreeLookupOptions.None)?.Hash!)
            .ToArray();
        BlockHeader newBranchHead = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Assert.That(newBranch, Is.Not.EquivalentTo(originalBranch));

        for (int i = 0; i < lastNBlock - 1; i++)
        {
            Hash256 blockHashAnswer = tracker.GetAncestor(newBranchHead.Hash!, i)!;
            Assert.That(blockHashAnswer, Is.EqualTo(newBranch[^(i+2)]));

            Hash256 newBranchAnswer = tracker.GetAncestor(originalBranchHead!.Hash!, i)!;
            Assert.That(newBranchAnswer, Is.EqualTo(originalBranch[^(i+2)]));

            if (i >= reorgDepth - 1)
            {
                Assert.That(blockHashAnswer, Is.EqualTo(newBranchAnswer));
            }
            else
            {
                Assert.That(blockHashAnswer, Is.Not.EqualTo(newBranchAnswer));
            }
        }
    }
}
