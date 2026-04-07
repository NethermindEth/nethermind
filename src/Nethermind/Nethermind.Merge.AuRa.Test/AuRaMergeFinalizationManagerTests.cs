// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
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
    private IBranchProcessor _branchProcessor;

    [SetUp]
    public void Setup()
    {
        _manualFinalizationManager = Substitute.For<IManualBlockFinalizationManager>();
        _auRaFinalizationManager = Substitute.For<IAuRaBlockFinalizationManager>();
        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        _branchProcessor = Substitute.For<IBranchProcessor>();
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

    [Test]
    public void Post_merge_blocks_do_not_trigger_aura_finalization()
    {
        _poSSwitcher.HasEverReachedTerminalBlock().Returns(false);

        AuRaMergeFinalizationManager manager = new(_manualFinalizationManager, _auRaFinalizationManager, _poSSwitcher);
        manager.SetMainBlockBranchProcessor(_branchProcessor);

        // Trigger merge transition
        _poSSwitcher.TerminalBlockReached += Raise.Event();
        _auRaFinalizationManager.ClearReceivedCalls();

        // Fire a block processed event — should NOT reach AuRa finalization
        int blocksFinalized = 0;
        manager.BlocksFinalized += (_, _) => blocksFinalized++;

        _branchProcessor.BlockProcessed += Raise.EventWith(
            new BlockProcessedEventArgs(Nethermind.Core.Test.Builders.Build.A.Block.TestObject, Array.Empty<TxReceipt>()));

        Assert.That(blocksFinalized, Is.Zero,
            "Post-merge BlockProcessed should not trigger AuRa finalization");
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
