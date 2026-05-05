// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
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
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.History.Test;

public class HistoryPrunerTests
{
    private const long SecondsPerSlot = 1;
    private const long BeaconGenesisBlockNumber = 50;
    private static readonly IBlocksConfig BlocksConfig = new BlocksConfig()
    {
        SecondsPerSlot = SecondsPerSlot
    };

    private static readonly ISyncConfig SyncConfig = new SyncConfig()
    {
        AncientBodiesBarrier = BeaconGenesisBlockNumber,
        AncientReceiptsBarrier = BeaconGenesisBlockNumber,
        PivotNumber = 100,
        SnapSync = true
    };

    private static IEnumerable<TestCaseData> PruningCases()
    {
        const int blocks = 100;

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 },
            /*syncPivot:*/ (long)blocks,
            /*primeWithOldestRead:*/ true,
            /*expectedPruneBelow:*/ 36L,
            /*finalCutoff:*/ 36L
        ).SetName("Can_prune_blocks_older_than_specified_epochs");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 },
            /*syncPivot:*/ (long)blocks,
            /*primeWithOldestRead:*/ false, // regression: pruner must self-bootstrap without external OldestBlockHeader call
            /*expectedPruneBelow:*/ 36L,
            /*finalCutoff:*/ 36L
        ).SetName("Can_prune_without_prior_oldest_block_read");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.UseAncientBarriers, RetentionEpochs = 100 /* no effect in UseAncientBarriers mode */, PruningInterval = 0 },
            /*syncPivot:*/ (long)blocks,
            /*primeWithOldestRead:*/ true,
            /*expectedPruneBelow:*/ BeaconGenesisBlockNumber,
            /*finalCutoff:*/ BeaconGenesisBlockNumber
        ).SetName("Can_prune_to_ancient_barriers");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.UseAncientBarriers, PruningInterval = 0 },
            /*syncPivot:*/ 20L, // below BeaconGenesisBlockNumber — sync pivot caps the prune boundary
            /*primeWithOldestRead:*/ true,
            /*expectedPruneBelow:*/ 20L,
            /*finalCutoff:*/ BeaconGenesisBlockNumber
        ).SetName("Prunes_up_to_sync_pivot");
    }

    [TestCaseSource(nameof(PruningCases))]
    public async Task Prunes_history(
        IHistoryConfig historyConfig,
        long syncPivot,
        bool primeWithOldestRead,
        long expectedPruneBelow,
        long finalCutoff)
    {
        const int blocks = 100;

        List<Hash256> blockHashes = [];
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: syncPivot, blockHashes: blockHashes);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();

        if (primeWithOldestRead)
            CheckOldestAndCutoff(1, finalCutoff, historyPruner);

        historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);
        for (int i = 1; i <= blocks; i++)
        {
            if (i < expectedPruneBelow)
                CheckBlockPruned(testBlockchain, blockHashes, i);
            else
                CheckBlockPreserved(testBlockchain, blockHashes, i);
        }

        CheckHeadPreserved(testBlockchain, blocks);
        CheckOldestAndCutoff(expectedPruneBelow, finalCutoff, historyPruner);
    }

    [Test]
    public async Task Can_find_oldest_block()
    {
        const int blocks = 100;
        const int cutoff = 36;

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 2,
            PruningInterval = 0
        };

        List<Hash256> blockHashes = [];
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks, blockHashes: blockHashes);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();

        CheckOldestAndCutoff(1, cutoff, historyPruner);

        historyPruner.TryPruneHistory(CancellationToken.None);
        historyPruner.SetDeletePointerToOldestBlock(); // recalculate oldest block with binary search

        CheckOldestAndCutoff(cutoff, cutoff, historyPruner);
    }

    [Test]
    public async Task Does_not_prune_when_disabled()
    {
        const int blocks = 10;

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Disabled,
            PruningInterval = 0
        };
        List<Hash256> blockHashes = [];
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, blockHashes: blockHashes);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);

        for (int i = 1; i <= blocks; i++)
        {
            CheckBlockPreserved(testBlockchain, blockHashes, i);
        }

        CheckHeadPreserved(testBlockchain, blocks);
    }

    [TestCase(0, 100000u, false)]
    [TestCase(100, 10u, true)]
    public void Validates_config(int minHistoryRetentionEpochs, uint retentionEpochs, bool shouldThrow)
    {
        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = retentionEpochs,
        };
        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec { MinHistoryRetentionEpochs = minHistoryRetentionEpochs });
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(new TestMemDb());

        TestDelegate action = () => new HistoryPruner(
            Substitute.For<IBlockTree>(),
            Substitute.For<IReceiptStorage>(),
            specProvider ?? Substitute.For<ISpecProvider>(),
            Substitute.For<IChainLevelInfoRepository>(),
            dbProvider,
            historyConfig,
            BlocksConfig,
            SyncConfig,
            new ProcessExitSource(new()),
            Substitute.For<IBackgroundTaskScheduler>(),
            Substitute.For<IBlockProcessingQueue>(),
            LimboLogs.Instance);

        if (shouldThrow)
            Assert.Throws<HistoryPruner.HistoryPrunerException>(action);
        else
            Assert.DoesNotThrow(action);
    }

    [TestCase(5u)]
    [TestCase(0u)]
    public async Task SchedulePruneHistory_passes_configured_timeout_to_scheduler(uint pruningTimeoutSeconds)
    {
        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 100000,
            PruningTimeoutSeconds = pruningTimeoutSeconds,
            PruningInterval = 0
        };

        CapturingScheduler scheduler = new();
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(BuildContainer(historyConfig, scheduler));

        IHistoryPruner historyPruner = testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.SchedulePruneHistory();

        await scheduler.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        TimeSpan? expected = pruningTimeoutSeconds == 0 ? null : TimeSpan.FromSeconds(pruningTimeoutSeconds);
        Assert.That(scheduler.CapturedTimeout, Is.EqualTo(expected));
    }

    // Pointer = max(genesis block number, persisted DB value) — persisted value wins when above genesis
    [Test]
    public async Task Delete_pointer_is_not_reset_on_restart()
    {
        const int blocks = 100;
        const long storedPointer = 50;

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 2,
            PruningInterval = 0
        };

        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        IDb metadataDb = testBlockchain.Container.Resolve<IDbProvider>().MetadataDb;
        metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(storedPointer).Bytes);

        IHistoryPruner historyPruner = testBlockchain.Container.Resolve<IHistoryPruner>();

        Assert.That(historyPruner.OldestBlockHeader?.Number, Is.EqualTo(storedPointer));
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
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(blockNumber, blockHashes[blockNumber]), Is.False, $"Receipt for block {blockNumber} should be pruned");

            // should still be preserved
            Assert.That(testBlockchain.BlockTree.FindHeader(blockNumber, BlockTreeLookupOptions.None), Is.Not.Null, $"Header {blockNumber} should still exist");
            Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(blockNumber), Is.Not.Null, $"Block info {blockNumber} should still exist");
        }
    }

    private static void CheckOldestAndCutoff(long oldest, long cutoff, IHistoryPruner historyPruner)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyPruner.CutoffBlockNumber, Is.EqualTo(cutoff));
            Assert.That(historyPruner.OldestBlockHeader.Number, Is.EqualTo(oldest));
        }
    }

    private static async Task<BasicTestBlockchain> CreateBlockchainWithBlocks(
        IHistoryConfig historyConfig,
        int blocks,
        long? syncPivot = null,
        List<Hash256> blockHashes = null,
        IBackgroundTaskScheduler scheduler = null)
    {
        BasicTestBlockchain bc = await BasicTestBlockchain.Create(BuildContainer(historyConfig, scheduler));
        blockHashes?.Add(bc.BlockTree.Head!.Hash!);
        for (int i = 0; i < blocks; i++)
        {
            await bc.AddBlock();
            blockHashes?.Add(bc.BlockTree.Head!.Hash!);
        }
        if (syncPivot is { } pivot)
            bc.BlockTree.SyncPivot = (pivot, Hash256.Zero);
        return bc;
    }

    private sealed class CapturingScheduler : IBackgroundTaskScheduler
    {
        public TimeSpan? CapturedTimeout { get; private set; }
        public TaskCompletionSource Invoked { get; } = new();

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null, string source = null)
        {
            CapturedTimeout = timeout;
            Invoked.TrySetResult();
            return true;
        }
    }

    private static Action<ContainerBuilder> BuildContainer(IHistoryConfig historyConfig, IBackgroundTaskScheduler scheduler = null)
    {
        // n.b. in prod MinHistoryRetentionEpochs should be 82125, however not feasible to test this
        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec() { MinHistoryRetentionEpochs = 0 });

        // prevent pruner being triggered by empty queue
        IBlockProcessingQueue blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();

        return containerBuilder =>
        {
            containerBuilder
            .AddSingleton(specProvider)
            .AddSingleton(blockProcessingQueue)
            .AddSingleton(historyConfig)
            .AddSingleton(BlocksConfig)
            .AddSingleton(SyncConfig);

            if (scheduler is not null)
                containerBuilder.AddSingleton(scheduler);
        };
    }
}
