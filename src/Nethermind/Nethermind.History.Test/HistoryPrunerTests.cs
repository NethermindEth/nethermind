// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
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
    private const ulong BeaconGenesisBlockNumber = 50;
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
        const uint blocks = 100;

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 },
            /*syncPivot:*/ blocks,
            /*primeWithOldestRead:*/ true,
            /*expectedPruneBelow:*/ 36UL,
            /*finalCutoff:*/ 36UL
        ).SetName("Can_prune_blocks_older_than_specified_epochs");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 },
            /*syncPivot:*/ blocks,
            /*primeWithOldestRead:*/ false, // regression: pruner must self-bootstrap without external OldestBlockHeader call
            /*expectedPruneBelow:*/ 36UL,
            /*finalCutoff:*/ 36UL
        ).SetName("Can_prune_without_prior_oldest_block_read");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.UseAncientBarriers, RetentionEpochs = 100 /* no effect in UseAncientBarriers mode */, PruningInterval = 0 },
            /*syncPivot:*/ blocks,
            /*primeWithOldestRead:*/ true,
            /*expectedPruneBelow:*/ BeaconGenesisBlockNumber,
            /*finalCutoff:*/ BeaconGenesisBlockNumber
        ).SetName("Can_prune_to_ancient_barriers");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.UseAncientBarriers, PruningInterval = 0 },
            /*syncPivot:*/ 20UL, // below BeaconGenesisBlockNumber — sync pivot caps the prune boundary
            /*primeWithOldestRead:*/ true,
            /*expectedPruneBelow:*/ 20UL,
            /*finalCutoff:*/ BeaconGenesisBlockNumber
        ).SetName("Prunes_up_to_sync_pivot");

        // 5 epochs × 32 slots = 160 blocks of retention > chain length (100) — CalculateRollingCutoff
        // Retention window (5 × 32 = 160 blocks) exceeds chain length (100), so the cutoff is clamped to 0 and no pruning occurs.
        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 5, PruningInterval = 0 },
            /*syncPivot:*/ blocks,
            /*primeWithOldestRead:*/ false,
            /*expectedPruneBelow:*/ 1UL,
            /*finalCutoff:*/ 0UL
        ).SetName("Rolling_mode_with_retention_larger_than_chain_age_does_not_prune");
    }

    private static IEnumerable<TestCaseData> BalPruningCases()
    {
        // head=100, SlotsPerEpoch=32 → cutoff = 100 - retentionEpochs*32 (clamped at 0)
        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, BalRetentionEpochs = 1, PruningInterval = 0 },
            /*expectedBlocksPointer:*/ 36UL,
            /*expectedBalsPointer:*/ 68UL
        ).SetName("Bals_pruned_past_block_cutoff_when_bal_retention_is_shorter");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, BalRetentionEpochs = 2, PruningInterval = 0 },
            /*expectedBlocksPointer:*/ 36UL,
            /*expectedBalsPointer:*/ 36UL
        ).SetName("Bals_pruned_alongside_blocks_when_retentions_equal");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, BalRetentionEpochs = 2, PruningInterval = 0 },
            /*expectedBlocksPointer:*/ 68UL,
            /*expectedBalsPointer:*/ 68UL
        ).SetName("Bals_forced_forward_when_block_retention_is_shorter");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.UseAncientBarriers, BalRetentionEpochs = 1, PruningInterval = 0 },
            /*expectedBlocksPointer:*/ BeaconGenesisBlockNumber,
            /*expectedBalsPointer:*/ 68UL
        ).SetName("Bals_use_separate_rolling_cutoff_in_ancient_barriers_mode");
    }

    [TestCaseSource(nameof(BalPruningCases))]
    public async Task Bal_pruning_uses_separate_cutoff(
        IHistoryConfig historyConfig,
        ulong expectedBlocksPointer,
        ulong expectedBalsPointer)
    {
        const int blocks = 100;

        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyPruner.OldestBlockHeader, Is.Not.Null, "OldestBlockHeader should not be null");
            Assert.That(historyPruner.OldestBlockHeader?.Number, Is.EqualTo(expectedBlocksPointer));
            Assert.That(historyPruner.BalsDeletePointer, Is.EqualTo(expectedBalsPointer));
        }
    }

    [TestCase(100UL, 1u, 68UL)]
    [TestCase(100UL, 4u, 0UL)] // negative pre-clamp
    [TestCase(100UL, 0u, 100UL)]
    public async Task Bal_cutoff_block_number_uses_separate_retention(ulong head, uint balRetentionEpochs, ulong expectedCutoff)
    {
        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 1, // intentionally differs from balRetentionEpochs
            BalRetentionEpochs = balRetentionEpochs,
            PruningInterval = 0
        };

        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, (int)head, syncPivot: head);
        IHistoryPruner historyPruner = testBlockchain.Container.Resolve<IHistoryPruner>();

        Assert.That(historyPruner.BalCutoffBlockNumber, Is.EqualTo(expectedCutoff));
    }

    [TestCaseSource(nameof(PruningCases))]
    public async Task Prunes_history(
        IHistoryConfig historyConfig,
        ulong syncPivot,
        bool primeWithOldestRead,
        ulong expectedPruneBelow,
        ulong finalCutoff)
    {
        const int blocks = 100;

        List<Hash256> blockHashes = [];
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: syncPivot, blockHashes: blockHashes);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();

        if (primeWithOldestRead)
            CheckOldestAndCutoff(1, finalCutoff, historyPruner);

        historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);
        for (uint i = 1; i <= blocks; i++)
        {
            if (i < expectedPruneBelow)
                CheckBlockPruned(testBlockchain, blockHashes, i);
            else
                CheckBlockPreserved(testBlockchain, blockHashes, i);
        }

        CheckHeadPreserved(testBlockchain, blocks);
        CheckOldestAndCutoff(expectedPruneBelow, finalCutoff, historyPruner);
    }

    [TestCase(0UL, 0UL)]
    [TestCase(1UL, 32UL)]
    [TestCase(82125UL, 2_628_000UL)] // mainnet EIP-4444 default
    public async Task GetRetentionBlocks_converts_epochs_to_blocks(ulong retentionEpochs, ulong expected)
    {
        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Disabled, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks: 0);
        IHistoryPruner historyPruner = testBlockchain.Container.Resolve<IHistoryPruner>();

        Assert.That(historyPruner.GetRetentionBlocks(retentionEpochs), Is.EqualTo(expected));
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

        for (uint i = 1; i <= blocks; i++)
        {
            CheckBlockPreserved(testBlockchain, blockHashes, i);
        }

        CheckHeadPreserved(testBlockchain, blocks);
    }

    [TestCase(0UL, 100000u, 0UL, 3533u, false)]
    [TestCase(100UL, 10u, 0UL, 3533u, true)]      // block retention below min
    [TestCase(0UL, 100000u, 3533UL, 3000u, true)] // BAL retention below min
    [TestCase(0UL, 100000u, 3533UL, 3533u, false)] // BAL retention exactly at min
    public void Validates_config(ulong minHistoryRetentionEpochs, uint retentionEpochs, ulong minBalRetentionEpochs, uint balRetentionEpochs, bool shouldThrow)
    {
        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = retentionEpochs,
            BalRetentionEpochs = balRetentionEpochs,
        };
        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec
        {
            MinHistoryRetentionEpochs = minHistoryRetentionEpochs,
            MinBalRetentionEpochs = minBalRetentionEpochs,
        });
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(new TestMemDb());

        Action action = () => new HistoryPruner(
            Substitute.For<IBlockTree>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<IBlockAccessListStore>(),
            specProvider,
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
        const ulong storedPointer = 50;

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
            Assert.That(testBlockchain.BlockTree.FindBlock(0UL, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block should still exist");
            Assert.That(testBlockchain.BlockTree.FindHeader(0UL, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block header should still exist");
            Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(0UL), Is.Not.Null, "Genesis block info should still exist");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(0UL, genesisHash), Is.True, "Genesis block receipt should still exist");
        }
    }

    private static void CheckHeadPreserved(BasicTestBlockchain testBlockchain, ulong headNumber)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.BestKnownNumber, Is.EqualTo(headNumber), "BestKnownNumber should be maintained");
            Assert.That(testBlockchain.BlockTree.Head?.Number, Is.EqualTo(headNumber), "Head should be maintained");
        }
    }

    private static void CheckBlockPreserved(BasicTestBlockchain testBlockchain, List<Hash256> blockHashes, ulong blockNumber)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.FindBlock(blockNumber, BlockTreeLookupOptions.None), Is.Not.Null, $"Block {blockNumber} should still exist");
            Assert.That(testBlockchain.BlockTree.FindHeader(blockNumber, BlockTreeLookupOptions.None), Is.Not.Null, $"Header {blockNumber} should still exist");
            Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(blockNumber), Is.Not.Null, $"Block info {blockNumber} should still exist");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(blockNumber, blockHashes[(int)blockNumber]), Is.True, $"Receipt for block {blockNumber} should still exist");
        }
    }

    private static void CheckBlockPruned(BasicTestBlockchain testBlockchain, List<Hash256> blockHashes, ulong blockNumber)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.FindBlock(blockNumber, BlockTreeLookupOptions.None), Is.Null, $"Block {blockNumber} should be pruned");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(blockNumber, blockHashes[(int)blockNumber]), Is.False, $"Receipt for block {blockNumber} should be pruned");

            // should still be preserved
            Assert.That(testBlockchain.BlockTree.FindHeader(blockNumber, BlockTreeLookupOptions.None), Is.Not.Null, $"Header {blockNumber} should still exist");
            Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(blockNumber), Is.Not.Null, $"Block info {blockNumber} should still exist");
        }
    }

    private static void CheckOldestAndCutoff(ulong oldest, ulong cutoff, IHistoryPruner historyPruner)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyPruner.CutoffBlockNumber, Is.EqualTo(cutoff));
            Assert.That(historyPruner.OldestBlockHeader, Is.Not.Null, "OldestBlockHeader should not be null");
            Assert.That(historyPruner.OldestBlockHeader?.Number, Is.EqualTo(oldest));
        }
    }

    private static async Task<BasicTestBlockchain> CreateBlockchainWithBlocks(
        IHistoryConfig historyConfig,
        int blocks,
        ulong? syncPivot = null,
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
        public TaskCompletionSource Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
