// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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

[Parallelizable(ParallelScope.Self)]
public class AuRaMergeFinalizationManagerTests
{
    private IManualBlockFinalizationManager _manualFinalizationManager;
    private IAuRaBlockFinalizationManager _auRaFinalizationManager;
    private IPoSSwitcher _poSSwitcher;
    private IBlockTree _blockTree;

    [SetUp]
    public void Setup()
    {
        _manualFinalizationManager = Substitute.For<IManualBlockFinalizationManager>();
        _auRaFinalizationManager = Substitute.For<IAuRaBlockFinalizationManager>();
        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        _blockTree = Substitute.For<IBlockTree>();
    }

    [TearDown]
    public void TearDown()
    {
        _manualFinalizationManager?.Dispose();
        _auRaFinalizationManager?.Dispose();
    }

    private void SetHead(bool postMerge)
    {
        Block head = Build.A.Block.WithNumber(postMerge ? 30_000_000 : 1_000).TestObject;
        _blockTree.Head.Returns(head);
        _poSSwitcher.IsPostMerge(head.Header).Returns(postMerge);
    }

    [TestCase(true, Description = "Already post-merge at startup")]
    [TestCase(false, Description = "Merge transition at runtime")]
    public void Disposes_aura_manager_on_merge(bool alreadyPostMerge)
    {
        SetHead(alreadyPostMerge);

        AuRaMergeFinalizationManager _ = new(_manualFinalizationManager, _auRaFinalizationManager, _poSSwitcher, _blockTree);

        if (!alreadyPostMerge)
        {
            _auRaFinalizationManager.DidNotReceive().Dispose();
            _poSSwitcher.TerminalBlockReached += Raise.Event();
        }

        _auRaFinalizationManager.Received(1).Dispose();
    }

    [TestCase(true, 0, Description = "Post-merge: skipped so the inner never subscribes or walks")]
    [TestCase(false, 1, Description = "Pre-merge: forwarded so the inner initializes normally")]
    public void SetMainBlockBranchProcessor_forwards_only_pre_merge(bool alreadyPostMerge, int expectedForwards)
    {
        SetHead(alreadyPostMerge);
        AuRaMergeFinalizationManager wrapper = new(_manualFinalizationManager, _auRaFinalizationManager, _poSSwitcher, _blockTree);

        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        wrapper.SetMainBlockBranchProcessor(branchProcessor);

        _auRaFinalizationManager.Received(expectedForwards).SetMainBlockBranchProcessor(branchProcessor);
    }

    [Test]
    public void Terminal_block_handler_unsubscribes_itself()
    {
        SetHead(postMerge: false);

        AuRaMergeFinalizationManager _ = new(_manualFinalizationManager, _auRaFinalizationManager, _poSSwitcher, _blockTree);

        // First raise disposes and unsubscribes
        _poSSwitcher.TerminalBlockReached += Raise.Event();
        _auRaFinalizationManager.Received(1).Dispose();

        _auRaFinalizationManager.ClearReceivedCalls();

        // Second raise should be a no-op — handler was unsubscribed
        _poSSwitcher.TerminalBlockReached += Raise.Event();
        _auRaFinalizationManager.DidNotReceive().Dispose();
    }

    [Test]
    public void Fresh_archive_with_FinalTotalDifficulty_in_config_still_wires_pre_merge_finalization()
    {
        // Regression: HasEverReachedTerminalBlock() is true on fresh archive DB with FTD in config,
        // but head is still genesis — must not skip or dispose in that case.
        Block genesis = Build.A.Block.Genesis.TestObject;
        _blockTree.Head.Returns(genesis);
        _poSSwitcher.HasEverReachedTerminalBlock().Returns(true);
        _poSSwitcher.IsPostMerge(genesis.Header).Returns(false);

        AuRaMergeFinalizationManager wrapper = new(_manualFinalizationManager, _auRaFinalizationManager, _poSSwitcher, _blockTree);

        _auRaFinalizationManager.DidNotReceive().Dispose();

        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        wrapper.SetMainBlockBranchProcessor(branchProcessor);
        _auRaFinalizationManager.Received(1).SetMainBlockBranchProcessor(branchProcessor);
    }
}
