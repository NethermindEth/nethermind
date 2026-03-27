// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Visitors;

[Parallelizable(ParallelScope.All)]
public class StartupTreeFixerTests
{
    [Test, MaxTime(Timeout.MaxTestTime), Ignore("Not implemented")]
    public void Cleans_missing_references_from_chain_level_info()
    {
        // for now let us just look at the warnings (before we start adding cleanup)
    }

    [Test, MaxTime(Timeout.MaxTestTime), Ignore("Not implemented")]
    public void Warns_when_blocks_are_marked_as_processed_but_there_are_no_bodies()
    {
        // for now let us just look at the warnings (before we start adding cleanup)
    }

    [Test, MaxTime(Timeout.MaxTestTime), Ignore("Not implemented")]
    public void Warns_when_there_is_a_hole_in_processed_blocks()
    {
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Deletes_everything_after_the_missing_level()
    {
        MemDb blockInfosDb = new();
        BlockTreeBuilder builder = Build.A.BlockTree();
        BlockTree tree = builder
            .WithoutSettingHead
            .WithBlockInfoDb(blockInfosDb)
            .TestObject;
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;
        Block block4 = Build.A.Block.WithNumber(4).WithDifficulty(5).WithParent(block3).TestObject;
        Block block5 = Build.A.Block.WithNumber(5).WithDifficulty(6).WithParent(block4).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestBlock(block3);
        tree.SuggestBlock(block4);
        tree.SuggestHeader(block5.Header);

        tree.UpdateMainChain(block0);
        tree.UpdateMainChain(block1);
        tree.UpdateMainChain(block2);

        blockInfosDb.Delete(3);

        tree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .TestObject;

        StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, Substitute.For<IStateReader>(), LimboNoErrorLogger.Instance);
        await tree.Accept(fixer, CancellationToken.None);

        Assert.That(blockInfosDb.Get(3), Is.Null, "level 3");
        Assert.That(blockInfosDb.Get(4), Is.Null, "level 4");
        Assert.That(blockInfosDb.Get(5), Is.Null, "level 5");

        tree.Head!.Header.Should().BeEquivalentTo(block2.Header);
        tree.BestSuggestedHeader.Should().BeEquivalentTo(block2.Header);
        tree.BestSuggestedBody?.Body.Should().BeEquivalentTo(block2.Body);
        tree.BestKnownNumber.Should().Be(2);
    }

