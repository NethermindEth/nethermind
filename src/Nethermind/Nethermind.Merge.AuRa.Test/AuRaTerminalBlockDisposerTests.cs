// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

[Parallelizable(ParallelScope.Self)]
public class AuRaTerminalBlockDisposerTests
{
    private IAuRaBlockFinalizationManager _auRaFinalizationManager;
    private IPoSSwitcher _poSSwitcher;
    private IBlockTree _blockTree;

    [SetUp]
    public void Setup()
    {
        _auRaFinalizationManager = Substitute.For<IAuRaBlockFinalizationManager>();
        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        _blockTree = Substitute.For<IBlockTree>();
    }

    [TearDown]
    public void TearDown() => _auRaFinalizationManager?.Dispose();

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

        AuRaTerminalBlockDisposer _ = new(_auRaFinalizationManager, _poSSwitcher, _blockTree);

        if (!alreadyPostMerge)
        {
            _auRaFinalizationManager.DidNotReceive().Dispose();
            _poSSwitcher.TerminalBlockReached += Raise.Event();
        }

        _auRaFinalizationManager.Received(1).Dispose();
    }

    [Test]
    public void Terminal_block_handler_unsubscribes_itself()
    {
        SetHead(postMerge: false);

        AuRaTerminalBlockDisposer _ = new(_auRaFinalizationManager, _poSSwitcher, _blockTree);

        _poSSwitcher.TerminalBlockReached += Raise.Event();
        _auRaFinalizationManager.Received(1).Dispose();

        _auRaFinalizationManager.ClearReceivedCalls();

        _poSSwitcher.TerminalBlockReached += Raise.Event();
        _auRaFinalizationManager.DidNotReceive().Dispose();
    }

    [Test]
    public void Fresh_archive_with_FinalTotalDifficulty_in_config_does_not_dispose_pre_merge_aura()
    {
        // Regression: HasEverReachedTerminalBlock() is true on fresh archive DB with FTD in config,
        // but head is still genesis — must not dispose in that case.
        Block genesis = Build.A.Block.Genesis.TestObject;
        _blockTree.Head.Returns(genesis);
        _poSSwitcher.HasEverReachedTerminalBlock().Returns(true);
        _poSSwitcher.IsPostMerge(genesis.Header).Returns(false);

        AuRaTerminalBlockDisposer _ = new(_auRaFinalizationManager, _poSSwitcher, _blockTree);

        _auRaFinalizationManager.DidNotReceive().Dispose();
    }
}
