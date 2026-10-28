// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.TxPool;
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
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 },
            /*syncPivot:*/ blocks,
            /*primeWithOldestRead:*/ true,
            /*expectedPruneBelow:*/ 36UL,
            /*finalCutoff:*/ 36UL
        ).SetName("Can_prune_blocks_older_than_specified_epochs");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 },
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
            /*finalCutoff:*/ 20UL
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
        // head=100, SlotsPerEpoch=32 → block cutoff = 100 - (retentionEpochs + 1)*32, BAL cutoff = 100 - (balRetentionEpochs + 1)*32 (clamped at 0)
        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, BalRetentionEpochs = 0, PruningInterval = 0 },
            /*expectedBlocksPointer:*/ 36UL,
            /*expectedBalsPointer:*/ 68UL
        ).SetName("Bals_pruned_past_block_cutoff_when_bal_retention_is_shorter");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, BalRetentionEpochs = 1, PruningInterval = 0 },
            /*expectedBlocksPointer:*/ 36UL,
            /*expectedBalsPointer:*/ 36UL
        ).SetName("Bals_pruned_alongside_blocks_when_cutoffs_equal");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 0, BalRetentionEpochs = 1, PruningInterval = 0 },
            /*expectedBlocksPointer:*/ 68UL,
            /*expectedBalsPointer:*/ 68UL
        ).SetName("Bals_forced_forward_when_block_retention_is_shorter");

        yield return new TestCaseData(
            new HistoryConfig { Pruning = PruningModes.UseAncientBarriers, BalRetentionEpochs = 0, PruningInterval = 0 },
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
            Assert.That(historyPruner.BalCutoffBlockNumber, Is.GreaterThanOrEqualTo(expectedBalsPointer));
        }
    }

    private const ulong BalOnlyHead = 500_000;

    // Head 500_000, plus one epoch of slack: the 14299-epoch chain floor keeps 457_600 blocks, a one-epoch retention keeps 64.
    [TestCase(null, PruningMode.Hybrid, true, 42_400UL)]
    [TestCase(1u, PruningMode.Hybrid, true, 499_936UL)]
    [TestCase(uint.MaxValue, PruningMode.Hybrid, true, 0UL)] // keeps them all
    [TestCase(null, PruningMode.None, true, 42_400UL)]
    [TestCase(1u, PruningMode.None, true, 499_936UL)]
    [TestCase(uint.MaxValue, PruningMode.None, true, 0UL)]
    [TestCase(null, PruningMode.Hybrid, false, null)]
    [TestCase(null, PruningMode.None, false, null)]
    public void Bals_are_pruned_when_history_pruning_is_disabled(uint? balRetentionEpochs, PruningMode stateMode, bool chainHasAccessLists, ulong? expectedCutoff)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        using IContainer container = CreateBalOnlyContainer(blockTree, receiptStorage, balStore, balRetentionEpochs, stateMode, chainHasAccessLists);
        HistoryPruner pruner = (HistoryPruner)container.Resolve<IHistoryPruner>();
        IDb metadataDb = container.Resolve<IDbProvider>().MetadataDb;

        pruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruner.BalCutoffBlockNumber, Is.EqualTo(expectedCutoff));
            Assert.That(pruner.CutoffBlockNumber, Is.Null);
            if (expectedCutoff is > 1 and { } cutoff)
            {
                balStore.Received(1).DeleteRange(1, cutoff);
                Assert.That(pruner.BalsDeletePointer, Is.EqualTo(cutoff));
                Assert.That(new RlpReader(metadataDb.Get(MetadataDbKeys.BlockAccessListPruningDeletePointer)!).DecodeULong(), Is.EqualTo(cutoff));
            }
            else
            {
                balStore.DidNotReceive().DeleteRange(Arg.Any<ulong>(), Arg.Any<ulong>());
            }

            receiptStorage.DidNotReceive().RemoveReceiptsRange(Arg.Any<ulong>(), Arg.Any<ulong>());
            blockTree.DidNotReceive().DeleteOldBlockRange(Arg.Any<ulong>(), Arg.Any<ulong>());
            Assert.That(metadataDb.KeyExists(MetadataDbKeys.HistoryPruningDeletePointer), Is.False);
        }
    }

    [Test]
    public void Bal_only_pruning_resumes_from_persisted_pointer()
    {
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        using IContainer container = CreateBalOnlyContainer(Substitute.For<IBlockTree>(), Substitute.For<IReceiptStorage>(), balStore);
        container.Resolve<IDbProvider>().MetadataDb.Set(MetadataDbKeys.BlockAccessListPruningDeletePointer, Rlp.Encode(30_000UL).Bytes);
        HistoryPruner pruner = (HistoryPruner)container.Resolve<IHistoryPruner>();

        pruner.TryPruneHistory(CancellationToken.None);

        balStore.Received(1).DeleteRange(30_000, 42_400);
        balStore.DidNotReceive().DeleteRange(Arg.Is<ulong>(static from => from < 30_000), Arg.Any<ulong>());
    }

    [Test]
    public void Bal_admission_floor_survives_rewind_before_pruning_pointer_is_saved()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        using IContainer container = CreateBalOnlyContainer(blockTree, Substitute.For<IReceiptStorage>(), balStore);
        HistoryPruner pruner = (HistoryPruner)container.Resolve<IHistoryPruner>();
        balStore.When(static store => store.DeleteRange(1, 42_400)).Do(_ =>
        {
            blockTree.Head.Returns(Build.A.Block.WithNumber(BalOnlyHead - 1).TestObject);
            Assert.That(pruner.BalCutoffBlockNumber, Is.EqualTo(42_400UL));
        });

        pruner.TryPruneHistory(CancellationToken.None);

        Assert.That(pruner.BalCutoffBlockNumber, Is.EqualTo(42_400UL));
    }

    [Test]
    public void Delayed_bal_response_after_pruning_and_head_rewind_is_skipped()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        balStore.WriteLock.Returns(new Lock());
        using IContainer container = CreateBalOnlyContainer(blockTree, Substitute.For<IReceiptStorage>(), balStore, balRetentionEpochs: 0);
        blockTree.Head.Returns(Build.A.Block.WithNumber(32).TestObject);
        blockTree.SyncPivot.Returns((3UL, TestItem.KeccakA));
        HistoryPruner pruner = (HistoryPruner)container.Resolve<IHistoryPruner>();
        byte[] encodedBal = [0xc0];
        BlockHeader header = Build.A.BlockHeader.WithNumber(2).WithBlockAccessListHash(Keccak.Compute(encodedBal)).TestObject;
        blockTree.FindHeader(header.Hash!, blockNumber: 2).Returns(header);
        SyncConfig syncConfig = new() { FastSync = true, PivotNumber = 3 };
        using ILifetimeScope scope = container.BeginLifetimeScope(builder => builder
            .AddModule(new SynchronizerModule(syncConfig))
            .AddSingleton<ISyncConfig>(syncConfig)
            .AddSingleton(Substitute.For<ISyncPointers>())
            .AddSingleton(Substitute.For<ISyncPeerPool>())
            .AddSingleton<ISyncReport>(new NullSyncReport()));
        BlockAccessListsSyncFeed feed = (BlockAccessListsSyncFeed)scope.Resolve<ISyncFeed<BlockAccessListsSyncBatch>>();
        feed.InitializeFeed();
        using BlockAccessListsSyncBatch batch = new([new BlockInfo(header.Hash!, 0) { BlockNumber = 2 }])
        {
            Response = new ArrayPoolList<byte[]>([encodedBal])
        };

        blockTree.Head.Returns(Build.A.Block.WithNumber(35).TestObject);
        pruner.TryPruneHistory(CancellationToken.None);
        blockTree.Head.Returns(Build.A.Block.WithNumber(34).TestObject);
        feed.HandleResponse(batch);

        using (Assert.EnterMultipleScope())
        {
            balStore.Received(1).DeleteRange(1, 3);
            balStore.DidNotReceive().Insert(2UL, header.Hash!, Arg.Any<byte[]>());
        }
    }

    [Test]
    public void Persisted_bal_admission_floor_respects_chain_support_before_first_pruning_pass([Values] bool chainHasAccessLists)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        using IContainer container = CreateBalOnlyContainer(blockTree, Substitute.For<IReceiptStorage>(), balStore, chainHasAccessLists: chainHasAccessLists);
        container.Resolve<IDbProvider>().MetadataDb.Set(MetadataDbKeys.BlockAccessListPruningDeletePointer, Rlp.Encode(42_400UL).Bytes);
        blockTree.Head.Returns(Build.A.Block.WithNumber(BalOnlyHead - 1).TestObject);

        HistoryPruner pruner = (HistoryPruner)container.Resolve<IHistoryPruner>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruner.BalCutoffBlockNumber, Is.EqualTo(chainHasAccessLists ? 42_400UL : (ulong?)null));
            balStore.DidNotReceive().DeleteRange(Arg.Any<ulong>(), Arg.Any<ulong>());
        }
    }

    [Test]
    public void Bal_only_pruning_stops_at_the_sync_pivot()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        using IContainer container = CreateBalOnlyContainer(blockTree, Substitute.For<IReceiptStorage>(), balStore, balRetentionEpochs: 1);
        HistoryPruner pruner = (HistoryPruner)container.Resolve<IHistoryPruner>();
        blockTree.SyncPivot.Returns((400_000UL, Keccak.Zero));

        pruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            balStore.Received(1).DeleteRange(1, 400_000);
            Assert.That(pruner.BalsDeletePointer, Is.EqualTo(400_000UL));
        }
    }

    [Test]
    public async Task Scheduled_pass_prunes_bals_when_history_pruning_is_disabled()
    {
        TaskCompletionSource deleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        balStore.When(static s => s.DeleteRange(Arg.Any<ulong>(), Arg.Any<ulong>())).Do(_ => deleted.TrySetResult());
        await using BackgroundTaskScheduler scheduler = new(Substitute.For<IBranchProcessor>(), Substitute.For<IChainHeadInfoProvider>(), 1, 16, LimboLogs.Instance);
        using IContainer container = CreateBalOnlyContainer(Substitute.For<IBlockTree>(), Substitute.For<IReceiptStorage>(), balStore, scheduler: scheduler);
        HistoryPruner pruner = (HistoryPruner)container.Resolve<IHistoryPruner>();

        pruner.SchedulePruneHistory();

        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        balStore.Received(1).DeleteRange(1, 42_400);
    }

    [TestCase(HistoryRetentionConstants.HistoryPruneEpochs, 42_400UL)]
    [TestCase(15_000UL, 19_968UL)]
    public void Default_bal_retention_is_the_chain_floor(ulong minBalRetentionEpochs, ulong expectedCutoff)
    {
        using IContainer container = CreateBalOnlyContainer(Substitute.For<IBlockTree>(), Substitute.For<IReceiptStorage>(), Substitute.For<IBlockAccessListStore>(), minBalRetentionEpochs: minBalRetentionEpochs);
        HistoryPruner pruner = (HistoryPruner)container.Resolve<IHistoryPruner>();

        Assert.That(pruner.BalCutoffBlockNumber, Is.EqualTo(expectedCutoff));
    }

    private static IContainer CreateBalOnlyContainer(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        IBlockAccessListStore balStore,
        uint? balRetentionEpochs = null,
        PruningMode stateMode = PruningMode.Hybrid,
        bool chainHasAccessLists = true,
        IBackgroundTaskScheduler scheduler = null,
        ulong minBalRetentionEpochs = HistoryRetentionConstants.HistoryPruneEpochs)
    {
        blockTree.SyncPivot.Returns((BalOnlyHead, Keccak.Zero));
        blockTree.Head.Returns(Build.A.Block.WithNumber(BalOnlyHead).TestObject);
        // Lowered to an explicit retention so short windows pass the floor check.
        minBalRetentionEpochs = ulong.Min(minBalRetentionEpochs, balRetentionEpochs ?? ulong.MaxValue);
        return new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new HistoryConfig { Pruning = PruningModes.Disabled, BalRetentionEpochs = balRetentionEpochs, PruningInterval = 0 },
                new PruningConfig { Mode = stateMode },
                BlocksConfig,
                SyncConfig))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(new ReleaseSpec { IsEip7928Enabled = chainHasAccessLists, MinBalRetentionEpochs = minBalRetentionEpochs }))
            .AddSingleton(blockTree)
            .AddSingleton(receiptStorage)
            .AddSingleton(balStore)
            .AddSingleton(scheduler ?? Substitute.For<IBackgroundTaskScheduler>())
            .AddSingleton(Substitute.For<IBlockProcessingQueue>())
            .AddSingleton<IPrunedReceiptRetention>(NullPrunedReceiptRetention.Instance)
            .Build();
    }

    [TestCase(100UL, 1u, 36UL)]
    [TestCase(100UL, 4u, 0UL)] // negative pre-clamp
    [TestCase(100UL, 0u, 68UL)] // the extra epoch of slack
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
            RetentionEpochs = 1,
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

    [TestCase(PruningModes.Disabled, 100UL)]
    [TestCase(PruningModes.Rolling, 0UL)]
    public async Task Does_not_prune_when_pruning_is_disabled_or_sync_pivot_is_missing(PruningModes pruning, ulong syncPivot)
    {
        const int blocks = 100;

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = pruning,
            RetentionEpochs = 2
        };
        List<Hash256> blockHashes = [];
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: syncPivot, blockHashes: blockHashes);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        CheckGenesisPreserved(testBlockchain, blockHashes[0]);

        for (uint i = 1; i <= blocks; i++)
        {
            CheckBlockPreserved(testBlockchain, blockHashes, i);
        }

        CheckHeadPreserved(testBlockchain, blocks);
    }

    [Test]
    public async Task Initial_pruning_pass_remains_pending_after_missing_sync_pivot()
    {
        const int blocks = 100;

        HistoryConfig historyConfig = new() { Pruning = PruningModes.Rolling, RetentionEpochs = 2 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: 0);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        testBlockchain.BlockTree.SyncPivot = (blocks, Hash256.Zero);
        historyPruner.TryPruneHistory(CancellationToken.None);

        Assert.That(testBlockchain.BlockTree.FindBlock(1UL, BlockTreeLookupOptions.None), Is.Null,
            "a missing sync pivot must not consume the initial pass's interval bypass");
    }

    [Test]
    public async Task Initial_pruning_pass_bypasses_interval_once()
    {
        const int blocks = 100;
        const ulong initialCutoff = 36;
        const ulong laterCutoff = 68;

        HistoryConfig historyConfig = new() { Pruning = PruningModes.Rolling, RetentionEpochs = 1 };
        using BasicTestBlockchain testBlockchain = await CreateBlockchainWithBlocks(historyConfig, blocks, syncPivot: blocks);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyPruner.OldestBlockHeader?.Number, Is.EqualTo(initialCutoff));
            Assert.That(testBlockchain.BlockTree.FindBlock(initialCutoff - 1, BlockTreeLookupOptions.None), Is.Null);
        }

        historyConfig.RetentionEpochs = 0;
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyPruner.CutoffBlockNumber, Is.EqualTo(laterCutoff));
            Assert.That(historyPruner.OldestBlockHeader?.Number, Is.EqualTo(initialCutoff));
            Assert.That(testBlockchain.BlockTree.FindBlock(50UL, BlockTreeLookupOptions.None), Is.Not.Null,
                "a same-head pass remains interval-throttled after the initial pass");
        }
    }

    // Cutoff = head - (retention + 1) * 32: one epoch of slack below the CL's serving window.
    [TestCase(33024UL, null, 2_000_000UL, 33024UL, 943_200UL)] // the chain floor
    [TestCase(15000UL, null, 2_000_000UL, 15000UL, 1_519_968UL)] // another floor
    [TestCase(33024UL, 40000u, 2_000_000UL, 40000UL, 719_968UL)]
    [TestCase(33024UL, null, 2_000_031UL, 33024UL, 943_231UL)] // last slot of an epoch
    public void Rolling_retention_defaults_to_the_chain_floor(ulong floor, uint? retentionEpochs, ulong head, ulong expectedEpochs, ulong expectedCutoff)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(head).TestObject);

        HistoryPruner pruner = CreatePruner(
            blockTree,
            new TestSpecProvider(new ReleaseSpec { MinHistoryRetentionEpochs = floor }),
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = retentionEpochs });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruner.RetentionEpochs, Is.EqualTo(expectedEpochs));
            Assert.That(pruner.CutoffBlockNumber, Is.EqualTo(expectedCutoff));
            // The CL serves from the start of epoch (head epoch - N).
            Assert.That(pruner.CutoffBlockNumber, Is.LessThanOrEqualTo((head / 32 - expectedEpochs) * 32));
        }
    }

    [Test]
    public void Ancient_barriers_cutoff_follows_the_tree_pivot([Values(0UL, 100UL)] ulong configPivotNumber)
    {
        // A pivot supplied by the consensus layer reaches only the block tree, after the pruner is built, and never the config.
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.SyncPivot.Returns((configPivotNumber, TestItem.KeccakA));
        blockTree.Head.Returns(Build.A.Block.WithNumber(200).TestObject);
        SyncConfig syncConfig = new()
        {
            FastSync = true,
            SnapSync = true,
            PivotNumber = configPivotNumber,
            AncientBodiesBarrier = BeaconGenesisBlockNumber,
            AncientReceiptsBarrier = BeaconGenesisBlockNumber + 30,
        };

        HistoryPruner pruner = CreatePruner(
            blockTree,
            new TestSpecProvider(new ReleaseSpec()),
            new HistoryConfig { Pruning = PruningModes.UseAncientBarriers, PruningInterval = 0 },
            syncConfig: syncConfig);
        blockTree.SyncPivot.Returns((100UL, TestItem.KeccakA));

        // The fast-blocks feeds stop at max(1, min(tree pivot, barrier)), so blocks below it are the ones to prune.
        Assert.That(pruner.CutoffBlockNumber, Is.EqualTo(BeaconGenesisBlockNumber));
    }

    [Test]
    public void Geth_retention_upper_bound_preserves_history([Values("4294967295", "\"0xffffffff\"")] string retentionEpochs)
    {
        using MemoryStream genesis = new(Encoding.UTF8.GetBytes($$"""
            { "config": { "chainId": 1, "minHistoryRetentionEpochs": {{retentionEpochs}} }, "difficulty": "0x1", "gasLimit": "0x8000000", "alloc": {} }
            """));
        ChainSpecBasedSpecProvider specProvider = new(new GethGenesisLoader(new EthereumJsonSerializer()).Load(genesis));
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(2_000_000).TestObject);

        HistoryPruner pruner = CreatePruner(blockTree, specProvider, new HistoryConfig { Pruning = PruningModes.Rolling });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruner.RetentionEpochs, Is.EqualTo((ulong)uint.MaxValue));
            Assert.That(pruner.GetRetentionBlocks(pruner.RetentionEpochs), Is.EqualTo(137_438_953_440UL));
            Assert.That(pruner.CutoffBlockNumber, Is.Zero);
        }
    }

    [TestCase(0UL, 100000u, 0UL, 3533u, false)]
    [TestCase(100UL, 10u, 0UL, 3533u, true)]      // block retention below min
    [TestCase(0UL, 100000u, 3533UL, 3000u, true)] // BAL retention below min
    [TestCase(0UL, 100000u, 3533UL, 3533u, false)] // BAL retention exactly at min
    [TestCase(0UL, 100000u, 14299UL, null, false)] // unset BAL retention defaults to the chain floor
    [TestCase(0UL, 100000u, 20000UL, null, false)]
    [TestCase(14299UL, null, 0UL, 3533u, false)] // unset block retention defaults to the floor
    [TestCase(14299UL, 14298u, 0UL, 3533u, true)]
    public void Validates_config(ulong minHistoryRetentionEpochs, uint? retentionEpochs, ulong minBalRetentionEpochs, uint? balRetentionEpochs, bool shouldThrow)
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
        Action action = () => CreatePruner(Substitute.For<IBlockTree>(), specProvider, historyConfig);

        if (shouldThrow)
            Assert.Throws<HistoryPruner.HistoryPrunerException>(action);
        else
            Assert.DoesNotThrow(action);
    }

    private static HistoryPruner CreatePruner(IBlockTree blockTree, ISpecProvider specProvider, IHistoryConfig historyConfig, ILogManager logManager = null, ISyncConfig syncConfig = null)
    {
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(new TestMemDb());

        return new HistoryPruner(
            blockTree,
            Substitute.For<IReceiptStorage>(),
            Substitute.For<IBlockAccessListStore>(),
            specProvider,
            Substitute.For<IChainLevelInfoRepository>(),
            Substitute.For<IHeaderStore>(),
            dbProvider,
            historyConfig,
            BlocksConfig,
            syncConfig ?? SyncConfig,
            new ProcessExitSource(new()),
            Substitute.For<IBackgroundTaskScheduler>(),
            Substitute.For<IBlockProcessingQueue>(),
            NullPrunedReceiptRetention.Instance,
            logManager ?? LimboLogs.Instance);
    }

    [TestCase(null, true, true)]
    [TestCase(100u, true, false)]
    [TestCase(uint.MaxValue, true, false)]
    [TestCase(null, false, false)]
    public void Bal_only_default_retention_is_logged_before_pruning(uint? retentionEpochs, bool chainHasAccessLists, bool logs)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        ILogger wrappedLogger = new(logger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<HistoryPruner>().Returns(wrappedLogger);

        CreatePruner(
            Substitute.For<IBlockTree>(),
            new TestSpecProvider(new ReleaseSpec { IsEip7928Enabled = chainHasAccessLists, MinBalRetentionEpochs = 100 }),
            new HistoryConfig { Pruning = PruningModes.Disabled, BalRetentionEpochs = retentionEpochs },
            logManager);

        logger.Received(logs ? 1 : 0).Info("Pruning block access lists older than 100 epochs. Set History.BalRetentionEpochs to 4294967295 to keep all of them.");
    }

    // No pointer is persisted until the first pass, so a node switching to Rolling falls back to the oldest stored block.
    [TestCase(3_000_000UL, 0UL, true, TestName = "Rolling_default_warns_about_a_backlog_when_no_pointer_is_persisted")]
    [TestCase(2_000_000UL, 0UL, false, TestName = "Rolling_default_does_not_warn_about_less_than_a_window")]
    [TestCase(3_000_000UL, 1_000_000UL, false, TestName = "Rolling_default_does_not_warn_below_the_lowest_stored_block")]
    public void Rolling_default_warns_about_a_backlog_when_no_pointer_is_persisted(ulong head, ulong lowestBlock, bool warns)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(head).TestObject);
        blockTree.GetLowestBlock().Returns(lowestBlock);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        ILogger wrappedLogger = new(logger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<HistoryPruner>().Returns(wrappedLogger);

        CreatePruner(
            blockTree,
            new TestSpecProvider(new ReleaseSpec { MinHistoryRetentionEpochs = 33024 }),
            new HistoryConfig { Pruning = PruningModes.Rolling },
            logManager);

        logger.Received(warns ? 1 : 0).Warn(Arg.Is<string>(m => m.Contains("History.RetentionEpochs is not set")));
    }

    [TestCase(null, false, TestName = "SetDeletePointerToOldestBlock_never_searches_while_no_body_was_inserted")]
    [TestCase(7000UL, false, TestName = "SetDeletePointerToOldestBlock_holds_above_the_static_barrier")]
    [TestCase(1UL, true, TestName = "SetDeletePointerToOldestBlock_releases_at_the_static_barrier")]
    public void SetDeletePointerToOldestBlock_holds_while_the_ancient_bodies_feed_is_descending(ulong? bodyPointer, bool searches)
    {
        TestMemDb metadataDb = new();
        if (bodyPointer is not null)
            metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(bodyPointer.Value).Bytes);
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels);

        for (int i = 0; i < 3; i++)
        {
            pruner.SetDeletePointerToOldestBlock();
        }

        if (searches)
            chainLevels.Received().LoadLevel(Arg.Any<ulong>());
        else
            chainLevels.DidNotReceive().LoadLevel(Arg.Any<ulong>());
    }

    [Test]
    public void SetDeletePointerToOldestBlock_releases_when_the_feed_marked_its_backfill_complete()
    {
        TestMemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(7000UL).Bytes);
        metadataDb.Set(MetadataDbKeys.AncientBodiesDownloadComplete, [1]);
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels);

        pruner.SetDeletePointerToOldestBlock();
        chainLevels.Received().LoadLevel(Arg.Any<ulong>());
    }

    [Test]
    public void SetDeletePointerToOldestBlock_releases_a_pointer_parked_at_the_barrier_the_feed_last_started_with()
    {
        TestMemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(5000UL).Bytes);
        metadataDb.Set(MetadataDbKeys.BodiesBarrierWhenStarted, ((long)5000).ToBigEndianByteArrayWithoutLeadingZeros());
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels);

        pruner.SetDeletePointerToOldestBlock();
        chainLevels.Received().LoadLevel(Arg.Any<ulong>());
    }

    [Test]
    public void OldestUnreclaimedBlockNumber_reads_the_persisted_boundary_before_the_pointers_load()
    {
        TestMemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(5_000_000UL).Bytes);
        metadataDb.Set(MetadataDbKeys.HistoryPruningReclaimCursor, Rlp.Encode(4_000_000UL).Bytes);
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels);

        Assert.That(pruner.OldestUnreclaimedBlockNumber, Is.EqualTo(4_000_000UL),
            "before the pointers load, the persisted cursors are the truth, not the config barrier");
    }

    [Test]
    public void OldestUnreclaimedBlockNumber_floors_at_the_configured_barrier_before_the_pointers_load()
    {
        TestMemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(5_000UL).Bytes);
        metadataDb.Set(MetadataDbKeys.HistoryPruningReclaimCursor, Rlp.Encode(4_000UL).Bytes);
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels, lowestBlock: 9_000UL);

        Assert.That(pruner.OldestUnreclaimedBlockNumber, Is.EqualTo(9_000UL),
            "a barrier raised above the persisted cursors refuses more than necessary, transiently");
    }

    [Test]
    public void OldestUnreclaimedBlockNumber_is_the_configured_barrier_when_nothing_was_persisted()
    {
        TestMemDb metadataDb = new();
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels, lowestBlock: 9_000UL);

        Assert.That(pruner.OldestUnreclaimedBlockNumber, Is.EqualTo(9_000UL),
            "with no persisted state the barrier is the whole answer, matching a never-pruned node");
    }

    [Test]
    public void SetDeletePointerToOldestBlock_holds_when_the_barrier_dropped_below_the_one_the_feed_last_started_with()
    {
        TestMemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(7000UL).Bytes);
        metadataDb.Set(MetadataDbKeys.BodiesBarrierWhenStarted, ((long)7000).ToBigEndianByteArrayWithoutLeadingZeros());
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels);

        Assert.That(pruner.SetDeletePointerToOldestBlock(), Is.False,
            "a recorded barrier above the current one describes a finished descent the config since reopened");
        chainLevels.DidNotReceive().LoadLevel(Arg.Any<ulong>());
    }

    [Test]
    public void SetDeletePointerToOldestBlock_never_releases_a_written_pointer_above_the_barrier_without_the_marker()
    {
        TestMemDb metadataDb = new();
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels);

        for (ulong frontier = 9000; frontier > 8995; frontier--)
        {
            metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(frontier).Bytes);
            Assert.That(pruner.SetDeletePointerToOldestBlock(), Is.False);
            Assert.That(pruner.SetDeletePointerToOldestBlock(), Is.False,
                "a written pointer above the static barrier must hold until the completion marker releases it");
        }

        chainLevels.DidNotReceive().LoadLevel(Arg.Any<ulong>());
    }

    [Test]
    public void SetDeletePointerToOldestBlock_releases_a_written_pointer_when_synchronization_is_disabled()
    {
        TestMemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(9000UL).Bytes);
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels, synchronizationEnabled: false);

        pruner.SetDeletePointerToOldestBlock();

        chainLevels.Received().LoadLevel(Arg.Any<ulong>());
    }

    [Test]
    public void A_frontier_frozen_by_disabled_synchronization_is_not_persisted_by_a_pruning_pass()
    {
        (HistoryPruner pruner, TestMemDb metadataDb, IPrunedReceiptRetention retention) = CreateFrontierFixture(synchronizationEnabled: false);

        Assert.That(pruner.OldestBlockHeader?.Number, Is.EqualTo(9_000UL));
        pruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadataDb.KeyExists(MetadataDbKeys.HistoryPruningDeletePointer), Is.False,
                "a frozen frontier must not reach disk through a pruning-path save");
            Assert.That(metadataDb.KeyExists(MetadataDbKeys.HistoryPruningReclaimCursor), Is.False,
                "the frontier-seeded reclaim cursor must not reach disk either");
            Assert.That(metadataDb.KeyExists(MetadataDbKeys.HistoryPruningSliceCleanupCursor), Is.False,
                "the first-save sentinel of the cleanup cursor is pinned on the frozen path too");
            Assert.That(metadataDb.KeyExists(MetadataDbKeys.BlockAccessListPruningDeletePointer), Is.True,
                "the pass itself persists normally");
        }
        retention.DidNotReceive().OnPruningPassStarting(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<ulong>());
    }

    [Test]
    public void Initial_pruning_pass_remains_pending_while_ancient_backfill_is_held()
    {
        (HistoryPruner pruner, TestMemDb metadataDb, IPrunedReceiptRetention retention) = CreateFrontierFixture(
            synchronizationEnabled: true,
            pruningInterval: 256);

        pruner.TryPruneHistory(CancellationToken.None);
        retention.DidNotReceive().OnPruningPassStarting(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<ulong>());
        Assert.That(metadataDb.KeyExists(MetadataDbKeys.HistoryPruningDeletePointer), Is.False,
            "the ancient backfill hold must leave the pruning state unloaded");

        metadataDb.Set(MetadataDbKeys.AncientBodiesDownloadComplete, [1]);
        metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(1UL).Bytes);
        ulong cutoff = pruner.CutoffBlockNumber!.Value;
        retention.RetainedHeights(Arg.Any<ulong>(), Arg.Any<ulong>(), out Arg.Any<ulong>(), out Arg.Any<ulong>())
            .Returns(callInfo =>
            {
                callInfo[2] = callInfo.ArgAt<ulong>(0);
                callInfo[3] = callInfo.ArgAt<ulong>(1);
                return new HashSet<ulong>();
            });
        pruner.TryPruneHistory(CancellationToken.None);

        Assert.That(new RlpReader(metadataDb.Get(MetadataDbKeys.HistoryPruningDeletePointer)!).DecodeULong(),
            Is.EqualTo(cutoff));
        retention.Received().OnPruningPassStarting(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<ulong>());
    }

    [Test]
    public void A_discovered_boundary_is_persisted_by_a_pruning_pass_when_synchronization_is_enabled()
    {
        (HistoryPruner pruner, TestMemDb metadataDb, IPrunedReceiptRetention retention) = CreateFrontierFixture(synchronizationEnabled: true, markerPresent: true);

        Assert.That(pruner.OldestBlockHeader?.Number, Is.EqualTo(9_000UL));
        pruner.TryPruneHistory(CancellationToken.None);

        Assert.That(metadataDb.KeyExists(MetadataDbKeys.HistoryPruningDeletePointer), Is.True,
            "the suppression is scoped to the frozen path, not persistence outright");
        retention.Received().OnPruningPassStarting(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<ulong>());
    }

    private static (HistoryPruner Pruner, TestMemDb MetadataDb, IPrunedReceiptRetention Retention) CreateFrontierFixture(bool synchronizationEnabled, bool markerPresent = false, ulong pruningInterval = 0)
    {
        TestMemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.LowestInsertedBodyNumber, Rlp.Encode(9000UL).Bytes);
        if (markerPresent)
        {
            metadataDb.Set(MetadataDbKeys.AncientBodiesDownloadComplete, [1]);
        }
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadataDb);
        dbProvider.BlocksDb.Returns(new TestMemDb());
        dbProvider.ReceiptsDb.GetColumnDb(Arg.Any<ReceiptsColumns>()).Returns(new TestMemDb());
        IChainLevelInfoRepository chainLevels = Substitute.For<IChainLevelInfoRepository>();
        Block oldest = Build.A.Block.WithNumber(9_000UL).TestObject;
        chainLevels.LoadLevel(Arg.Is<ulong>(static n => n >= 9_000)).Returns(new ChainLevelInfo(true, new BlockInfo(oldest.Hash!, 0)));
        IPrunedReceiptRetention retention = Substitute.For<IPrunedReceiptRetention>();

        HistoryPruner pruner = CreateDetachedPruner(dbProvider, chainLevels, synchronizationEnabled: synchronizationEnabled, oldestBlock: oldest, balRetentionEpochs: 1, retention: retention, pruningInterval: pruningInterval);
        return (pruner, metadataDb, retention);
    }

    private static HistoryPruner CreateDetachedPruner(IDbProvider dbProvider, IChainLevelInfoRepository chainLevels, bool synchronizationEnabled = true, Block oldestBlock = null, uint balRetentionEpochs = 3533, IPrunedReceiptRetention retention = null, ulong lowestBlock = 0, ulong pruningInterval = 0)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.SyncPivot.Returns((10_000UL, Keccak.Zero));
        blockTree.Head.Returns(Build.A.Block.WithNumber(10_000UL).TestObject);
        blockTree.GetLowestBlock().Returns(lowestBlock);
        if (oldestBlock is not null)
        {
            blockTree.FindBlock(Arg.Any<Hash256>(), Arg.Any<ulong?>()).Returns(oldestBlock);
            blockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(oldestBlock.Header);
        }

        return new HistoryPruner(
            blockTree,
            Substitute.For<IReceiptStorage>(),
            Substitute.For<IBlockAccessListStore>(),
            new TestSpecProvider(new ReleaseSpec()),
            chainLevels,
            Substitute.For<IHeaderStore>(),
            dbProvider,
            new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 100, PruningInterval = pruningInterval, BalRetentionEpochs = balRetentionEpochs },
            BlocksConfig,
            new SyncConfig { FastSync = true, PivotNumber = 10_000, DownloadBodiesInFastSync = true, SynchronizationEnabled = synchronizationEnabled },
            new ProcessExitSource(new()),
            Substitute.For<IBackgroundTaskScheduler>(),
            Substitute.For<IBlockProcessingQueue>(),
            retention ?? NullPrunedReceiptRetention.Instance,
            LimboLogs.Instance);
    }

    // Runs through the real scheduler, which replaces a missing timeout with its own 2 s default. The observation
    // window outlasts that default, so a disabled timeout must still leave the pass uncancelled at its end.
    [TestCase(1u, true)]
    [TestCase(0u, false)]
    public async Task SchedulePruneHistory_cancels_the_pass_only_when_the_configured_timeout_elapses(uint pruningTimeoutSeconds, bool expectCancelled)
    {
        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 100000,
            PruningTimeoutSeconds = pruningTimeoutSeconds,
            PruningInterval = 0
        };

        await using BackgroundTaskScheduler realScheduler = new(Substitute.For<IBranchProcessor>(), Substitute.For<IChainHeadInfoProvider>(), 1, 16, LimboLogs.Instance);
        DeadlineObservingScheduler scheduler = new(realScheduler, TimeSpan.FromSeconds(3));
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(BuildContainer(historyConfig, scheduler));

        IHistoryPruner historyPruner = testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.SchedulePruneHistory();

        bool cancelled = await scheduler.CancelledWithinWindow.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(cancelled, Is.EqualTo(expectCancelled));
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

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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

        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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
        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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
        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 0 };
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
        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = 1, PruningInterval = 1000 };
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

    private sealed class DeadlineObservingScheduler(IBackgroundTaskScheduler inner, TimeSpan window) : IBackgroundTaskScheduler
    {
        public TaskCompletionSource<bool> CancelledWithinWindow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
            => inner.TryScheduleTask(request, (req, token) =>
            {
                CancelledWithinWindow.TrySetResult(token.WaitHandle.WaitOne(window));
                return fulfillFunc(req, token);
            }, timeout);
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
