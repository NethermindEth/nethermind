// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
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

        tree.TryUpdateMainChain(block0.Header, true, preloadedBlocks: new[] { block0 });
        tree.TryUpdateMainChain(block1.Header, true, preloadedBlocks: new[] { block1 });
        tree.TryUpdateMainChain(block2.Header, true, preloadedBlocks: new[] { block2 });

        blockInfosDb.Delete(3);

        tree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .TestObject;

        using StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, Substitute.For<IStateReader>(), NoErrorLimboLogs.Instance);
        await tree.Accept(fixer, CancellationToken.None);

        Assert.That(blockInfosDb.Get(3), Is.Null, "level 3");
        Assert.That(blockInfosDb.Get(4), Is.Null, "level 4");
        Assert.That(blockInfosDb.Get(5), Is.Null, "level 5");

        Assert.That(tree.Head!.Header, Is.EqualTo(block2.Header).UsingBlockHeaderComparer());
        Assert.That(tree.BestSuggestedHeader, Is.EqualTo(block2.Header).UsingBlockHeaderComparer());
        Assert.That(tree.BestSuggestedBody?.Body, Is.EqualTo(block2.Body).UsingBlockBodyComparer());
        Assert.That(tree.BestKnownNumber, Is.EqualTo(2));
    }

    [MaxTime(Timeout.MaxTestTime * 4)]
    [TestCase(0ul)]
    [TestCase(1ul)]
    [TestCase(2ul)]
    [TestCase(4ul)]
    [TestCase(5ul)]
    [TestCase(6ul)]
    [TestCase(65ul)]
    public async Task Suggesting_blocks_works_correctly_after_processor_restart(ulong suggestedBlocksAmount)
    {
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev, testTimeout: Timeout.MaxTestTime * 4).Build();
        await testRpc.BlockchainProcessor.StopAsync();
        IBlockTree tree = testRpc.BlockTree;
        ulong startingBlockNumber = tree.Head!.Number;

        SuggestNumberOfBlocks(tree, suggestedBlocksAmount);

        Task waitTask = suggestedBlocksAmount != 0ul
            ? testRpc.WaitForNewHeadWhere(b => b.Number == startingBlockNumber + suggestedBlocksAmount)
            : Task.CompletedTask;

        await testRpc.RestartBlockchainProcessor();

        using StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, testRpc.StateReader, NoErrorLimboLogs.Instance, 5);
        await tree.Accept(fixer, CancellationToken.None);

        await waitTask;

        await testRpc.AddBlock();
        Assert.That(tree.Head!.Number, Is.EqualTo(startingBlockNumber + suggestedBlocksAmount + 1ul));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(0ul)]
    [TestCase(1ul)]
    [TestCase(2ul)]
    [TestCase(6ul)]
    public async Task Fixer_should_not_suggest_block_without_state(ulong suggestedBlocksAmount)
    {
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await testRpc.BlockchainProcessor.StopAsync();
        IBlockTree tree = testRpc.BlockTree;

        SuggestNumberOfBlocks(tree, suggestedBlocksAmount);

        await testRpc.RestartBlockchainProcessor();

        // we create a new empty db for stateDb so we shouldn't suggest new blocks
        using IBlockTreeVisitor fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, Substitute.For<IStateReader>(), NoErrorLimboLogs.Instance, 5);
        BlockVisitOutcome result = await fixer.VisitBlock(tree.Head!, CancellationToken.None);

        Assert.That(result, Is.EqualTo(BlockVisitOutcome.None));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Fixer_should_not_suggest_block_with_null_block()
    {
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await testRpc.BlockchainProcessor.StopAsync();
        IBlockTree tree = testRpc.BlockTree;

        SuggestNumberOfBlocks(tree, 1ul);

        await testRpc.RestartBlockchainProcessor();

        using IBlockTreeVisitor fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, testRpc.StateReader, NoErrorLimboLogs.Instance, 5);
        BlockVisitOutcome result = await fixer.VisitBlock(null!, CancellationToken.None);

        Assert.That(result, Is.EqualTo(BlockVisitOutcome.None));
    }

    private static void SuggestNumberOfBlocks(IBlockTree blockTree, ulong blockAmount)
    {
        Block newParent = blockTree.Head!;
        for (ulong i = 0ul; i < blockAmount; ++i)
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

        tree.TryUpdateMainChain(block2.Header, true, preloadedBlocks: new[] { block2 });

        using StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, Substitute.For<IStateReader>(), NoErrorLimboLogs.Instance);
        await tree.Accept(fixer, CancellationToken.None);

        Assert.That(blockInfosDb.Get(3), Is.Null, "level 3");
        Assert.That(blockInfosDb.Get(4), Is.Null, "level 4");
        Assert.That(blockInfosDb.Get(5), Is.Null, "level 5");

        Assert.That(tree.BestKnownNumber, Is.EqualTo(2ul), "best known");
        Assert.That(tree.Head?.Header, Is.EqualTo(block2.Header).UsingBlockHeaderComparer());
        Assert.That(tree.BestSuggestedHeader, Is.EqualTo(block2.Header).UsingBlockHeaderComparer());
    }
}
