// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
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
    public void Registered_as_singleton_and_resolving_triggers_disposal_when_head_post_merge()
    {
        // The disposer is wired through DI (AuRaMergeModule + InitializeBlockchainAuRaMerge) rather than
        // hand-constructed: registering it as a singleton and resolving it must trigger the same ctor
        // side-effect (immediate disposal on a post-merge head) and yield a single instance.
        SetHead(postMerge: true);

        ContainerBuilder builder = new();
        builder
            .AddSingleton(_auRaFinalizationManager)
            .AddSingleton(_poSSwitcher)
            .AddSingleton(_blockTree)
            .AddSingleton<AuRaTerminalBlockDisposer>();
        using IContainer container = builder.Build();

        AuRaTerminalBlockDisposer disposer = container.Resolve<AuRaTerminalBlockDisposer>();

        Assert.That(container.Resolve<AuRaTerminalBlockDisposer>(), Is.SameAs(disposer));
        _auRaFinalizationManager.Received(1).Dispose();
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
