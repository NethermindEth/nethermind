// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.HistoryPruning;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.HistoryPruning;

public class HistoryPrunerTests
{
    private const long SecondsPerSlot = 1;
    private const long BeaconGenesisBlockNumber = 50;
    private static readonly ulong BeaconGenesisTimestamp = (ulong)new DateTimeOffset(TestBlockchain.InitialTimestamp).ToUnixTimeSeconds() + (BeaconGenesisBlockNumber * SecondsPerSlot);

    [Test]
    public async Task Can_prune_blocks_older_than_specified_epochs()
    {
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        Core.Block head = testBlockchain.BlockTree.Head;
        Assert.That(head, Is.Not.Null);

        // n.b. technically invalid, should be at least 82125 epochs
        // however not feasible to test this
        IHistoryConfig historyConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = 2,
            DropPreMerge = false
        };
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        HistoryPruner historyPruner = new(
            testBlockchain.BlockTree,
            testBlockchain.ReceiptStorage,
            specProvider,
            testBlockchain.BlockStore,
            testBlockchain.ChainLevelInfoRepository,
            testBlockchain.DbProvider.MetadataDb,
            historyConfig,
            SecondsPerSlot,
            LimboLogs.Instance);

        testBlockchain.BlockTree.SyncPivot = (1000, Hash256.Zero);