    [Retry(30)]
    [MaxTime(Timeout.MaxTestTime * 4)]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(65)]
    public async Task Suggesting_blocks_works_correctly_after_processor_restart(int suggestedBlocksAmount)
    {
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev, testTimeout: Timeout.MaxTestTime * 4).Build();
        await testRpc.BlockchainProcessor.StopAsync();
        IBlockTree tree = testRpc.BlockTree;
        long startingBlockNumber = tree.Head!.Number;

        SuggestNumberOfBlocks(tree, suggestedBlocksAmount);

        await testRpc.RestartBlockchainProcessor();

        Task waitTask = suggestedBlocksAmount != 0
            ? testRpc.WaitForNewHeadWhere(b => b.Number == startingBlockNumber + suggestedBlocksAmount)
            : Task.CompletedTask;
        // fixing after restart
        StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, testRpc.StateReader, LimboNoErrorLogger.Instance, 5);
        await tree.Accept(fixer, CancellationToken.None);

        // waiting for N new heads
        await waitTask;

        // add a new block at the end
        await testRpc.AddBlock();
        Assert.That(tree.Head!.Number, Is.EqualTo(startingBlockNumber + suggestedBlocksAmount + 1));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(6)]
    public async Task Fixer_should_not_suggest_block_without_state(int suggestedBlocksAmount)
    {
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await testRpc.BlockchainProcessor.StopAsync();
        IBlockTree tree = testRpc.BlockTree;

        SuggestNumberOfBlocks(tree, suggestedBlocksAmount);

        await testRpc.RestartBlockchainProcessor();

        // we create a new empty db for stateDb so we shouldn't suggest new blocks
        IBlockTreeVisitor fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, Substitute.For<IStateReader>(), LimboNoErrorLogger.Instance, 5);
        BlockVisitOutcome result = await fixer.VisitBlock(tree.Head!, CancellationToken.None);

        Assert.That(result, Is.EqualTo(BlockVisitOutcome.None));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Fixer_should_not_suggest_block_with_null_block()
    {
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await testRpc.BlockchainProcessor.StopAsync();
        IBlockTree tree = testRpc.BlockTree;

        SuggestNumberOfBlocks(tree, 1);

        await testRpc.RestartBlockchainProcessor();

        IBlockTreeVisitor fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, testRpc.StateReader, LimboNoErrorLogger.Instance, 5);
        BlockVisitOutcome result = await fixer.VisitBlock(null!, CancellationToken.None);

        Assert.That(result, Is.EqualTo(BlockVisitOutcome.None));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Fixer_starts_from_repaired_head()
    {
        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block repairedHead = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block queuedBlock = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(repairedHead).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(repairedHead);
        tree.SuggestBlock(queuedBlock);

        tree.UpdateMainChain(block0);
        tree.UpdateMainChain(block1);
        tree.UpdateMainChain(repairedHead);

        StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, Substitute.For<IStateReader>(), LimboNoErrorLogger.Instance);

        Assert.That(fixer.StartLevelInclusive, Is.EqualTo(repairedHead.Number + 1));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Fixer_with_repaired_head_and_recoverable_parent_suggests_blocks_normally()
    {
        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block repairedHead = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block queuedBlock = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(repairedHead).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(repairedHead);
        tree.SuggestBlock(queuedBlock);

        tree.UpdateMainChain(block0);
        tree.UpdateMainChain(block1);
        tree.UpdateMainChain(repairedHead);

        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(repairedHead.Header).Returns(true);
        List<Hash256> suggestedBlocks = [];
        EventHandler<BlockEventArgs> handler = (_, args) => suggestedBlocks.Add(args.Block.Hash!);
        tree.NewBestSuggestedBlock += handler;

        try
        {
            StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, stateReader, LimboNoErrorLogger.Instance);
            await tree.Accept(fixer, CancellationToken.None);
        }
        finally
        {
            tree.NewBestSuggestedBlock -= handler;
        }

        suggestedBlocks.Should().Equal(queuedBlock.Hash!);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Fixer_only_suggests_blocks_that_descend_from_repaired_head()
    {
        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block repairedHead = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).WithStateRoot(TestItem.KeccakA).TestObject;
        Block staleForkHead = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).WithStateRoot(TestItem.KeccakB).TestObject;
        Block canonicalBlock3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(repairedHead).WithStateRoot(TestItem.KeccakB).TestObject;
        Block staleBlock3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(staleForkHead).WithStateRoot(TestItem.KeccakC).TestObject;
        Block canonicalBlock4 = Build.A.Block.WithNumber(4).WithDifficulty(5).WithParent(canonicalBlock3).WithStateRoot(TestItem.KeccakD).TestObject;
        Block staleBlock4 = Build.A.Block.WithNumber(4).WithDifficulty(5).WithParent(staleBlock3).WithStateRoot(TestItem.KeccakE).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(repairedHead);
        tree.SuggestBlock(staleForkHead);
        tree.SuggestBlock(canonicalBlock3);
        tree.SuggestBlock(staleBlock3);
        tree.SuggestBlock(canonicalBlock4);
        tree.SuggestBlock(staleBlock4);

        tree.UpdateMainChain(block0);
        tree.UpdateMainChain(block1);
        tree.UpdateMainChain(repairedHead);

        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(repairedHead.Header).Returns(true);

        List<Hash256> suggestedBlocks = [];
        EventHandler<BlockEventArgs> handler = (_, args) => suggestedBlocks.Add(args.Block.Hash!);
        tree.NewBestSuggestedBlock += handler;

        try
        {
            StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, stateReader, LimboNoErrorLogger.Instance, 16);
            await tree.Accept(fixer, CancellationToken.None);
        }
        finally
        {
            tree.NewBestSuggestedBlock -= handler;
        }

        suggestedBlocks.Should().Equal(canonicalBlock3.Hash!, canonicalBlock4.Hash!);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Fixer_logs_error_when_head_does_not_match_persisted_state_info()
    {
        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).WithStateRoot(TestItem.KeccakA).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).WithStateRoot(TestItem.KeccakB).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.UpdateMainChain(block0);
        tree.UpdateMainChain(block1);

        TestLogger testLogger = new();
        IPersistedStateInfoProvider persistedStateInfoProvider = Substitute.For<IPersistedStateInfoProvider>();
        persistedStateInfoProvider.TryGetPersistedStateInfo(out Arg.Any<PersistedStateInfo>())
            .Returns(callInfo =>
            {
                callInfo[0] = new PersistedStateInfo(block1.Number, TestItem.KeccakC);
                return true;
            });

        _ = new StartupBlockTreeFixer(
            new SyncConfig(),
            tree,
            Substitute.For<IStateReader>(),
            new ILogger(testLogger),
            persistedStateInfoProvider: persistedStateInfoProvider);

        testLogger.LogList.Should().Contain(log => log.Contains("Startup head does not match persisted state info.", StringComparison.Ordinal));
    }

    private static void SuggestNumberOfBlocks(IBlockTree blockTree, int blockAmount)
    {
        Block newParent = blockTree.Head!;
        for (int i = 0; i < blockAmount; ++i)
        {
            Block newBlock = Build.A.Block
                .WithNumber(newParent.Number + 1)
                .WithDifficulty(newParent.Difficulty + 1)
                .WithParent(newParent)
                .WithStateRoot(newParent.StateRoot!).TestObject;
            blockTree.SuggestBlock(newBlock);
            newParent = newBlock;
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task When_head_block_is_followed_by_a_block_bodies_gap_it_should_delete_all_levels_after_the_gap_start()
    {
        MemDb blockInfosDb = new();

        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithBlockInfoDb(blockInfosDb)
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;
        Block block4 = Build.A.Block.WithNumber(4).WithDifficulty(5).WithParent(block3).TestObject;
        Block block5 = Build.A.Block.WithNumber(5).WithDifficulty(6).WithParent(block4).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestHeader(block3.Header);
        tree.SuggestHeader(block4.Header);
        tree.SuggestBlock(block5);

        tree.UpdateMainChain(block2);

        StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, Substitute.For<IStateReader>(), LimboNoErrorLogger.Instance);
        await tree.Accept(fixer, CancellationToken.None);

        Assert.That(blockInfosDb.Get(3), Is.Null, "level 3");
        Assert.That(blockInfosDb.Get(4), Is.Null, "level 4");
        Assert.That(blockInfosDb.Get(5), Is.Null, "level 5");

        Assert.That(tree.BestKnownNumber, Is.EqualTo(2L), "best known");
        Assert.That(tree.Head?.Header, Is.EqualTo(block2.Header), "head");
        Assert.That(tree.BestSuggestedHeader!.Hash, Is.EqualTo(block2.Hash), "suggested");
    }
}
