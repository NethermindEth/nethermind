// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtBlockTreeChildHeaderSourceTests
{
    /// <summary>Resolves a pending block by parent hash when no canonical block exists at its height.</summary>
    [Test]
    public void TryFindChild_MatchesOnTheParent_AcrossForkSiblingsAndBeforeCanonicalisation()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().WithoutSettingHead;
        BlockTree blockTree = builder.TestObject;

        Block genesis = Build.A.Block.Genesis.TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, preloadedBlocks: [genesis]);

        // Neither fork branch is canonical, so lookup by height cannot resolve either block.
        Block branchA = Suggest(blockTree, genesis, 1);
        Block branchB = Suggest(blockTree, genesis, 2);
        Block onA = Suggest(blockTree, branchA, 3);
        Block onB = Suggest(blockTree, branchB, 4);

        PbtBlockTreeChildHeaderSource source = new(blockTree, builder.ChainLevelInfoRepository);

        Assert.That(source.TryFindChild(branchA.Header)?.Hash, Is.EqualTo(onA.Hash));
        Assert.That(source.TryFindChild(branchB.Header)?.Hash, Is.EqualTo(onB.Hash), "the sibling at the same height is not mistaken for it");
        Assert.That(source.TryFindChild(onA.Header), Is.Null, "and a block nothing was suggested onto resolves to nothing");
    }

    /// <remarks>Distinct difficulties ensure the fork siblings have distinct hashes.</remarks>
    private static Block Suggest(BlockTree blockTree, Block parent, long difficulty)
    {
        Block block = Build.A.Block.WithParent(parent).WithDifficulty((ulong)difficulty).TestObject;
        blockTree.SuggestBlock(block);
        return block;
    }
}
