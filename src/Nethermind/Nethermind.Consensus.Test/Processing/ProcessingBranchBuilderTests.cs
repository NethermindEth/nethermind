// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
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

    private static IStateReader StateOnlyFor(Block? block = null)
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(false);
        if (block is not null)
        {
            stateReader.HasStateForBlock(Arg.Is<BlockHeader?>(h => h != null && h.Hash == block.Hash)).Returns(true);
        }
        return stateReader;
    }

    private static Block ChildOf(Block parent, int nonce) =>
        Build.A.Block.WithNumber(parent.Number + 1).WithParent(parent).WithNonce((ulong)nonce).WithDifficulty(2).TestObject;
}
