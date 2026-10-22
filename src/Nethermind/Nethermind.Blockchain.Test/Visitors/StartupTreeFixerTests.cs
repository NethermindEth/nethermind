// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    // A flat DB keeps the state of its in-memory tail on a clean shutdown, so the blocks above the persisted state
    // need no re-execution; only those whose state was not kept, or that were never processed, are suggested.
    [TestCase(2ul, 5ul, 2ul, TestName = "Nothing kept: everything above the persisted state is re-executed")]
    [TestCase(4ul, 5ul, 4ul, TestName = "Part kept: resumes from the last block with state")]
    [TestCase(5ul, 5ul, 5ul, TestName = "All kept: resumes from the head")]
    [TestCase(5ul, 3ul, 3ul, TestName = "Unprocessed blocks with state are still executed")]
    [MaxTime(Timeout.MaxTestTime)]
    public async Task Resumes_from_the_last_processed_block_whose_state_was_kept(ulong keptUpTo, ulong processedUpTo, ulong expectedHead)
    {
        MemDb blockInfosDb = new();
        BlockTreeBuilder builder = Build.A.BlockTree().WithoutSettingHead.WithBlockInfoDb(blockInfosDb);
        BlockTree tree = builder.TestObject;
        Block[] blocks = new Block[6];
        for (int i = 0; i < blocks.Length; i++)
        {
            BlockBuilder block = Build.A.Block.WithNumber((ulong)i).WithDifficulty((ulong)i + 1);
            blocks[i] = (i == 0 ? block : block.WithParent(blocks[i - 1])).TestObject;
            tree.SuggestBlock(blocks[i]);
            if ((ulong)i <= processedUpTo) tree.TryUpdateMainChain(blocks[i].Header, true, preloadedBlocks: [blocks[i]]);
        }

        // An orphaned sibling of block 3, as a devnet leaves in the in-memory window; its state was kept too.
        Block fork = Build.A.Block.WithNumber(3).WithDifficulty(1).WithParent(blocks[2]).WithExtraData([1]).TestObject;
        tree.SuggestBlock(fork);

        // Restart with the persisted state at block 2.
        tree = Build.A.BlockTree().WithoutSettingHead.WithDatabaseFrom(builder).WithBestPersistedState(2).TestObject;
        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(c => c.Arg<BlockHeader>().Number <= Math.Max(keptUpTo, 2));
        List<Hash256> suggested = [];
        tree.NewBestSuggestedBlock += (_, e) => suggested.Add(e.Block.Hash!);
        Assert.That(tree.Head!.Number, Is.EqualTo(2ul));

        ulong moved = KeptStateResumer.Resume(tree, stateReader, NoErrorLimboLogs.Instance);
        using StartupBlockTreeFixer fixer = new(new SyncConfig(), tree, stateReader, NoErrorLimboLogs.Instance);
        await tree.Accept(fixer, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(moved, Is.EqualTo(expectedHead - 2));
            Assert.That(tree.Head!.Hash, Is.EqualTo(blocks[expectedHead].Hash));
            Assert.That(tree.FindHeader(3, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(blocks[3].Hash), "the fork stays off the canonical chain");
            Assert.That(suggested.Where(h => h != fork.Hash), Is.EqualTo(blocks[((int)expectedHead + 1)..].Select(static b => b.Hash)));
            Assert.That(suggested.Contains(fork.Hash!), Is.EqualTo(expectedHead < 3), "a fork below the resumed head is not re-executed");
        }
    }

    [MaxTime(Timeout.MaxTestTime * 4)]
    [Test]
    public async Task Suggesting_blocks_works_correctly_after_processor_restart([Values(0ul, 1ul, 2ul, 4ul, 5ul, 6ul, 65ul)] ulong suggestedBlocksAmount)
    {
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev, testTimeout: Timeout.MaxTestTime * 4).Build();
        await testRpc.BlockProcessingQueue.StopAsync();
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
    [Test]
    public async Task Fixer_should_not_suggest_block_without_state([Values(0ul, 1ul, 2ul, 6ul)] ulong suggestedBlocksAmount)
    {
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await testRpc.BlockProcessingQueue.StopAsync();
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
        await testRpc.BlockProcessingQueue.StopAsync();
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