        await historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);
        for (int i = 1; i <= 100; i++)
        {
            if (i < 100 - 64)
            {
                CheckBlockPruned(testBlockchain, blockHashes, i);
            }
            else
            {
                CheckBlockPreserved(testBlockchain, blockHashes, i);
            }
        }

        CheckHeadPreserved(testBlockchain, 100L);
    }

    [Test]
    public async Task Can_prune_pre_merge_blocks()
    {
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        Core.Block head = testBlockchain.BlockTree.Head;
        Assert.That(head, Is.Not.Null);

        IHistoryConfig historyConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = null,
            DropPreMerge = true
        };
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.BeaconChainGenesisTimestamp.Returns(BeaconGenesisTimestamp);

        HistoryPruner historyPruner = new(
            testBlockchain.BlockTree,
            testBlockchain.ReceiptStorage,
            specProvider,
            testBlockchain.BlockStore,
            testBlockchain.ChainLevelInfoRepository,
            testBlockchain.DbProvider.MetadataDb,
            historyConfig,
            SecondsPerSlot,
            LimboLogs.Instance);

        testBlockchain.BlockTree.SyncPivot = (1000, Hash256.Zero);

        await historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);

        for (int i = 1; i <= 100; i++)
        {
            if (i < BeaconGenesisBlockNumber)
            {
                CheckBlockPruned(testBlockchain, blockHashes, i);
            }
            else
            {
                CheckBlockPreserved(testBlockchain, blockHashes, i);
            }
        }

        CheckHeadPreserved(testBlockchain, 100L);
    }

    [Test]
    public async Task Does_not_prune_when_disabled()
    {
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < 10; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        IHistoryConfig historyConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = null,
            DropPreMerge = false
        };

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        HistoryPruner historyPruner = new(
            testBlockchain.BlockTree,
            testBlockchain.ReceiptStorage,
            specProvider,
            testBlockchain.BlockStore,
            testBlockchain.ChainLevelInfoRepository,
            testBlockchain.DbProvider.MetadataDb,
            historyConfig,
            SecondsPerSlot,
            LimboLogs.Instance);

        await historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);

        for (int i = 1; i <= 10; i++)
        {
            CheckBlockPreserved(testBlockchain, blockHashes, i);
        }

        CheckHeadPreserved(testBlockchain, 10L);
    }

    [Test]
    public void Can_accept_valid_config()
    {
        IHistoryConfig validHistoryConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = 100000,
            DropPreMerge = false
        };

        Assert.DoesNotThrow(() => new HistoryPruner(
            Substitute.For<IBlockTree>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IBlockStore>(),
            Substitute.For<IChainLevelInfoRepository>(),
            Substitute.For<IDb>(),
            validHistoryConfig,
            SecondsPerSlot,
            LimboLogs.Instance));
    }

    [Test]
    public void Can_reject_invalid_config()
    {
        IHistoryConfig invalidHistoryConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = 10,
            DropPreMerge = false
        };

        Assert.Throws<HistoryPruner.HistoryPrunerException>(() => new HistoryPruner(
            Substitute.For<IBlockTree>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IBlockStore>(),
            Substitute.For<IChainLevelInfoRepository>(),
            Substitute.For<IDb>(),
            invalidHistoryConfig,
            SecondsPerSlot,
            LimboLogs.Instance));
    }

    private static void CheckGenesisPreserved(BasicTestBlockchain testBlockchain, Hash256 genesisHash)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.FindBlock(0, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block should still exist");
            Assert.That(testBlockchain.BlockTree.FindHeader(0, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block header should still exist");
            Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(0), Is.Not.Null, "Genesis block info should still exist");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(0, genesisHash), Is.True, "Genesis block receipt should still exist");
        }
    }

    private static void CheckHeadPreserved(BasicTestBlockchain testBlockchain, long headNumber)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.BestKnownNumber, Is.EqualTo(headNumber), "BestKnownNumber should be maintained");
            Assert.That(testBlockchain.BlockTree.Head?.Number, Is.EqualTo(headNumber), "Head should be maintained");
        }
    }

    private static void CheckBlockPreserved(BasicTestBlockchain testBlockchain, List<Hash256> blockHashes, int blockNumber)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.FindBlock(blockNumber, BlockTreeLookupOptions.None), Is.Not.Null, $"Block {blockNumber} should still exist");
            Assert.That(testBlockchain.BlockTree.FindHeader(blockNumber, BlockTreeLookupOptions.None), Is.Not.Null, $"Header {blockNumber} should still exist");
            Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(blockNumber), Is.Not.Null, $"Block info {blockNumber} should still exist");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(blockNumber, blockHashes[blockNumber]), Is.True, $"Receipt for block {blockNumber} should still exist");
        }
    }

    private static void CheckBlockPruned(BasicTestBlockchain testBlockchain, List<Hash256> blockHashes, int blockNumber)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.FindBlock(blockNumber, BlockTreeLookupOptions.None), Is.Null, $"Block {blockNumber} should be pruned");
            Assert.That(testBlockchain.BlockTree.FindHeader(blockNumber, BlockTreeLookupOptions.None), Is.Null, $"Header {blockNumber} should be pruned");
            Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(blockNumber), Is.Null, $"Block info {blockNumber} should be pruned");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(blockNumber, blockHashes[blockNumber]), Is.False, $"Receipt for block {blockNumber} should be pruned");
        }
    }

    // [Test, MaxTime(Timeout.MaxTestTime)]
    // public void Can_delete_blocks_before_timestamp()
    // {
    //     BlockTree tree = Build.A.BlockTree()
    //         .WithoutSettingHead
    //         .TestObject;

    //     List<Block> blocks = [];
    //     Block? parentBlock = null;

    //     for (int i = 0; i <= 5; i++)
    //     {
    //         BlockBuilder blockBuilder = Build.A.Block
    //             .WithNumber(i)
    //             .WithDifficulty((ulong)i + 1)
    //             .WithTimestamp(1000 + (ulong)i);

    //         if (parentBlock != null)
    //         {
    //             blockBuilder.WithParent(parentBlock);
    //         }

    //         Block block = blockBuilder.TestObject;
    //         blocks.Add(block);
    //         tree.SuggestBlock(block);
    //         parentBlock = block;
    //     }

    //     tree.UpdateMainChain(blocks, true);

    //     using (Assert.EnterMultipleScope())
    //     {
    //         Assert.That(tree.BestKnownNumber, Is.EqualTo(5L), "BestKnownNumber should be 5 before deletion");
    //         Assert.That(tree.FindBlock(0, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block should exist before deletion");
    //         Assert.That(tree.FindBlock(1, BlockTreeLookupOptions.None), Is.Not.Null, "Block 1 should exist before deletion");
    //         Assert.That(tree.FindBlock(2, BlockTreeLookupOptions.None), Is.Not.Null, "Block 2 should exist before deletion");
    //         Assert.That(tree.FindBlock(3, BlockTreeLookupOptions.None), Is.Not.Null, "Block 3 should exist before deletion");
    //         Assert.That(tree.BestKnownNumber, Is.EqualTo(5L), "BestKnownNumber should remain 5 after deletion");
    //     }

    //     foreach (var deletedBlock in HistoryPruner.DeleteBlocksBeforeTimestamp(1003, CancellationToken.None))
    //     {
    //         Assert.That(deletedBlock.Number, Is.InRange(1, 2));
    //     }

    //     using (Assert.EnterMultipleScope())
    //     {
    //         Assert.That(tree.FindBlock(0, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block should still exist after deletion");
    //         Assert.That(tree.FindBlock(1, BlockTreeLookupOptions.None), Is.Null, "Block 1 should be deleted");
    //         Assert.That(tree.FindBlock(2, BlockTreeLookupOptions.None), Is.Null, "Block 2 should be deleted");
    //         Assert.That(tree.FindBlock(3, BlockTreeLookupOptions.None), Is.Not.Null, "Block 3 should still exist");
    //         Assert.That(tree.FindBlock(4, BlockTreeLookupOptions.None), Is.Not.Null, "Block 4 should still exist");
    //         Assert.That(tree.FindBlock(5, BlockTreeLookupOptions.None), Is.Not.Null, "Block 5 should still exist");
    //         Assert.That(tree.BestKnownNumber, Is.EqualTo(5L), "BestKnownNumber should remain 5 after deletion");
    //         Assert.That(tree.Head?.Number, Is.EqualTo(5L), "Head should remain at block 5");
    //     }
    // }
}
