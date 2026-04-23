// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeFinalizationManagerTests
{
    private static (IManualBlockFinalizationManager Manual, IAuRaBlockFinalizationManager AuRa, IPoSSwitcher PoS, IBlockTree BlockTree) CreateMocks() =>
        (Substitute.For<IManualBlockFinalizationManager>(),
         Substitute.For<IAuRaBlockFinalizationManager>(),
         Substitute.For<IPoSSwitcher>(),
         Substitute.For<IBlockTree>());

    [Test]
    public void SetMainBlockBranchProcessor_forwards_when_head_is_null()
    {
        (IManualBlockFinalizationManager manual, IAuRaBlockFinalizationManager aura, IPoSSwitcher posSwitcher, IBlockTree blockTree) = CreateMocks();
        blockTree.Head.Returns((Block?)null);
        using AuRaMergeFinalizationManager wrapper = new(manual, aura, posSwitcher, blockTree);

        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        wrapper.SetMainBlockBranchProcessor(branchProcessor);

        aura.Received(1).SetMainBlockBranchProcessor(branchProcessor);
    }

    [Test]
    public void SetMainBlockBranchProcessor_forwards_when_head_is_pre_merge()
    {
        (IManualBlockFinalizationManager manual, IAuRaBlockFinalizationManager aura, IPoSSwitcher posSwitcher, IBlockTree blockTree) = CreateMocks();
        Block head = Build.A.Block.WithNumber(1_000).TestObject;
        blockTree.Head.Returns(head);
        posSwitcher.IsPostMerge(head.Header).Returns(false);
        using AuRaMergeFinalizationManager wrapper = new(manual, aura, posSwitcher, blockTree);

        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        wrapper.SetMainBlockBranchProcessor(branchProcessor);

        aura.Received(1).SetMainBlockBranchProcessor(branchProcessor);
    }

    [Test]
    public void SetMainBlockBranchProcessor_skips_when_head_is_post_merge()
    {
        (IManualBlockFinalizationManager manual, IAuRaBlockFinalizationManager aura, IPoSSwitcher posSwitcher, IBlockTree blockTree) = CreateMocks();
        Block head = Build.A.Block.WithNumber(30_000_000).TestObject;
        blockTree.Head.Returns(head);
        posSwitcher.IsPostMerge(head.Header).Returns(true);
        using AuRaMergeFinalizationManager wrapper = new(manual, aura, posSwitcher, blockTree);

        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        wrapper.SetMainBlockBranchProcessor(branchProcessor);

        aura.DidNotReceive().SetMainBlockBranchProcessor(Arg.Any<IBranchProcessor>());
    }

    [Test]
    public void SetMainBlockBranchProcessor_forwards_when_HasEverReachedTerminalBlock_but_head_is_genesis()
    {
        // Regression: HasEverReachedTerminalBlock() is true on fresh archive DB with FTD in config,
        // but head is still genesis — must not skip forwarding in that case.
        (IManualBlockFinalizationManager manual, IAuRaBlockFinalizationManager aura, IPoSSwitcher posSwitcher, IBlockTree blockTree) = CreateMocks();
        Block genesis = Build.A.Block.Genesis.TestObject;
        blockTree.Head.Returns(genesis);
        posSwitcher.HasEverReachedTerminalBlock().Returns(true);
        posSwitcher.IsPostMerge(genesis.Header).Returns(false);
        using AuRaMergeFinalizationManager wrapper = new(manual, aura, posSwitcher, blockTree);

        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        wrapper.SetMainBlockBranchProcessor(branchProcessor);

        aura.Received(1).SetMainBlockBranchProcessor(branchProcessor);
    }
}
