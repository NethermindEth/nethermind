// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
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
using Nethermind.Core.Test.Builders;
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
    [TestCase(33024UL, 1_056_768UL)]
    [TestCase(82125UL, 2_628_000UL)]
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
            Substitute.For<IHeaderStore>(),
            dbProvider,
            historyConfig,
            BlocksConfig,
            SyncConfig,
            new ProcessExitSource(new()),
            Substitute.For<IBackgroundTaskScheduler>(),
            Substitute.For<IBlockProcessingQueue>(),
            NullPrunedReceiptRetention.Instance,
            LimboLogs.Instance);

        if (shouldThrow)
            Assert.Throws<HistoryPruner.HistoryPrunerException>(action);
        else
            Assert.DoesNotThrow(action);
    }

    [TestCase(null, false, false)]
    [TestCase(5000UL, false, false)]
    [TestCase(1UL, true, false)]
    [TestCase(7000UL, false, true)]
    [TestCase(6800UL, true, true)]
    public void SetDeletePointerToOldestBlock_holds_until_the_ancient_bodies_feed_reaches_its_barrier(ulong? bodyPointer, bool searches, bool rollingPruning)
    {
        TestMemDb metadataDb = new();
        if (bodyPointer is not null)
            metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(bodyPointer.Value).Bytes);
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.SyncPivot.Returns((10_000UL, Keccak.Zero));
        blockTree.Head.Returns(Build.A.Block.WithNumber(10_000UL).TestObject);
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        // With rolling pruning the bodies feed stops at the cutoff (head 10_000 - 100 epochs = 6_800),
        // so a pointer parked there means the download finished, not that it is still descending.
        IHistoryConfig historyConfig = rollingPruning
            ? new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 100, PruningInterval = 0 }
            : new HistoryConfig { Pruning = PruningModes.Disabled, PruningInterval = 0 };

        HistoryPruner pruner = new(
            blockTree,
            Substitute.For<IReceiptStorage>(),
            Substitute.For<IBlockAccessListStore>(),
            new TestSpecProvider(new ReleaseSpec()),
            chainLevels,
            Substitute.For<IHeaderStore>(),
            dbProvider,
            historyConfig,
            BlocksConfig,
            new SyncConfig { FastSync = true, PivotNumber = 10_000, DownloadBodiesInFastSync = true },
            new ProcessExitSource(new()),
            Substitute.For<IBackgroundTaskScheduler>(),
            Substitute.For<IBlockProcessingQueue>(),
            NullPrunedReceiptRetention.Instance,
            LimboLogs.Instance);

        Assert.That(pruner.SetDeletePointerToOldestBlock(), Is.False);

        if (searches)
            chainLevels.Received().LoadLevel(Arg.Any<ulong>());
        else
            chainLevels.DidNotReceive().LoadLevel(Arg.Any<ulong>());
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

    [Test]
    public async Task Reclaim_stops_exactly_at_the_published_boundary()
    {
        const int blocks = 100;
        const ulong expectedBoundary = 36;

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        List<Hash256> blockHashes = [];
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks, blockHashes: blockHashes);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyPruner.OldestBlockHeader?.Number, Is.EqualTo(expectedBoundary));
            Assert.That(testBlockchain.BlockTree.FindBlock(expectedBoundary - 1, BlockTreeLookupOptions.None), Is.Null,
                "the block below the boundary must be gone");
            Assert.That(testBlockchain.BlockTree.FindBlock(expectedBoundary, BlockTreeLookupOptions.None), Is.Not.Null,
                "the block AT the boundary is the oldest the node still announces and must survive the reclaim");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(expectedBoundary, blockHashes[(int)expectedBoundary]), Is.True,
                "its receipts go with it");
        }
    }

    [Test]
    public async Task Sweep_makes_progress_even_when_the_budget_is_already_spent()
    {
        const int blocks = 100;

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        IDb metadataDb = testBlockchain.Container.Resolve<IDbProvider>().MetadataDb;
        metadataDb.Set(MetadataDbKeys.HistoryPruningTxIndexSweepCursor, [1, 2, 3]);

        // The sweep runs last, so it is the pass most likely to find the budget already gone.
        using CancellationTokenSource spent = new();
        spent.Cancel();

        ((HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>()).TryPruneHistory(spent.Token);

        Assert.That(metadataDb.Get(MetadataDbKeys.HistoryPruningTxIndexSweepCursor), Is.Not.EqualTo(new byte[] { 1, 2, 3 }),
            "the seeded cursor was never revisited, so the walk did not start");
    }

    [Test]
    public async Task Reclaim_makes_progress_even_when_the_budget_is_already_spent()
    {
        const int blocks = 100;

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        // What the scheduler hands a pass that waited behind others, the deadline being stamped at enqueue.
        using CancellationTokenSource spent = new();
        spent.Cancel();

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(spent.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyPruner.OldestBlockHeader?.Number, Is.EqualTo(36UL), "the boundary still publishes");
            Assert.That(testBlockchain.BlockTree.FindBlock(1UL, BlockTreeLookupOptions.None), Is.Null,
                "and at least one chunk is reclaimed behind it, or the disk never comes back on a busy node");
        }
    }

    [Test]
    public async Task Reclaim_backlog_survives_a_restart()
    {
        const int blocks = 100;

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        const ulong boundary = 36;

        // What a crash between the two metadata writes leaves. Seeded rather than produced by interrupting a pass,
        // because a chunk spans more heights than a test chain has, so an interrupted pass leaves nothing to resume.
        IDb metadataDb = testBlockchain.Container.Resolve<IDbProvider>().MetadataDb;
        metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(boundary).Bytes);
        metadataDb.Set(MetadataDbKeys.HistoryPruningReclaimCursor, Rlp.Encode(1UL).Bytes);

        NewPrunerOver(testBlockchain).TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            for (ulong number = 1; number < boundary; number++)
            {
                Assert.That(testBlockchain.BlockTree.FindBlock(number, BlockTreeLookupOptions.None), Is.Null,
                    $"block {number} sits below a published boundary with the cursor still behind it - a restart that does not read the cursor back leaves it on disk forever");
            }

            Assert.That(testBlockchain.BlockTree.FindBlock(boundary, BlockTreeLookupOptions.None), Is.Not.Null);
        }
    }

    [Test]
    public async Task Sweep_resumes_after_restart_with_nothing_else_left_to_prune()
    {
        const int blocks = 100;

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        // Blocks and access lists caught up, a sweep left half-finished. The sweep is then the only work owed, so a
        // pass has to be scheduled on the strength of its cursor alone rather than on some other pass being due.
        IDb metadataDb = testBlockchain.Container.Resolve<IDbProvider>().MetadataDb;
        metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(36UL).Bytes);
        metadataDb.Set(MetadataDbKeys.HistoryPruningReclaimCursor, Rlp.Encode(36UL).Bytes);
        metadataDb.Set(MetadataDbKeys.HistoryPruningTxIndexSweepCursor, [1, 2, 3]);

        NewPrunerOver(testBlockchain).TryPruneHistory(CancellationToken.None);

        Assert.That(metadataDb.Get(MetadataDbKeys.HistoryPruningTxIndexSweepCursor), Is.Not.EqualTo(new byte[] { 1, 2, 3 }),
            "the pass never ran, so the half-finished sweep is stranded for the life of the process");
    }

    [Test]
    public async Task Reclaim_cursor_is_persisted_so_a_restart_can_read_it()
    {
        const int blocks = 100;

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        ((HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>()).TryPruneHistory(CancellationToken.None);

        IDb metadataDb = testBlockchain.Container.Resolve<IDbProvider>().MetadataDb;
        byte[] cursor = metadataDb.Get(MetadataDbKeys.HistoryPruningReclaimCursor);

        Assert.That(cursor, Is.Not.Null, "without this write the boundary is durable and the reclaim behind it is not");
        Assert.That(new RlpReader(cursor).DecodeULong(), Is.EqualTo(36UL));
    }

    [Test]
    public async Task Reclaim_on_a_database_with_no_cursor_starts_level_with_the_published_boundary()
    {
        const int blocks = 100;
        const ulong storedBoundary = 50;

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        // A database pruned by the per-block code has a boundary and no cursor, and everything below it is gone.
        IDb metadataDb = testBlockchain.Container.Resolve<IDbProvider>().MetadataDb;
        metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(storedBoundary).Bytes);

        NewPrunerOver(testBlockchain).TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.FindBlock(1UL, BlockTreeLookupOptions.None), Is.Not.Null,
                "the cursor defaulted below the boundary and reclaimed ground the boundary never covered");
            Assert.That(testBlockchain.BlockTree.FindBlock(storedBoundary - 1, BlockTreeLookupOptions.None), Is.Not.Null);
        }
    }

    [Test]
    public async Task Sweep_cursor_completing_a_cycle_reads_back_like_a_missing_key()
    {
        const int blocks = 100;

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        IDb metadataDb = testBlockchain.Container.Resolve<IDbProvider>().MetadataDb;
        metadataDb.Set(MetadataDbKeys.HistoryPruningTxIndexSweepCursor, [1, 2, 3]);

        ((HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>()).TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            // Reaching the end stores an empty value rather than removing the key; both have to mean "start over".
            Assert.That(metadataDb.Get(MetadataDbKeys.HistoryPruningTxIndexSweepCursor), Is.Not.Null.And.Empty);
            Assert.That(() => NewPrunerOver(testBlockchain).TryPruneHistory(CancellationToken.None), Throws.Nothing);
        }
    }

    [Test]
    public async Task Cleanup_reclaims_a_height_a_bounded_slice_retained_once_its_window_has_moved_past_it()
    {
        const int blocks = 100;
        const ulong retainedHeight = 10;

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        MutableRetention retention = new();
        retention.Retained.Add(retainedHeight);
        HistoryPruner pruner = NewPrunerOver(testBlockchain, retention);

        pruner.TryPruneHistory(CancellationToken.None);
        Assert.That(testBlockchain.BlockTree.FindBlock(retainedHeight, BlockTreeLookupOptions.None), Is.Not.Null,
            "while inside the slice window the height keeps its body");

        retention.Retained.Clear();
        retention.ExpiredUpperBound = 20;
        pruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.FindBlock(retainedHeight, BlockTreeLookupOptions.None), Is.Null,
                "once the slice window has moved past it, the cleanup cursor reclaims what the main cursor never revisits");
            IDb metadataDb = testBlockchain.Container.Resolve<IDbProvider>().MetadataDb;
            Assert.That(metadataDb.Get(MetadataDbKeys.HistoryPruningSliceCleanupCursor), Is.Not.Null,
                "the cleanup cursor survives a restart or it re-tombstones the same ground forever");
        }
    }

    [Test]
    public async Task Sweep_lookup_falls_back_to_the_header_bloom_where_the_retention_cannot_answer()
    {
        const int blocks = 20;
        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        NeverAnsweringRetention retention = new(retainedHeight: 5);
        HistoryPruner pruner = NewPrunerOver(testBlockchain, retention);
        pruner.TryPruneHistory(CancellationToken.None);
        Func<ulong, bool> lookup = pruner.SweepRetentionLookup();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookup(5), Is.True, "an unanswered height falls back to the header check instead of being retained wholesale");
            Assert.That(lookup(6), Is.False, "an unanswered height the header check declines is swept, not kept forever");
        }
    }

    private sealed class NeverAnsweringRetention(ulong retainedHeight) : IPrunedReceiptRetention
    {
        public bool ShouldRetainReceipts(BlockHeader header) => header.Number == retainedHeight;

        public IReadOnlySet<ulong> RetainedHeights(ulong fromInclusive, ulong toExclusive, out ulong answeredFrom, out ulong answeredTo)
        {
            answeredFrom = fromInclusive;
            answeredTo = fromInclusive;
            return new HashSet<ulong>();
        }
    }

    [Test]
    public async Task Pruning_hands_the_retention_the_receipts_frontier_not_the_bodies_one()
    {
        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 0 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, 100, syncPivot: 100);

        IDb receiptsDefault = testBlockchain.Container.Resolve<IDbProvider>().ReceiptsDb.GetColumnDb(ReceiptsColumns.Default);
        receiptsDefault.Set(Keccak.Zero, Rlp.Encode(60UL).Bytes);

        FrontierCapturingRetention retention = new();
        HistoryPruner pruner = NewPrunerOver(testBlockchain, retention);
        pruner.TryPruneHistory(CancellationToken.None);

        Assert.That(retention.OldestStoredReceipts, Is.EqualTo(60UL),
            "the stamp floor must follow the receipt backfill's own pointer, not the bodies frontier the delete pointer measures");
    }

    [Test]
    public async Task Stamps_are_validated_on_the_first_call_after_startup_not_the_first_interval_boundary()
    {
        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 2, PruningInterval = 1000 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, 100, syncPivot: 100);

        IDb receiptsDefault = testBlockchain.Container.Resolve<IDbProvider>().ReceiptsDb.GetColumnDb(ReceiptsColumns.Default);
        receiptsDefault.Set(Keccak.Zero, Rlp.Encode(60UL).Bytes);

        FrontierCapturingRetention retention = new();
        HistoryPruner pruner = NewPrunerOver(testBlockchain, retention);
        _ = pruner.OldestBlockHeader;
        pruner.TryPruneHistory(CancellationToken.None);

        Assert.That(retention.OldestStoredReceipts, Is.EqualTo(60UL),
            "the read side refuses every sliced address until the stamps are validated, so this must run on the first tick even when another caller loaded the pointers first and no interval boundary has work");
    }

    private sealed class FrontierCapturingRetention : IPrunedReceiptRetention
    {
        public ulong OldestStoredReceipts;

        public bool ShouldRetainReceipts(BlockHeader header) => false;

        public IReadOnlySet<ulong> RetainedHeights(ulong fromInclusive, ulong toExclusive, out ulong answeredFrom, out ulong answeredTo)
        {
            answeredFrom = fromInclusive;
            answeredTo = toExclusive;
            return new HashSet<ulong>();
        }

        public void OnPruningPassStarting(ulong oldestStoredReceipts, ulong reclaimedThrough, ulong sliceCleanupThrough)
            => OldestStoredReceipts = oldestStoredReceipts;
    }

    private sealed class MutableRetention : IPrunedReceiptRetention
    {
        public readonly HashSet<ulong> Retained = [];
        public ulong ExpiredUpperBound;

        public bool ShouldRetainReceipts(BlockHeader header) => Retained.Contains(header.Number);

        public IReadOnlySet<ulong> RetainedHeights(ulong fromInclusive, ulong toExclusive, out ulong answeredFrom, out ulong answeredTo)
        {
            answeredFrom = fromInclusive;
            answeredTo = toExclusive;
            HashSet<ulong> answer = [];
            foreach (ulong height in Retained)
            {
                if (height >= fromInclusive && height < toExclusive) answer.Add(height);
            }

            return answer;
        }

        public ulong ExpiredRetentionUpperBound() => ExpiredUpperBound;
    }

    private static HistoryPruner NewPrunerOver(BasicTestBlockchain testBlockchain, IPrunedReceiptRetention retention = null) => new(
        testBlockchain.Container.Resolve<IBlockTree>(),
        testBlockchain.Container.Resolve<IReceiptStorage>(),
        testBlockchain.Container.Resolve<IBlockAccessListStore>(),
        testBlockchain.Container.Resolve<ISpecProvider>(),
        testBlockchain.Container.Resolve<IChainLevelInfoRepository>(),
        testBlockchain.Container.Resolve<IHeaderStore>(),
        testBlockchain.Container.Resolve<IDbProvider>(),
        testBlockchain.Container.Resolve<IHistoryConfig>(),
        testBlockchain.Container.Resolve<IBlocksConfig>(),
        testBlockchain.Container.Resolve<ISyncConfig>(),
        testBlockchain.Container.Resolve<IProcessExitSource>(),
        testBlockchain.Container.Resolve<IBackgroundTaskScheduler>(),
        testBlockchain.Container.Resolve<IBlockProcessingQueue>(),
        retention ?? testBlockchain.Container.Resolve<IPrunedReceiptRetention>(),
        LimboLogs.Instance);

    [Test]
    public async Task Reclaim_never_touches_genesis_or_the_sync_pivot()
    {
        const int blocks = 100;
        const ulong syncPivot = 20;

        // Retention low enough that the cutoff lands above the pivot, so only the pivot clamp holds it back.
        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
        List<Hash256> blockHashes = [];
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: syncPivot, blockHashes: blockHashes);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            CheckGenesisPreserved(testBlockchain, blockHashes[0]);
            Assert.That(testBlockchain.BlockTree.FindBlock(syncPivot, BlockTreeLookupOptions.None), Is.Not.Null,
                "the sync pivot is the floor of what re-execution can start from and must never be reclaimed");
            Assert.That(historyPruner.OldestBlockHeader?.Number, Is.LessThanOrEqualTo(syncPivot));
        }
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
        bc.Container.Resolve<IDbProvider>().MetadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(1UL).Bytes);
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
        // n.b. in prod MinHistoryRetentionEpochs should be 33024, however not feasible to test this
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
