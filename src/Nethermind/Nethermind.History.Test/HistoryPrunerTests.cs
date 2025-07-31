// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.History.Test;

public class HistoryPrunerTests
{
    private const long SecondsPerSlot = 1;
    private const long BeaconGenesisBlockNumber = 50;
    private static readonly ulong BeaconGenesisTimestamp = (ulong)new DateTimeOffset(TestBlockchain.InitialTimestamp).ToUnixTimeSeconds() + (BeaconGenesisBlockNumber * SecondsPerSlot);
    private static readonly IBlocksConfig BlocksConfig = new BlocksConfig()
    {
        SecondsPerSlot = SecondsPerSlot
    };

    [Test]
    public async Task Can_prune_blocks_older_than_specified_epochs()
    {
        const int blocks = 100;
        const int cutoff = 36;

        // n.b. technically invalid, should be at least 82125 epochs
        // however not feasible to test this
        IHistoryConfig historyConfig = new HistoryConfig
        {
            RetentionEpochs = 2,
            DropPreMerge = false
        };

        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(BuildContainer(historyConfig));

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < blocks; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        Block head = testBlockchain.BlockTree.Head;
        Assert.That(head, Is.Not.Null);
        testBlockchain.BlockTree.SyncPivot = (blocks, Hash256.Zero);

        IHistoryPruner historyPruner = testBlockchain.Container.Resolve<IHistoryPruner>();


        CheckOldestAndCutoff(1, cutoff, historyPruner);

        await historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);
        for (int i = 1; i <= blocks; i++)
        {
            if (i < cutoff)
            {
                CheckBlockPruned(testBlockchain, blockHashes, i);
            }
            else
            {
                CheckBlockPreserved(testBlockchain, blockHashes, i);
            }
        }

