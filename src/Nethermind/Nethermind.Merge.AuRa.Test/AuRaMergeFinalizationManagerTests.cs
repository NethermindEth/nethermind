// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Processing;
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

    [SetUp]
    public void Setup()
    {
        _manualFinalizationManager = Substitute.For<IManualBlockFinalizationManager>();
        _auRaFinalizationManager = Substitute.For<IAuRaBlockFinalizationManager>();
        _poSSwitcher = Substitute.For<IPoSSwitcher>();
    }

    [TearDown]
    public void TearDown()
    {
        _manualFinalizationManager?.Dispose();
        _auRaFinalizationManager?.Dispose();
    }

    [TestCase(true, Description = "Already post-merge at startup")]
    [TestCase(false, Description = "Merge transition at runtime")]
    public void Disposes_aura_manager_on_merge(bool alreadyPostMerge)
    {
        _poSSwitcher.HasEverReachedTerminalBlock().Returns(alreadyPostMerge);

        AuRaMergeFinalizationManager _ = new(_manualFinalizationManager, _auRaFinalizationManager, _poSSwitcher);

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
        _poSSwitcher.HasEverReachedTerminalBlock().Returns(alreadyPostMerge);
        AuRaMergeFinalizationManager wrapper = new(_manualFinalizationManager, _auRaFinalizationManager, _poSSwitcher);

        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        wrapper.SetMainBlockBranchProcessor(branchProcessor);

        _auRaFinalizationManager.Received(expectedForwards).SetMainBlockBranchProcessor(branchProcessor);
    }

    [Test]
    public void Terminal_block_handler_unsubscribes_itself()
    {
        _poSSwitcher.HasEverReachedTerminalBlock().Returns(false);

        AuRaMergeFinalizationManager _ = new(_manualFinalizationManager, _auRaFinalizationManager, _poSSwitcher);

        // First raise disposes and unsubscribes
        _poSSwitcher.TerminalBlockReached += Raise.Event();
        _auRaFinalizationManager.Received(1).Dispose();

        _auRaFinalizationManager.ClearReceivedCalls();

        // Second raise should be a no-op — handler was unsubscribed
        _poSSwitcher.TerminalBlockReached += Raise.Event();
        _auRaFinalizationManager.DidNotReceive().Dispose();
    }
}
