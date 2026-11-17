// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

[Parallelizable(ParallelScope.All)]
public class ProcessingBranchBuilderTests
{
    [Test]
    public void Throws_when_no_ancestor_down_to_genesis_has_state()
    {
        IBlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        Block top = blockTree.FindBlock(2, BlockTreeLookupOptions.None)!;

        ProcessingBranchBuilder builder = new(blockTree, StateOnlyFor(), [], LimboLogs.Instance.GetClassLogger<ProcessingBranchBuilder>());

        Assert.Throws<InvalidOperationException>(() => builder.PrepareProcessingBranch(top, ProcessingOptions.None));
    }

    [Test]
    public void Genesis_alone_still_processes_without_a_base_state()
    {
        IBlockTree blockTree = Build.A.BlockTree().OfChainLength(1).TestObject;
        Block genesis = blockTree.FindBlock(0, BlockTreeLookupOptions.None)!;

        ProcessingBranchBuilder builder = new(blockTree, StateOnlyFor(), [], LimboLogs.Instance.GetClassLogger<ProcessingBranchBuilder>());

        using ProcessingBranch branch = builder.PrepareProcessingBranch(genesis, ProcessingOptions.None);
        Assert.That(branch.BaseBlock, Is.Null);
    }

    // After a restart only the persisted block (3) has state. A side branch that forked below it (at 1)
    // and rose above it never passes through it, so its walk reaches genesis; the canonical one stops at 3.
    [Test]
    public void Side_branch_forked_below_the_persisted_block_throws_while_the_canonical_branch_processes()
    {
        IBlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;
        Block genesis = Build.A.Block.Genesis.TestObject;
        genesis.Header.TotalDifficulty = genesis.Header.Difficulty;
        blockTree.SuggestBlock(genesis);
        blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, preloadedBlocks: [genesis]);

