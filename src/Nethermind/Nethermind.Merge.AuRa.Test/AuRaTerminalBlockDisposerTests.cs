// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

[Parallelizable(ParallelScope.Self)]
public class AuRaTerminalBlockDisposerTests
{
    private IAuRaBlockFinalizationManager _auRaFinalizationManager;
    private IPoSSwitcher _poSSwitcher;
    private IBlockTree _blockTree;
    private IMainProcessingContext _mainProcessingContext;
    private IBranchProcessor _branchProcessor;

    [SetUp]
    public void Setup()
    {
        _auRaFinalizationManager = Substitute.For<IAuRaBlockFinalizationManager>();
        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        _blockTree = Substitute.For<IBlockTree>();
        _mainProcessingContext = Substitute.For<IMainProcessingContext>();
        _branchProcessor = Substitute.For<IBranchProcessor>();
        _mainProcessingContext.BranchProcessor.Returns(_branchProcessor);
    }

    [TearDown]
    public void TearDown() => _auRaFinalizationManager?.Dispose();

    private AuRaTerminalBlockDisposer CreateDisposer() => new(
        _auRaFinalizationManager,
        _poSSwitcher,
        _blockTree,
        _mainProcessingContext);

    private void SetHead(bool postMerge)
    {
        Block head = Build.A.Block.WithNumber(postMerge ? 30_000_000UL : 1_000UL).TestObject;
        _blockTree.Head.Returns(head);
        _poSSwitcher.IsPostMerge(head.Header).Returns(postMerge);
    }

    [TestCase(true, Description = "Already post-merge at startup")]
    [TestCase(false, Description = "Merge transition at runtime")]
    public void Disposes_aura_manager_on_merge(bool alreadyPostMerge)
    {
        SetHead(alreadyPostMerge);

        AuRaTerminalBlockDisposer _ = CreateDisposer();

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

        AuRaTerminalBlockDisposer _ = CreateDisposer();

        _poSSwitcher.TerminalBlockReached += Raise.Event();
        _auRaFinalizationManager.Received(1).Dispose();

        _auRaFinalizationManager.ClearReceivedCalls();

        _poSSwitcher.TerminalBlockReached += Raise.Event();
        _auRaFinalizationManager.DidNotReceive().Dispose();
    }

    [Test]
    public void Registered_as_singleton_and_resolving_triggers_disposal_when_head_post_merge()
    {
        SetHead(postMerge: true);

        ContainerBuilder builder = new();
        builder
            .AddSingleton(_auRaFinalizationManager)
            .AddSingleton(_poSSwitcher)
            .AddSingleton(_blockTree)
            .AddSingleton(_mainProcessingContext)
            .AddSingleton<AuRaTerminalBlockDisposer>();
        using IContainer container = builder.Build();

        AuRaTerminalBlockDisposer disposer = container.Resolve<AuRaTerminalBlockDisposer>();

        Assert.That(container.Resolve<AuRaTerminalBlockDisposer>(), Is.SameAs(disposer));
        _auRaFinalizationManager.Received(1).Dispose();
    }

    [Test]
    public void Fresh_archive_with_FinalTotalDifficulty_in_config_does_not_dispose_pre_merge_aura()
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        _blockTree.Head.Returns(genesis);
        _poSSwitcher.HasEverReachedTerminalBlock().Returns(true);
        _poSSwitcher.IsPostMerge(genesis.Header).Returns(false);

        AuRaTerminalBlockDisposer _ = CreateDisposer();

        _auRaFinalizationManager.DidNotReceive().Dispose();
    }

    [Test]
    public void Disposes_aura_manager_when_terminal_total_difficulty_is_zero()
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        _blockTree.Head.Returns(genesis);
        _poSSwitcher.IsPostMerge(genesis.Header).Returns(false);
        _poSSwitcher.TerminalTotalDifficulty.Returns(UInt256.Zero);

        AuRaTerminalBlockDisposer _ = CreateDisposer();

        _auRaFinalizationManager.Received(1).Dispose();
    }

    [Test]
    public void Disposes_aura_manager_before_post_merge_block_is_processed()
    {
        SetHead(postMerge: false);
        Block postMergeBlock = Build.A.Block.WithNumber(30_000_000UL).TestObject;
        _poSSwitcher.IsPostMerge(postMergeBlock.Header).Returns(true);

        AuRaTerminalBlockDisposer _ = CreateDisposer();

        _branchProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(postMergeBlock));
        _poSSwitcher.TerminalBlockReached += Raise.Event();

        _auRaFinalizationManager.Received(1).Dispose();
    }

    [Test]
    public void Disposes_aura_manager_only_once_when_terminal_event_and_container_disposal_both_occur()
    {
        SetHead(postMerge: false);
        AuRaTerminalBlockDisposer disposer = CreateDisposer();

        _poSSwitcher.TerminalBlockReached += Raise.Event();
        disposer.Dispose();

        _auRaFinalizationManager.Received(1).Dispose();
    }
}