        CheckHeadPreserved(testBlockchain, blocks);
        CheckOldestAndCutoff(cutoff, cutoff, historyPruner);
    }

    [Test]
    public async Task Can_prune_pre_merge_blocks()
    {
        const int blocks = 100;

        IHistoryConfig historyConfig = new HistoryConfig
        {
            RetentionEpochs = 100, // should have no effect
            DropPreMerge = true
        };
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(BuildContainer(historyConfig));

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < blocks; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        Block head = testBlockchain.BlockTree.Head;
        Assert.That(head, Is.Not.Null);
        testBlockchain.BlockTree.SyncPivot = (blocks, Hash256.Zero);

        IHistoryPruner historyPruner = testBlockchain.Container.Resolve<IHistoryPruner>();

        CheckOldestAndCutoff(1, BeaconGenesisBlockNumber, historyPruner);

        await historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);

        for (int i = 1; i <= blocks; i++)
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

        CheckHeadPreserved(testBlockchain, blocks);
        CheckOldestAndCutoff(BeaconGenesisBlockNumber, BeaconGenesisBlockNumber, historyPruner);
    }

    [Test]
    public async Task Prunes_up_to_sync_pivot()
    {
        const int blocks = 100;
        const long syncPivot = 20;

        IHistoryConfig historyConfig = new HistoryConfig
        {
            RetentionEpochs = null,
            DropPreMerge = true
        };
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(BuildContainer(historyConfig));

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < blocks; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        Block head = testBlockchain.BlockTree.Head;
        Assert.That(head, Is.Not.Null);
        testBlockchain.BlockTree.SyncPivot = (syncPivot, Hash256.Zero);

        IHistoryPruner historyPruner = testBlockchain.Container.Resolve<IHistoryPruner>();

        CheckOldestAndCutoff(1, BeaconGenesisBlockNumber, historyPruner);

        await historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);

        for (int i = 1; i <= blocks; i++)
        {
            if (i < syncPivot)
            {
                CheckBlockPruned(testBlockchain, blockHashes, i);
            }
            else
            {
                CheckBlockPreserved(testBlockchain, blockHashes, i);
            }
        }

        CheckHeadPreserved(testBlockchain, blocks);
        CheckOldestAndCutoff(syncPivot, BeaconGenesisBlockNumber, historyPruner);
    }

    [Test]
    public async Task Can_find_oldest_block()
    {
        const int blocks = 100;
        const int cutoff = 36;

        IHistoryConfig historyConfig = new HistoryConfig
        {
            RetentionEpochs = 2,
            DropPreMerge = false
        };
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(BuildContainer(historyConfig));

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < blocks; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        Block head = testBlockchain.BlockTree.Head;
        Assert.That(head, Is.Not.Null);
        testBlockchain.BlockTree.SyncPivot = (blocks, Hash256.Zero);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();

        CheckOldestAndCutoff(1, cutoff, historyPruner);

        await historyPruner.TryPruneHistory(CancellationToken.None);
        historyPruner.FindOldestBlock(); // recalculate oldest block with binary search

        CheckOldestAndCutoff(cutoff, cutoff, historyPruner);
    }

    [Test]
    public async Task Does_not_prune_when_disabled()
    {
        const int blocks = 10;

        IHistoryConfig historyConfig = new HistoryConfig
        {
            RetentionEpochs = null,
            DropPreMerge = false
        };
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(BuildContainer(historyConfig));

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < blocks; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        IHistoryPruner historyPruner = testBlockchain.Container.Resolve<IHistoryPruner>();
        await historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);

        for (int i = 1; i <= blocks; i++)
        {
            CheckBlockPreserved(testBlockchain, blockHashes, i);
        }

        CheckHeadPreserved(testBlockchain, blocks);
    }

    [Test]
    public void Can_accept_valid_config()
    {
        IHistoryConfig validHistoryConfig = new HistoryConfig
        {
            RetentionEpochs = 100000,
            DropPreMerge = false
        };

        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec() { MinHistoryRetentionEpochs = 100 });
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(new TestMemDb());

        Assert.DoesNotThrow(() => new HistoryPruner(
            Substitute.For<IBlockTree>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IChainLevelInfoRepository>(),
            dbProvider,
            validHistoryConfig,
            BlocksConfig,
            new ProcessExitSource(new()),
            Substitute.For<IBackgroundTaskScheduler>(),
            Substitute.For<IBlockProcessingQueue>(),
            LimboLogs.Instance));
    }

    [Test]
    public void Can_reject_invalid_config()
    {
        IHistoryConfig invalidHistoryConfig = new HistoryConfig
        {
            RetentionEpochs = 10,
            DropPreMerge = false
        };

        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec() { MinHistoryRetentionEpochs = 100 });
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(new TestMemDb());

        Assert.Throws<HistoryPruner.HistoryPrunerException>(() => new HistoryPruner(
            Substitute.For<IBlockTree>(),
            Substitute.For<IReceiptStorage>(),
            specProvider,
            Substitute.For<IChainLevelInfoRepository>(),
            dbProvider,
            invalidHistoryConfig,
            BlocksConfig,
            new ProcessExitSource(new()),
            Substitute.For<IBackgroundTaskScheduler>(),
            Substitute.For<IBlockProcessingQueue>(),
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

    private static void CheckOldestAndCutoff(long oldest, long cutoff, IHistoryPruner historyPruner)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyPruner.CutoffBlockNumber, Is.EqualTo(cutoff));
            Assert.That(historyPruner.OldestBlockNumber, Is.EqualTo(oldest));
        }
    }

    private static Action<ContainerBuilder> BuildContainer(IHistoryConfig historyConfig)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.BeaconChainGenesisTimestamp.Returns(BeaconGenesisTimestamp);
        specProvider.MergeBlockNumber.Returns(new ForkActivation(BeaconGenesisBlockNumber, BeaconGenesisTimestamp));

        return containerBuilder => containerBuilder
            .AddSingleton(specProvider)
            .AddSingleton(historyConfig)
            .AddSingleton(BlocksConfig);
    }
}