        Block canonical1 = ChildOf(genesis, nonce: 1);
        Block canonical2 = ChildOf(canonical1, nonce: 2);
        Block persisted = ChildOf(canonical2, nonce: 3);
        Block canonical4 = ChildOf(persisted, nonce: 4);
        foreach (Block block in new[] { canonical1, canonical2, persisted, canonical4 })
        {
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, preloadedBlocks: [block]);
        }

        Block side2 = ChildOf(canonical1, nonce: 102);
        Block side3 = ChildOf(side2, nonce: 103);
        Block side4 = ChildOf(side3, nonce: 104);
        foreach (Block block in new[] { side2, side3, side4 })
        {
            blockTree.SuggestBlock(block, BlockTreeSuggestOptions.None);
        }

        ProcessingBranchBuilder builder = new(blockTree, StateOnlyFor(persisted), [], LimboLogs.Instance.GetClassLogger<ProcessingBranchBuilder>());

        using (ProcessingBranch canonical = builder.PrepareProcessingBranch(canonical4, ProcessingOptions.None))
        {
            Assert.That(canonical.BaseBlock?.Hash, Is.EqualTo(persisted.Hash));
        }
        Assert.Throws<InvalidOperationException>(() => builder.PrepareProcessingBranch(side4, ProcessingOptions.None));
    }

    [Test]
    public void Side_ancestor_is_a_base_only_if_it_was_processed([Values] bool sideWasProcessed)
    {
        (IBlockTree blockTree, Block fork, Block head, Block side2, Block side3) = BuildSideBranch();
        if (sideWasProcessed)
        {
            blockTree.MarkChainAsProcessed([side2]);
        }

        ProcessingBranchBuilder builder = new(blockTree, StateOnlyFor(fork, head, side2), [], LimboLogs.Instance.GetClassLogger<ProcessingBranchBuilder>());

        using ProcessingBranch branch = builder.PrepareProcessingBranch(side3, ProcessingOptions.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.IsMainChain(side2.Header), Is.False);
            Assert.That(branch.BaseBlock?.Hash, Is.EqualTo(sideWasProcessed ? side2.Hash : fork.Hash));
            Assert.That(branch.Blocks.Count, Is.EqualTo(sideWasProcessed ? 1 : 2));
        }
    }

    [Test]
    public void Side_ancestor_with_state_is_a_base_only_if_it_is_the_sync_pivot([Values("fork", "side", "head")] string pivot)
    {
        (IBlockTree blockTree, Block fork, Block head, Block side2, Block side3) = BuildSideBranch();
        Block pivotBlock = pivot switch { "fork" => fork, "side" => side2, _ => head };
        blockTree.SyncPivot = (pivotBlock.Number, pivotBlock.Hash!);
        bool notPivot = pivotBlock != side2;

        ProcessingBranchBuilder builder = new(blockTree, StateOnlyFor(fork, head, side2), [], LimboLogs.Instance.GetClassLogger<ProcessingBranchBuilder>());

        using ProcessingBranch branch = builder.PrepareProcessingBranch(side3, ProcessingOptions.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(branch.BaseBlock?.Hash, Is.EqualTo(notPivot ? fork.Hash : side2.Hash));
            Assert.That(branch.Blocks.Count, Is.EqualTo(notPivot ? 2 : 1));
        }
    }

    [Test]
    public void Processed_side_ancestor_without_state_is_processed_again()
    {
        (IBlockTree blockTree, Block fork, Block head, Block side2, Block side3) = BuildSideBranch();
        blockTree.MarkChainAsProcessed([side2]);

        ProcessingBranchBuilder builder = new(blockTree, StateOnlyFor(fork, head), [], LimboLogs.Instance.GetClassLogger<ProcessingBranchBuilder>());

        using ProcessingBranch branch = builder.PrepareProcessingBranch(side3, ProcessingOptions.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(branch.BaseBlock?.Hash, Is.EqualTo(fork.Hash));
            Assert.That(branch.Blocks.Count, Is.EqualTo(2));
        }
    }

    [Test]
    public void Main_chain_ancestor_with_state_is_the_base()
    {
        (IBlockTree blockTree, Block fork, Block head, _, _) = BuildSideBranch();
        Block next = ChildOf(head, nonce: 3);
        blockTree.SuggestBlock(next);

        ProcessingBranchBuilder builder = new(blockTree, StateOnlyFor(fork, head), [], LimboLogs.Instance.GetClassLogger<ProcessingBranchBuilder>());

        using ProcessingBranch branch = builder.PrepareProcessingBranch(next, ProcessingOptions.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(branch.BaseBlock?.Hash, Is.EqualTo(head.Hash));
            Assert.That(branch.Blocks.Count, Is.EqualTo(1));
        }
    }

    private static (IBlockTree BlockTree, Block Fork, Block Head, Block Side2, Block Side3) BuildSideBranch()
    {
        IBlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;
        Block genesis = Build.A.Block.Genesis.TestObject;
        genesis.Header.TotalDifficulty = genesis.Header.Difficulty;
        blockTree.SuggestBlock(genesis);
        blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, preloadedBlocks: [genesis]);

        Block fork = ChildOf(genesis, nonce: 1);
        Block head = ChildOf(fork, nonce: 2);
        foreach (Block block in new[] { fork, head })
        {
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, preloadedBlocks: [block]);
        }

        Block side2 = ChildOf(fork, nonce: 102, difficulty: 1);
        Block side3 = ChildOf(side2, nonce: 103);
        blockTree.SuggestBlock(side2);
        blockTree.SuggestBlock(side3);
        return (blockTree, fork, head, side2, side3);
    }

    [Test]
    public void First_block_parent_is_taken_from_the_branch_base()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader parent = Build.A.BlockHeader.WithNumber(5).TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(parent).Returns(true);
        ProcessingBranchBuilder builder = new(blockTree, stateReader, [], LimboLogs.Instance.GetClassLogger<ProcessingBranchBuilder>());
        using ProcessingBranch branch = new(parent, new ArrayPoolList<Block>(1) { block });

        builder.PrepareBlocksToProcess(block, ProcessingOptions.None, branch, CancellationToken.None);

        stateReader.Received(1).HasStateForBlock(parent);
    }

    private static IStateReader StateOnlyFor(params Block[] blocks)
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(false);
        foreach (Block block in blocks)
        {
            stateReader.HasStateForBlock(Arg.Is<BlockHeader?>(h => h != null && h.Hash == block.Hash)).Returns(true);
        }
        return stateReader;
    }

    private static Block ChildOf(Block parent, int nonce, ulong difficulty = 2) =>
        Build.A.Block.WithNumber(parent.Number + 1).WithParent(parent).WithNonce((ulong)nonce).WithDifficulty(difficulty).TestObject;
}
