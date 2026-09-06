// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Reporting;
using Nethermind.Stats.SyncLimits;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test.FastBlocks;

[Parallelizable(ParallelScope.All)]
public class FastHeadersSyncTests
{
    [Test]
    public Task Will_fail_if_launched_without_fast_blocks_enabled()
    {
        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new HeadersSyncFeed(
                blockTree: blockTree,
                syncPeerPool: Substitute.For<ISyncPeerPool>(),
                syncConfig: new TestSyncConfig(),
                syncReport: Substitute.For<ISyncReport>(),
                poSSwitcher: Substitute.For<IPoSSwitcher>(),
                logManager: LimboLogs.Instance,
                chainLevelInfoRepository: Substitute.For<IChainLevelInfoRepository>(),
                headerStore: Substitute.For<IHeaderStore>());
        });

        return Task.CompletedTask;
    }

    [Test]
    public void When_initialized_with_no_inserted_headers_progress_starts_at_zero()
    {
        // Regression test for issue #11447: Reset() with a null LowestInsertedBlockHeader used to fall
        // back to 0, producing a current value of (_pivotNumber + 1) — visible as "Old Headers
        // 24,998,904 / 24,998,903 (100.00 %)" right after a fresh FlatDB sync started.
        const ulong pivotNumber = 1000UL;
        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;
        blockTree.SyncPivot = (pivotNumber, TestItem.KeccakA);

        ISyncReport syncReport = new NullSyncReport();
        using HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = pivotNumber,
                PivotHash = TestItem.KeccakA.ToString(),
                PivotTotalDifficulty = "1000"
            },
            poSSwitcher: Substitute.For<IPoSSwitcher>(),
            syncReport: syncReport,
            logManager: LimboLogs.Instance,
            chainLevelInfoRepository: Substitute.For<IChainLevelInfoRepository>(),
            headerStore: Substitute.For<IHeaderStore>());

        feed.InitializeFeed();

        Assert.That(syncReport.FastBlocksHeaders.CurrentValue, Is.EqualTo(0));
        Assert.That(syncReport.FastBlocksHeaders.TargetValue, Is.EqualTo(pivotNumber));
    }

    [Test]
    public async Task Can_prepare_3_requests_in_a_row()
    {
        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;
        blockTree.SyncPivot = (1000, TestItem.KeccakA);

        HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            poSSwitcher: Substitute.For<IPoSSwitcher>(),
            syncReport: Substitute.For<ISyncReport>(),
            logManager: LimboLogs.Instance,
            chainLevelInfoRepository: Substitute.For<IChainLevelInfoRepository>(),
            headerStore: Substitute.For<IHeaderStore>());

        await feed.PrepareRequest();
        await feed.PrepareRequest();
        await feed.PrepareRequest();
    }

    [Test]
    public async Task Can_handle_forks_with_persisted_headers()
    {
        IBlockTree remoteBlockTree = CachedBlockTreeBuilder.OfLength(1000);
        IBlockTree forkedBlockTree = Build.A.BlockTree().WithStateRoot(Keccak.Compute("1245")).OfChainLength(1000).TestObject;
        BlockHeader pivotBlock = remoteBlockTree.FindHeader(999)!;

        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree();
        IBlockTree blockTree = blockTreeBuilder.TestObject;
        for (ulong i = 500; i < 1000; i++)
        {
            Assert.That(blockTree.Insert(forkedBlockTree.FindHeader(i)!), Is.EqualTo(AddBlockResult.Added));
        }
        blockTree.SyncPivot = (pivotBlock.Number, pivotBlock.Hash!);

        ISyncReport syncReport = new NullSyncReport();
        HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = pivotBlock.Number,
                PivotHash = pivotBlock.Hash!.ToString(),
                PivotTotalDifficulty = pivotBlock.TotalDifficulty.ToString()!,
            },
            poSSwitcher: Substitute.For<IPoSSwitcher>(),
            syncReport: syncReport,
            logManager: LimboLogs.Instance,
            chainLevelInfoRepository: blockTreeBuilder.ChainLevelInfoRepository,
            headerStore: blockTreeBuilder.HeaderStore);

        feed.InitializeFeed();
        while (true)
        {
            HeadersSyncBatch? batch = await feed.PrepareRequest();
            if (batch is null) break;
            batch.Response = remoteBlockTree.FindHeaders(
                remoteBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash!, batch.RequestSize, 0,
                false)!;
            feed.HandleResponse(batch);
        }
    }

    [Test]
    public async Task When_next_header_hash_update_is_delayed_do_not_drop_peer()
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(1001).TestObject;
        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;

        ISyncReport syncReport = new NullSyncReport();

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());

        ManualResetEventSlim hangLatch = new(false);
        BlockHeader pivot = remoteBlockTree.FindHeader(1000, BlockTreeLookupOptions.None)!;
        blockTree.SyncPivot = (pivot.Number, pivot.Hash!);
        ResettableHeaderSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: syncPeerPool,
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = pivot.Hash!.Bytes.ToHexString(),
                PivotTotalDifficulty = pivot.TotalDifficulty.ToString()!
            },
            syncReport: syncReport,
            logManager: LimboLogs.Instance,
            hangOnBlockNumberAfterInsert: 425,
            hangLatch: hangLatch
        );

        feed.InitializeFeed();

        void FulfillBatch(HeadersSyncBatch batch)
        {
            batch.Response = remoteBlockTree.FindHeaders(
                remoteBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash, batch.RequestSize, 0,
                false)!;
            batch.ResponseSourcePeer = peerInfo;
        }

        using HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
        using HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;
        using HeadersSyncBatch batch3 = (await feed.PrepareRequest())!;
        using HeadersSyncBatch batch4 = (await feed.PrepareRequest())!;

        FulfillBatch(batch1);
        FulfillBatch(batch2);
        FulfillBatch(batch3);
        FulfillBatch(batch4);

        // Need to be triggered via `HandleDependencies` as there is a lock for `HandleResponse` that prevent this.
        feed.HandleResponse(batch1);
        feed.HandleResponse(batch3);
        feed.HandleResponse(batch2);
        Task _ = Task.Factory.StartNew(() => feed.PrepareRequest(), TaskCreationOptions.LongRunning);

        await Task.Delay(TimeSpan.FromMilliseconds(100));

        feed.HandleResponse(batch4);

        syncPeerPool.DidNotReceive().ReportBreachOfProtocol(peerInfo, Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public async Task Can_prepare_several_request_and_ignore_request_from_previous_sequence()
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(501).TestObject;
        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;

        ISyncReport syncReport = new NullSyncReport();

        BlockHeader pivot = remoteBlockTree.FindHeader(500, BlockTreeLookupOptions.None)!;
        blockTree.SyncPivot = (pivot.Number, pivot.Hash!);
        using ResettableHeaderSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 500,
                PivotHash = pivot.Hash!.Bytes.ToHexString(),
                PivotTotalDifficulty = pivot.TotalDifficulty!.ToString()!
            },
            syncReport: syncReport,
            logManager: LimboLogs.Instance);

        feed.InitializeFeed();

        void FulfillBatch(HeadersSyncBatch batch) =>
            batch.Response = remoteBlockTree.FindHeaders(
                remoteBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash, batch.RequestSize, 0,
                false)!;

        using HeadersSyncBatch? r = await feed.PrepareRequest();
        using HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
        FulfillBatch(batch1);

        feed.Reset();

        await feed.PrepareRequest();
        using HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;
        FulfillBatch(batch2);

        feed.HandleResponse(batch2);
        feed.HandleResponse(batch1);
    }

    [Test]
    public async Task Will_dispatch_when_only_partially_processed_dependency()
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(2001).TestObject;
        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;

        ISyncReport syncReport = new NullSyncReport();

        BlockHeader pivot = remoteBlockTree.FindHeader(2000, BlockTreeLookupOptions.None)!;
        blockTree.SyncPivot = (pivot.Number, pivot.Hash!);
        using HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = pivot.Number,
                PivotHash = pivot.Hash!.ToString(),
                PivotTotalDifficulty = pivot.TotalDifficulty.ToString()!,
            },
            syncReport: syncReport,
            poSSwitcher: Substitute.For<IPoSSwitcher>(),
            logManager: LimboLogs.Instance,
            chainLevelInfoRepository: Substitute.For<IChainLevelInfoRepository>(),
            headerStore: Substitute.For<IHeaderStore>());

        feed.InitializeFeed();

        void FulfillBatch(HeadersSyncBatch batch) =>
            batch.Response = remoteBlockTree.FindHeaders(
                remoteBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash, batch.RequestSize, 0,
                false)!;

        // First batch need to be handled first before handle dependencies can do anything
        using HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
        FulfillBatch(batch1);
        feed.HandleResponse(batch1);

        using HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;
        FulfillBatch(batch2);

        int maxHeaderBatchToProcess = 4;

        HeadersSyncBatch[] batches = Enumerable.Range(0, maxHeaderBatchToProcess + 1).Select(_ =>
        {
            HeadersSyncBatch batch = feed.PrepareRequest().Result!;
            FulfillBatch(batch);
            return batch;
        }).ToArray();

        // Disconnected chain so they all go to dependencies
        foreach (HeadersSyncBatch headersSyncBatch in batches)
        {
            feed.HandleResponse(headersSyncBatch);
        }

        // Batch2 would get processed
        feed.HandleResponse(batch2);

        // HandleDependantBatch would start from first batch in batches, stopped at second in batch (only process 2 batch)
        using HeadersSyncBatch newBatch = (await feed.PrepareRequest())!;
        Assert.That(blockTree.LowestInsertedHeader!.Number, Is.EqualTo(batches[1].StartNumber));

        // New batch would be at end of batch 5 (batch 6).
        Assert.That(newBatch.EndNumber, Is.EqualTo(batches[^1].StartNumber - 1));
        batches.DisposeItems();
    }

    [Test]
    public async Task Can_reset_and_not_hang_when_a_batch_is_processing()
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(501).TestObject;

        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;

        ISyncReport syncReport = new NullSyncReport();

        ManualResetEventSlim hangLatch = new(false);

        BlockHeader pivot = remoteBlockTree.FindHeader(500, BlockTreeLookupOptions.None)!;
        blockTree.SyncPivot = (pivot.Number, pivot.Hash!);
        ResettableHeaderSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 500,
                PivotHash = pivot.Hash!.Bytes.ToHexString(),
                PivotTotalDifficulty = pivot.TotalDifficulty!.ToString()!
            },
            syncReport: syncReport,
            logManager: LimboLogs.Instance,
            hangOnBlockNumber: 400,
            hangLatch: hangLatch
        );

        feed.InitializeFeed();

        void FulfillBatch(HeadersSyncBatch batch) =>
            batch.Response = remoteBlockTree.FindHeaders(
                remoteBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash, batch.RequestSize, 0,
                false)!;

        using HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
        FulfillBatch(batch1);

        // Initiate a process batch which should hang in the middle
        Task responseTask = Task.Factory.StartNew(() => feed.HandleResponse(batch1), TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        // Initiate a reset at the same time. Without protection, the _nextHeaderHash would be updated here, but so do at `InsertHeader` via `HandleResponse`.
        Task resetTask = Task.Factory.StartNew(() => feed.Reset(), TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        hangLatch.Set();
        await responseTask;
        await resetTask;

        // A new batch is creating, starting at hang block
        using HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;

        FulfillBatch(batch2);
        feed.HandleResponse(batch2);

        // The whole new batch should get processed instead of skipping due to concurrently modified _nextHeaderHash.
        Assert.That(blockTree.LowestInsertedHeader!.Number, Is.EqualTo(batch2.StartNumber));
    }

    [Test]
    public async Task Can_keep_returning_nulls_after_all_batches_were_prepared()
    {
        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;
        blockTree.SyncPivot = (1000, Keccak.Zero);
        HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            syncReport: Substitute.For<ISyncReport>(),
            poSSwitcher: Substitute.For<IPoSSwitcher>(),
            logManager: LimboLogs.Instance,
            chainLevelInfoRepository: Substitute.For<IChainLevelInfoRepository>(),
            headerStore: Substitute.For<IHeaderStore>());

        for (int i = 0; i < 10; i++)
        {
            await feed.PrepareRequest();
        }

        using HeadersSyncBatch? result = await feed.PrepareRequest();
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Does_not_prepare_batch_when_destination_moves_past_request_cursor()
    {
        const ulong pivotNumber = 1000UL;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.SyncPivot.Returns((pivotNumber, TestItem.KeccakA));

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        syncPeerPool.EstimateRequestLimit(RequestType.Headers, Arg.Any<IPeerAllocationStrategy>(), AllocationContexts.Headers, default)
            .Returns(Task.FromResult<int?>(10));

        using DestinationHeaderSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: syncPeerPool,
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = pivotNumber,
                PivotHash = TestItem.KeccakA.ToString(),
                PivotTotalDifficulty = "1000"
            },
            syncReport: new NullSyncReport(),
            logManager: LimboLogs.Instance,
            destinationNumber: 995);

        feed.InitializeFeed();

        using HeadersSyncBatch? firstBatch = await feed.PrepareRequest();
        Assert.That(firstBatch, Is.Not.Null);
        Assert.That(firstBatch!.StartNumber, Is.EqualTo(995));
        Assert.That(firstBatch.RequestSize, Is.EqualTo(6));

        feed.DestinationNumber = 996;

        using HeadersSyncBatch? overrunBatch = await feed.PrepareRequest();
        Assert.That(overrunBatch, Is.Null);
    }

    [Test]
    public async Task Finishes_when_all_downloaded()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1000).TestObject);
        blockTree.SyncPivot = (1000, Keccak.Zero);

        ISyncReport report = new NullSyncReport();
        HeadersSyncFeed feed = new(
            blockTree,
            Substitute.For<ISyncPeerPool>(),
            new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            report,
            Substitute.For<IPoSSwitcher>(),
            LimboLogs.Instance,
            Substitute.For<IChainLevelInfoRepository>(),
            Substitute.For<IHeaderStore>());
        await feed.PrepareRequest();
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
        using HeadersSyncBatch? result = await feed.PrepareRequest();

        Assert.That(result, Is.Null);
        Assert.That(feed.CurrentState, Is.EqualTo(SyncFeedState.Finished));
        Assert.That(report.FastBlocksHeaders.HasEnded, Is.True);
    }

    [Test]
    public async Task Can_resume_downloading_from_parent_of_lowest_inserted_header()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader
            .WithNumber(500)
            .WithTotalDifficulty(10_000_000)
            .TestObject);
        blockTree.SyncPivot = (1000, Keccak.Zero);

        ISyncReport report = new NullSyncReport();

        HeadersSyncFeed feed = new(
            blockTree,
            Substitute.For<ISyncPeerPool>(),
            new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            report,
            Substitute.For<IPoSSwitcher>(),
            LimboLogs.Instance,
            Substitute.For<IChainLevelInfoRepository>(),
            Substitute.For<IHeaderStore>());
        feed.InitializeFeed();
        using HeadersSyncBatch? result = await feed.PrepareRequest();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EndNumber, Is.EqualTo(499));
    }

    //Missing headers in the start is not allowed
    [TestCase(0, 1, 1, true, false)]
    [TestCase(0, 1, 1, false, true)]
    //Missing headers in the start is not allowed
    [TestCase(0, 2, 1, true, false)]
    [TestCase(0, 2, 1, false, true)]
    //Missing headers in the start is not allowed
    [TestCase(0, 2, 191, true, false)]
    [TestCase(0, 2, 191, false, true)]
    //Gaps are not allowed
    [TestCase(1, 1, 1, true, false)]
    [TestCase(1, 1, 1, true, true)]
    [TestCase(187, 5, 1, false, false)]
    [TestCase(187, 5, 1, false, true)]
    [TestCase(191, 1, 1, false, false)]
    [TestCase(191, 1, 1, false, true)]
    [TestCase(190, 1, 1, true, false)]
    [TestCase(190, 1, 1, true, true)]
    [TestCase(80, 1, 1, true, false)]
    [TestCase(80, 1, 1, true, true)]
    // All empty response
    [TestCase(0, 192, 1, false, false)]
    // All null response
    [TestCase(0, 192, 1, false, true)]
    public async Task Can_insert_all_good_headers_from_dependent_batch_with_missing_or_null_headers(int nullIndex, int count, int increment, bool shouldReport, bool useNulls)
    {
        using DependentBatchScenario scenario = new();
        IBlockTree peerChain = scenario.PeerChain;
        TestableHeadersSyncFeed feed = scenario.Feed;
        HeadersSyncBatch firstBatch = scenario.FirstBatch;
        HeadersSyncBatch dependentBatch = scenario.DependentBatch;
        const ulong lowestInserted = 999UL;

        void FillBatch(HeadersSyncBatch batch, ulong start, bool applyNulls)
        {
            int c = count;
            List<BlockHeader?> list = new(batch.RequestSize);
            ulong current = start;
            for (int j = 0; j < batch.RequestSize; j++, current++)
            {
                list.Add(peerChain.FindBlock(current, BlockTreeLookupOptions.None)!.Header);
            }
            if (applyNulls)
                for (int i = nullIndex; 0 < c; i += increment)
                {
                    list[i] = null;
                    c--;
                }
            if (!useNulls)
                list = list.Where(h => h is not null).ToList();
            batch.Response = list.ToPooledList();
        }

        FillBatch(firstBatch, lowestInserted - (ulong)firstBatch.RequestSize, false);
        FillBatch(dependentBatch, lowestInserted - (ulong)(dependentBatch.RequestSize * 2), true);
        ulong targetHeaderInDependentBatch = dependentBatch.StartNumber;

        feed.HandleResponse(dependentBatch);
        feed.HandleResponse(firstBatch);

        using HeadersSyncBatch? thirdBatch = await feed.PrepareRequest();
        FillBatch(thirdBatch!, thirdBatch!.StartNumber, false);
        feed.HandleResponse(thirdBatch);
        using HeadersSyncBatch? fourthBatch = await feed.PrepareRequest();
        FillBatch(fourthBatch!, fourthBatch!.StartNumber, false);
        feed.HandleResponse(fourthBatch);
        using HeadersSyncBatch? fifthBatch = await feed.PrepareRequest();

        Assert.That(scenario.LowestInserted, Is.LessThanOrEqualTo(targetHeaderInDependentBatch));
        scenario.SyncPeerPool.Received(shouldReport ? 1 : 0).ReportBreachOfProtocol(Arg.Any<PeerInfo>(), Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    /// <summary>
    /// Sets up a feed and takes two batches from it. <c>DependentBatch</c> covers the range below
    /// <c>FirstBatch</c>, so its headers cannot link to the chain until <c>FirstBatch</c> is
    /// answered. A response to it goes to `_dependencies` instead of being inserted.
    /// </summary>
    private sealed class DependentBatchScenario : IDisposable
    {
        public IBlockTree PeerChain { get; } = CachedBlockTreeBuilder.OfLength(1000);
        public ISyncPeerPool SyncPeerPool { get; } = Substitute.For<ISyncPeerPool>();
        public TestableHeadersSyncFeed Feed { get; }
        public HeadersSyncBatch FirstBatch { get; }
        public HeadersSyncBatch DependentBatch { get; }
        public ulong? LowestInserted => _localBlockTree.LowestInsertedHeader?.Number;

        private readonly IBlockTree _localBlockTree;

        public DependentBatchScenario(int requestSize = 0, int pivotNumber = 998)
        {
            if (requestSize > 0)
            {
                SyncPeerPool.EstimateRequestLimit(
                        Arg.Any<RequestType>(), Arg.Any<IPeerAllocationStrategy>(),
                        Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<int?>(requestSize));
            }

            BlockHeader pivotHeader = PeerChain.FindHeader((ulong)pivotNumber)!;
            TestSyncConfig syncConfig = new() { FastSync = true, PivotNumber = pivotHeader.Number, PivotHash = pivotHeader.Hash!.ToString(), PivotTotalDifficulty = pivotHeader.TotalDifficulty.ToString()! };

            _localBlockTree = Build.A.BlockTree(PeerChain.FindBlock(0, BlockTreeLookupOptions.None)!, null).WithSyncConfig(syncConfig).TestObject;
            _localBlockTree.SyncPivot = (pivotHeader.Number, pivotHeader.Hash);
            _localBlockTree.Insert(
                PeerChain.FindBlock((ulong)pivotNumber + 1, BlockTreeLookupOptions.None)!,
                BlockTreeInsertBlockOptions.SaveHeader);

            Feed = new TestableHeadersSyncFeed(_localBlockTree, SyncPeerPool, syncConfig, new NullSyncReport(), LimboLogs.Instance);
            Feed.InitializeFeed();

            FirstBatch = Feed.PrepareRequest().GetAwaiter().GetResult()!;
            DependentBatch = Feed.PrepareRequest().GetAwaiter().GetResult()!;
            DependentBatch.ResponseSourcePeer = new PeerInfo(Substitute.For<ISyncPeer>());
        }

        public void Dispose()
        {
            DependentBatch.Dispose();
            FirstBatch.Dispose();
            Feed.Dispose();
        }
    }

    // A batch is sliced for `_dependencies` before its header numbers are checked, so the numbers must not
    // decide the slice. Each case lists header numbers as offsets from the batch start. The last
    // is always correct for its position, which is what gets the batch to the dependency path.
    [TestCase("-1,1,2", TestName = "Number below the batch start")]
    [TestCase("3,1,2", TestName = "Number past the end of the response")]
    [TestCase("0,2,2", TestName = "Short response with a number gap")]
    [TestCase("0,0,2", TestName = "Duplicated number")]
    [TestCase("0,1,2,2,4", TestName = "Duplicate followed by a number gap")]
    [TestCase("x,1,1,3", TestName = "Duplicate behind a missing header")]
    [TestCase("x,x,x", TestName = "No headers at all")]
    [TestCase("0,x,2,x", TestName = "Trailing null after a missing header")]
    public async Task Will_never_lose_batch_on_bad_header_numbers(string offsets)
    {
        using DependentBatchScenario scenario = new();
        TestableHeadersSyncFeed feed = scenario.Feed;
        HeadersSyncBatch dependentBatch = scenario.DependentBatch;

        string[] tokens = offsets.Split(',');
        ArrayPoolList<BlockHeader?> response = new(tokens.Length);
        foreach (string token in tokens)
        {
            response.Add(token == "x"
                ? null
                : Build.A.BlockHeader.WithNumber((ulong)((long)dependentBatch.StartNumber + long.Parse(token))).TestObject);
        }
        dependentBatch.Response = response;

        ulong startNumber = dependentBatch.StartNumber;
        ulong endNumber = dependentBatch.EndNumber;
        Assert.DoesNotThrow(() => feed.HandleResponse(dependentBatch));

        // Whatever did not become a dependency must come back: a filler for the leftover range, or
        // the batch itself when none of it was held. InsertHeaders asserts the two add up to the whole.
        using HeadersSyncBatch? retry = await feed.PrepareRequest();
        Assert.That(retry, Is.Not.Null);
        Assert.That(retry!.StartNumber, Is.GreaterThanOrEqualTo(startNumber));
        Assert.That(retry.EndNumber, Is.LessThanOrEqualTo(endNumber));
    }

    // The highest header is correct for its position, so the batch is parked; every header below it
    // repeats that number. On drain it no longer links, so the peer is reported and the range re-requested.
    [Test]
    public async Task Reports_peer_and_retries_after_dependency_drains()
    {
        using DependentBatchScenario scenario = new();
        TestableHeadersSyncFeed feed = scenario.Feed;
        HeadersSyncBatch firstBatch = scenario.FirstBatch;
        HeadersSyncBatch dependentBatch = scenario.DependentBatch;

        // Every header claims EndNumber. The highest is then correct for its position, no upward
        // jump trips the old gap check, and the first pushes the upper bound past the response.
        ArrayPoolList<BlockHeader?> response = new(dependentBatch.RequestSize);
        for (int i = 0; i < dependentBatch.RequestSize; i++)
        {
            ulong number = i == 0 ? dependentBatch.EndNumber + 1 : dependentBatch.EndNumber;
            response.Add(Build.A.BlockHeader.WithNumber(number).TestObject);
        }
        dependentBatch.Response = response;

        Assert.DoesNotThrow(() => feed.HandleResponse(dependentBatch));

        // Connecting the batch above lets the dependency be processed, where the numbers get checked.
        RespondWithChainHeaders(scenario, firstBatch!);

        using HeadersSyncBatch? retry = await feed.PrepareRequest();

        scenario.SyncPeerPool.Received().ReportBreachOfProtocol(
            dependentBatch.ResponseSourcePeer!, DisconnectReason.HeaderBatchOnDifferentBranch, Arg.Any<string>());
        Assert.That(retry, Is.Not.Null);
        Assert.That(retry!.StartNumber, Is.EqualTo(dependentBatch.StartNumber));
        Assert.That(retry.RequestSize, Is.EqualTo(dependentBatch.RequestSize));
    }

    // The drain path removes a batch from `_dependencies` and disposes it, so a failed insert
    // there would orphan the range. It must recover without faulting PrepareRequest.
    [Test]
    public async Task Will_never_lose_batch_when_insert_throws_while_handling_dependency()
    {
        using DependentBatchScenario scenario = new();
        TestableHeadersSyncFeed feed = scenario.Feed;
        HeadersSyncBatch dependentBatch = scenario.DependentBatch;

        ArrayPoolList<BlockHeader?> response = new(dependentBatch.RequestSize);
        for (int i = 0; i < dependentBatch.RequestSize; i++)
        {
            response.Add(Build.A.BlockHeader.WithNumber(dependentBatch.StartNumber + (ulong)i).TestObject);
        }
        dependentBatch.Response = response;
        feed.HandleResponse(dependentBatch);

        // Connecting the batch above lets the dependency be processed on the next PrepareRequest.
        RespondWithChainHeaders(scenario, scenario.FirstBatch);

        // A fault here would end the dispatch loop and finish the feed, so the drain recovers the
        // range and returns rather than propagating. PrepareRequest hands the range straight back.
        feed.ThrowOnInsert = new InvalidOperationException("insert failed");
        using HeadersSyncBatch? retry = await feed.PrepareRequest();
        Assert.That(retry, Is.Not.Null);
        Assert.That(retry!.StartNumber, Is.EqualTo(dependentBatch.StartNumber));
        Assert.That(retry.RequestSize, Is.EqualTo(dependentBatch.RequestSize));
    }

    // Cancellation is not a failed insert: it ends the dispatch loop and finishes the feed, which
    // disposes the queue. It must propagate untouched rather than be treated as recoverable.
    [Test]
    public void Propagates_cancellation_from_a_dependency_drain()
    {
        using DependentBatchScenario scenario = new();
        TestableHeadersSyncFeed feed = scenario.Feed;
        HeadersSyncBatch dependentBatch = scenario.DependentBatch;

        ArrayPoolList<BlockHeader?> response = new(dependentBatch.RequestSize);
        for (int i = 0; i < dependentBatch.RequestSize; i++)
        {
            response.Add(Build.A.BlockHeader.WithNumber(dependentBatch.StartNumber + (ulong)i).TestObject);
        }
        dependentBatch.Response = response;
        feed.HandleResponse(dependentBatch);

        RespondWithChainHeaders(scenario, scenario.FirstBatch);

        feed.ThrowOnInsert = new OperationCanceledException();
        Assert.ThrowsAsync<OperationCanceledException>(() => feed.PrepareRequest());
        Assert.That(feed.Pending, Is.Empty);
    }

    // A dependency is keyed by its highest header's number, so its EndNumber must equal that
    // number. Trailing nulls used to push EndNumber above the key, and the `lowest - 1` lookup in
    // HandleDependentBatches could then never find it.
    [Test]
    public async Task Dependent_batch_is_keyed_by_its_end_number()
    {
        using DependentBatchScenario scenario = new();
        TestableHeadersSyncFeed feed = scenario.Feed;
        HeadersSyncBatch dependentBatch = scenario.DependentBatch;

        // Interior gap plus a trailing null: [h(start), null, h(start+2), null].
        ArrayPoolList<BlockHeader?> response = new(4)
        {
            Build.A.BlockHeader.WithNumber(dependentBatch.StartNumber).TestObject,
            null,
            Build.A.BlockHeader.WithNumber(dependentBatch.StartNumber + 2).TestObject,
            null
        };
        dependentBatch.Response = response;
        feed.HandleResponse(dependentBatch);

        // Answer everything else correctly; sync must descend past the hole rather than stall on it.
        RespondWithChainHeaders(scenario, scenario.FirstBatch);
        for (int i = 0; i < 40; i++)
        {
            using HeadersSyncBatch? next = await feed.PrepareRequest();
            if (next is null) break;

            RespondWithChainHeaders(scenario, next);
        }

        Assert.That(scenario.LowestInserted, Is.Not.Null);
        Assert.That(scenario.LowestInserted!.Value, Is.LessThanOrEqualTo(dependentBatch.StartNumber));
    }

    private static IDictionary<ulong, HeadersSyncBatch> Dependencies(HeadersSyncFeed feed) =>
        (IDictionary<ulong, HeadersSyncBatch>)typeof(HeadersSyncFeed)
            .GetField("_dependencies", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(feed)!;

    private static void RespondWithChainHeaders(DependentBatchScenario scenario, HeadersSyncBatch batch)
    {
        ArrayPoolList<BlockHeader?> headers = new(batch.RequestSize);
        for (ulong number = batch.StartNumber; number <= batch.EndNumber; number++)
        {
            headers.Add(scenario.PeerChain.FindBlock(number, BlockTreeLookupOptions.None)?.Header);
        }
        batch.Response = headers;
        scenario.Feed.HandleResponse(batch);
    }

    // A dependency is only ever looked up as `lowest - 1`, and the lowest inserted header never
    // rises, so an entry at or above it would sit there forever. Headers from the lowest inserted
    // upwards are already stored, so only the part of the batch below it may be re-requested —
    // re-requesting the whole batch would reject and re-queue it forever.
    [TestCase(100, 100, TestName = "Part of the batch is still needed")]
    [TestCase(0, 0, TestName = "All of the batch is already stored")]
    public async Task Does_not_add_dependency_above_lowest_inserted(int storedFromOffset, int expectedFillerSize)
    {
        using DependentBatchScenario scenario = new();
        HeadersSyncBatch dependentBatch = scenario.DependentBatch;

        ArrayPoolList<BlockHeader?> response = new(dependentBatch.RequestSize);
        for (int i = 0; i < dependentBatch.RequestSize; i++)
        {
            response.Add(Build.A.BlockHeader.WithNumber(dependentBatch.StartNumber + (ulong)i).TestObject);
        }
        dependentBatch.Response = response;

        scenario.Feed.PinnedLowestInserted = Build.A.BlockHeader
            .WithNumber(dependentBatch.StartNumber + (ulong)storedFromOffset).TestObject;

        // Accounted for, so the batch is not re-queued whole.
        Assert.That(scenario.Feed.HandleResponse(dependentBatch), Is.EqualTo(SyncResponseHandlingResult.OK));
        Assert.That(Dependencies(scenario.Feed).Count, Is.Zero, "batch must not reach _dependencies");

        using HeadersSyncBatch? next = await scenario.Feed.PrepareRequest();
        Assert.That(next, Is.Not.Null);
        if (expectedFillerSize > 0)
        {
            Assert.That(next!.StartNumber, Is.EqualTo(dependentBatch.StartNumber));
            Assert.That(next.RequestSize, Is.EqualTo(expectedFillerSize));
        }
        else
        {
            // Nothing of this range is outstanding, so sync must have moved below it.
            Assert.That(next!.EndNumber, Is.LessThan(dependentBatch.StartNumber));
        }
    }

    // A range can overlap a dependency once `RequeueAsNewBatch` re-queues it whole, so a response's
    // highest header can collide with an existing key. That exit used to queue the batch that the
    // `added <= 0` path queues anyway, and one instance in `_pending` twice is dispatched twice.
    [Test]
    public void Does_not_queue_a_batch_twice_when_a_dependency_already_exists()
    {
        using DependentBatchScenario scenario = new();
        HeadersSyncBatch dependentBatch = scenario.DependentBatch;

        ArrayPoolList<BlockHeader?> response = new(dependentBatch.RequestSize);
        for (int i = 0; i < dependentBatch.RequestSize; i++)
        {
            response.Add(Build.A.BlockHeader.WithNumber(dependentBatch.StartNumber + (ulong)i).TestObject);
        }
        dependentBatch.Response = response;

        // Disposed with the feed.
        Dependencies(scenario.Feed)[dependentBatch.EndNumber] =
            new HeadersSyncBatch { StartNumber = dependentBatch.StartNumber, RequestSize = dependentBatch.RequestSize };

        scenario.Feed.HandleResponse(dependentBatch);

        scenario.SyncPeerPool.Received().ReportBreachOfProtocol(
            dependentBatch.ResponseSourcePeer!, DisconnectReason.MultipleHeaderDependencies, Arg.Any<string>());
        Assert.That(scenario.Feed.Pending, Has.Exactly(1).Items);
        HeadersSyncBatch queued = scenario.Feed.Pending.Single();
        Assert.That(queued.StartNumber, Is.EqualTo(dependentBatch.StartNumber));
        Assert.That(queued.RequestSize, Is.EqualTo(dependentBatch.RequestSize));
        Assert.That(queued.Response, Is.Null, "queued through the added <= 0 path, which drops the response");
    }

    // Hand-picked shapes miss combinations: the trailing-null-after-a-gap bug needed two conditions
    // at once. Enumerate every null pattern instead and assert the invariants rather than outcomes.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public async Task Every_response_shape_becomes_a_dependency_or_is_requeued(int requestSize)
    {
        for (int mask = 0; mask < 1 << requestSize; mask++)
        {
            using DependentBatchScenario scenario = new(requestSize);
            HeadersSyncBatch dependentBatch = scenario.DependentBatch;
            Assert.That(dependentBatch.RequestSize, Is.EqualTo(requestSize), "stubbed request size did not take");

            ArrayPoolList<BlockHeader?> response = new(requestSize);
            for (int i = 0; i < requestSize; i++)
            {
                response.Add((mask & (1 << i)) != 0
                    ? null
                    : Build.A.BlockHeader.WithNumber(dependentBatch.StartNumber + (ulong)i).TestObject);
            }
            dependentBatch.Response = response;

            ulong startNumber = dependentBatch.StartNumber;
            ulong endNumber = dependentBatch.EndNumber;

            Assert.DoesNotThrow(() => scenario.Feed.HandleResponse(dependentBatch), $"mask {mask}");

            // Whatever is held must be findable: it is looked up by `lowest - 1`, so the key has to
            // be that entry's own EndNumber.
            foreach ((ulong key, HeadersSyncBatch dependency) in Dependencies(scenario.Feed))
            {
                Assert.That(key, Is.EqualTo(dependency.EndNumber), $"mask {mask} - dependency is unreachable");
            }

            // And the range must not vanish: either it is held, or it comes back to download.
            bool becameDependency = Dependencies(scenario.Feed).Count > 0;
            using HeadersSyncBatch? next = await scenario.Feed.PrepareRequest();
            bool requeued = next is not null && next.StartNumber >= startNumber && next.EndNumber <= endNumber;
            Assert.That(becameDependency || requeued, Is.True, $"mask {mask} - range neither held nor requeued");
        }
    }

    // batch.StartNumber == 0 is the one case where addedLast's sentinel (StartNumber - 1, clamped to
    // 0) collides with a real header number. Only reachable at the very bottom of a headers sync.
    [Test]
    public async Task Handles_a_dependent_batch_that_starts_at_genesis()
    {
        using DependentBatchScenario scenario = new(requestSize: 4, pivotNumber: 7);
        HeadersSyncBatch dependentBatch = scenario.DependentBatch;
        Assert.That(dependentBatch.StartNumber, Is.Zero, "batch should reach genesis");

        ArrayPoolList<BlockHeader?> response = new(dependentBatch.RequestSize);
        for (int i = 0; i < dependentBatch.RequestSize; i++)
        {
            response.Add(Build.A.BlockHeader.WithNumber(dependentBatch.StartNumber + (ulong)i).TestObject);
        }
        dependentBatch.Response = response;

        Assert.DoesNotThrow(() => scenario.Feed.HandleResponse(dependentBatch));
        foreach ((ulong key, HeadersSyncBatch dependency) in Dependencies(scenario.Feed))
        {
            Assert.That(key, Is.EqualTo(dependency.EndNumber));
        }
    }

    [Test]
    public async Task Does_not_download_persisted_header()
    {
        IBlockTree peerChain = CachedBlockTreeBuilder.OfLength(1000);
        BlockHeader pivotHeader = peerChain.FindHeader(999)!;
        TestSyncConfig syncConfig = new() { FastSync = true, PivotNumber = pivotHeader.Number, PivotHash = pivotHeader.Hash!.ToString(), PivotTotalDifficulty = pivotHeader.TotalDifficulty.ToString()! };

        BlockTreeBuilder localBlockTreeBuilder = Build.A.BlockTree(peerChain.FindBlock(0, BlockTreeLookupOptions.None)!, null).WithSyncConfig(syncConfig);
        IBlockTree localBlockTree = localBlockTreeBuilder.TestObject;
        localBlockTree.SyncPivot = (pivotHeader.Number, pivotHeader.Hash);

        // Insert some chain
        for (ulong i = 0; i < 600; i++)
        {
            Assert.That(localBlockTree.SuggestHeader(peerChain.FindHeader(i)!), Is.EqualTo(AddBlockResult.Added));
        }

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        ISyncReport report = new NullSyncReport();
        using HeadersSyncFeed feed = new(localBlockTree, syncPeerPool, syncConfig, report, Substitute.For<IPoSSwitcher>(), LimboLogs.Instance,
            localBlockTreeBuilder.ChainLevelInfoRepository, localBlockTreeBuilder.HeaderStore);
        feed.InitializeFeed();

        void FillBatch(HeadersSyncBatch batch)
        {
            List<BlockHeader?> list = new(batch.RequestSize);
            ulong current = batch.StartNumber;
            for (int j = 0; j < batch.RequestSize; j++, current++)
            {
                list.Add(peerChain.FindBlock(current, BlockTreeLookupOptions.None)!.Header);
            }
            batch.Response = list.ToPooledList();
        }

        using HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
        Assert.That(batch1.StartNumber, Is.EqualTo(808));

        using HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;
        Assert.That(batch2.StartNumber, Is.EqualTo(616));

        using HeadersSyncBatch batch3 = (await feed.PrepareRequest())!;
        Assert.That(batch3.StartNumber, Is.EqualTo(424));

        Assert.That((await feed.PrepareRequest()), Is.EqualTo(null));

        FillBatch(batch1);
        FillBatch(batch2);
        FillBatch(batch3);

        feed.HandleResponse(batch1);
        feed.HandleResponse(batch2);
        feed.HandleResponse(batch3);

        // The dependency batch is processed during prepare request.
        Assert.That((await feed.PrepareRequest()), Is.EqualTo(null));

        Assert.That(localBlockTree.LowestInsertedHeader?.Number, Is.EqualTo(0));
    }

    [Test]
    public async Task Limits_persisted_headers_dependency()
    {
        IBlockTree peerChain = CachedBlockTreeBuilder.OfLength(1000);
        BlockHeader pivotHeader = peerChain.FindHeader(700)!;
        TestSyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = pivotHeader.Number,
            PivotHash = pivotHeader.Hash!.ToString(),
            PivotTotalDifficulty = pivotHeader.TotalDifficulty.ToString()!,
            FastHeadersMemoryBudget = 100UL.KB,
        };

        BlockTreeBuilder localBlockTreeBuilder = Build.A.BlockTree(peerChain.FindBlock(0, BlockTreeLookupOptions.None)!, null).WithSyncConfig(syncConfig);
        IBlockTree localBlockTree = localBlockTreeBuilder.TestObject;
        localBlockTree.SyncPivot = (pivotHeader.Number, pivotHeader.Hash);

        // Insert some chain
        for (ulong i = 300; i < 600; i++)
        {
            Assert.That(localBlockTree.Insert(peerChain.FindHeader(i)!), Is.EqualTo(AddBlockResult.Added));
        }

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        ISyncReport report = new NullSyncReport();
        using HeadersSyncFeed feed = new(localBlockTree, syncPeerPool, syncConfig, report, Substitute.For<IPoSSwitcher>(), LimboLogs.Instance,
            localBlockTreeBuilder.ChainLevelInfoRepository, localBlockTreeBuilder.HeaderStore);
        feed.InitializeFeed();

        Assert.That((await feed.PrepareRequest()), Is.Not.EqualTo(null));
        Assert.That((await feed.PrepareRequest()), Is.EqualTo(null));
    }

    [Test]
    public async Task Can_use_persisted_header_without_total_difficulty()
    {
        IBlockTree peerChain = CachedBlockTreeBuilder.OfLength(1000);
        BlockHeader pivotHeader = peerChain.FindHeader(700)!;
        TestSyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = pivotHeader.Number,
            PivotHash = pivotHeader.Hash!.ToString(),
            PivotTotalDifficulty = pivotHeader.TotalDifficulty.ToString()!
        };

        IChainLevelInfoRepository levelInfoRepository = new ChainLevelInfoRepository(new TestMemDb());
        BlockTreeBuilder localBlockTreeBuilder = Build.A.BlockTree(peerChain.FindBlock(0, BlockTreeLookupOptions.None)!, null)
            .WithChainLevelInfoRepository(levelInfoRepository)
            .WithSyncConfig(syncConfig);
        IBlockTree localBlockTree = localBlockTreeBuilder.TestObject;
        localBlockTree.SyncPivot = (pivotHeader.Number, pivotHeader.Hash);

        // pivot.Number (700) >> MaxHeaderFetch, so firstCheckedHeader > 0 and the loop cannot underflow.
        ulong firstCheckedHeader = pivotHeader.Number - (ulong)GethSyncLimits.MaxHeaderFetch;
        for (ulong i = firstCheckedHeader - 1; i <= firstCheckedHeader; i++)
        {
            BlockHeader header = peerChain.FindHeader(i)!;
            header.TotalDifficulty = null;
            Assert.That(localBlockTree.Insert(header, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded), Is.EqualTo(AddBlockResult.Added));
        }
        levelInfoRepository.Delete(firstCheckedHeader - 1);

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        ISyncReport report = NullSyncReport.Instance;
        using HeadersSyncFeed feed = new(localBlockTree, syncPeerPool, syncConfig, report, Substitute.For<IPoSSwitcher>(), LimboLogs.Instance,
            levelInfoRepository, localBlockTreeBuilder.HeaderStore);
        feed.InitializeFeed();

        Assert.That((await feed.PrepareRequest()), Is.Not.EqualTo(null));
        Assert.That((await feed.PrepareRequest()), Is.Not.EqualTo(null));
        Assert.That((await feed.PrepareRequest()), Is.Not.EqualTo(null));
    }

    [Test]
    public async Task Can_initialize_feed_after_restart_when_pivot_chain_level_is_missing()
    {
        IBlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(1001).TestObject;
        BlockHeader pivot = remoteBlockTree.FindHeader(1000, BlockTreeLookupOptions.None)!;
        TestSyncConfig syncConfig = new() { FastSync = true };

        Func<BlockTreeBuilder> createBuilderOverSharedDbs = SharedDbsBlockTreeBuilderFactory(syncConfig);
        BlockTreeBuilder builderBeforeRestart = createBuilderOverSharedDbs();
        BlockTree treeBeforeRestart = builderBeforeRestart.TestObject;
        Assert.That(treeBeforeRestart.Insert(pivot), Is.EqualTo(AddBlockResult.Added));
        treeBeforeRestart.SyncPivot = (pivot.Number, pivot.Hash!); // Persisted to the metadata db
        builderBeforeRestart.ChainLevelInfoRepository.Delete(pivot.Number); // The chain level write that was lost

        BlockTreeBuilder builderAfterRestart = createBuilderOverSharedDbs();
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.FinalTotalDifficulty.Returns(pivot.TotalDifficulty);
        using HeadersSyncFeed feed = CreateFeed(builderAfterRestart, syncConfig, poSSwitcher);

        feed.InitializeFeed();

        using HeadersSyncBatch? batch = await feed.PrepareRequest();
        Assert.That(batch, Is.Not.Null);
        Assert.That(batch!.EndNumber, Is.EqualTo(pivot.Number));
    }

    [Test]
    public async Task Resets_header_sync_after_restart_when_lowest_inserted_header_chain_level_is_missing()
    {
        IBlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(1001).TestObject;
        BlockHeader pivot = remoteBlockTree.FindHeader(1000, BlockTreeLookupOptions.None)!;
        BlockHeader lowestInserted = remoteBlockTree.FindHeader(900, BlockTreeLookupOptions.None)!;
        TestSyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = pivot.Number,
            PivotHash = pivot.Hash!.ToString(),
            PivotTotalDifficulty = pivot.TotalDifficulty.ToString()!,
        };

        Func<BlockTreeBuilder> createBuilderOverSharedDbs = SharedDbsBlockTreeBuilderFactory(syncConfig);
        BlockTreeBuilder builderBeforeRestart = createBuilderOverSharedDbs();
        BlockTree treeBeforeRestart = builderBeforeRestart.TestObject;
        Assert.That(treeBeforeRestart.Insert(lowestInserted), Is.EqualTo(AddBlockResult.Added));
        treeBeforeRestart.LowestInsertedHeader = lowestInserted; // Persisted to the metadata db
        builderBeforeRestart.ChainLevelInfoRepository.Delete(lowestInserted.Number); // The chain level write that was lost

        BlockTreeBuilder builderAfterRestart = createBuilderOverSharedDbs();
        BlockTree treeAfterRestart = builderAfterRestart.TestObject;
        Assert.That(treeAfterRestart.LowestInsertedHeader?.Number, Is.EqualTo(lowestInserted.Number),
            "the level-less lowest inserted header must be loaded back on restart for the test to be meaningful");
        using HeadersSyncFeed feed = CreateFeed(builderAfterRestart, syncConfig, Substitute.For<IPoSSwitcher>());

        feed.InitializeFeed();

        Assert.That(treeAfterRestart.LowestInsertedHeader, Is.Null);
        using HeadersSyncBatch? batch = await feed.PrepareRequest();
        Assert.That(batch, Is.Not.Null);
        Assert.That(batch!.EndNumber, Is.EqualTo(pivot.Number));
    }

    private static Func<BlockTreeBuilder> SharedDbsBlockTreeBuilderFactory(TestSyncConfig syncConfig)
    {
        TestMemDb blocksDb = new();
        TestMemDb headersDb = new();
        TestMemDb blockNumbersDb = new();
        TestMemDb blockInfoDb = new();
        TestMemDb metadataDb = new();
        return () => Build.A.BlockTree()
            .WithoutSettingHead
            .WithBlocksDb(blocksDb)
            .WithHeadersDb(headersDb)
            .WithBlocksNumberDb(blockNumbersDb)
            .WithBlockInfoDb(blockInfoDb)
            .WithMetadataDb(metadataDb)
            .WithSyncConfig(syncConfig);
    }

    private static HeadersSyncFeed CreateFeed(BlockTreeBuilder builder, TestSyncConfig syncConfig, IPoSSwitcher poSSwitcher) => new(
        blockTree: builder.TestObject,
        syncPeerPool: Substitute.For<ISyncPeerPool>(),
        syncConfig: syncConfig,
        syncReport: new NullSyncReport(),
        poSSwitcher: poSSwitcher,
        logManager: LimboLogs.Instance,
        chainLevelInfoRepository: builder.ChainLevelInfoRepository,
        headerStore: builder.HeaderStore);

    [Test]
    public async Task Will_never_lose_batch_on_invalid_batch()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1000).TestObject);
        blockTree.SyncPivot = (1000, Keccak.Zero);
        ISyncReport report = new NullSyncReport();
        HeadersSyncFeed feed = new(
            blockTree,
            Substitute.For<ISyncPeerPool>(),
            new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            report,
            Substitute.For<IPoSSwitcher>(),
            LimboLogs.Instance,
            Substitute.For<IChainLevelInfoRepository>(),
            Substitute.For<IHeaderStore>());
        feed.InitializeFeed();

        List<HeadersSyncBatch> batches = [];
        while (true)
        {
            HeadersSyncBatch? batch = await feed.PrepareRequest();
            if (batch is null) break;
            batches.Add(batch);
        }
        int totalBatchCount = batches.Count;

        Channel<HeadersSyncBatch> batchToProcess = Channel.CreateBounded<HeadersSyncBatch>(batches.Count);
        foreach (HeadersSyncBatch headersSyncBatch in batches)
        {
            await batchToProcess.Writer.WriteAsync(headersSyncBatch);
        }
        batches.Clear();

        Task requestTasks = Task.Run(async () =>
        {
            for (int i = 0; i < 100000; i++)
            {
                HeadersSyncBatch? batch = await feed.PrepareRequest();
                if (batch is null)
                {
                    await Task.Delay(1);
                    continue;
                }

                await batchToProcess.Writer.WriteAsync(batch);
            }

            batchToProcess.Writer.Complete();
        });

        BlockHeader randomBlockHeader = Build.A.BlockHeader.WithNumber(999999).TestObject;
        await foreach (HeadersSyncBatch headersSyncBatch in batchToProcess.Reader.ReadAllAsync())
        {
            headersSyncBatch.Response = new ArrayPoolList<BlockHeader?>(1) { randomBlockHeader };
            feed.HandleResponse(headersSyncBatch);
        }

        await requestTasks;

        while (true)
        {
            using HeadersSyncBatch? batch = await feed.PrepareRequest();
            if (batch is null) break;
            batches.Add(batch);
        }

        Assert.That(batches.Count, Is.EqualTo(totalBatchCount));
    }

    [Test]
    public async Task Will_never_lose_batch_when_insert_throws()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1000).TestObject);
        blockTree.SyncPivot = (1000, Keccak.Zero);
        using TestableHeadersSyncFeed feed = new(
            blockTree,
            Substitute.For<ISyncPeerPool>(),
            new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            new NullSyncReport(),
            LimboLogs.Instance);
        feed.InitializeFeed();

        HeadersSyncBatch sent = (await feed.PrepareRequest())!;
        feed.ThrowOnInsert = new InvalidOperationException("insert failed");
        sent.Response = new ArrayPoolList<BlockHeader?>(1) { Build.A.BlockHeader.WithNumber(999).TestObject };

        Assert.Throws<InvalidOperationException>(() => feed.HandleResponse(sent));

        using HeadersSyncBatch? retried = await feed.PrepareRequest();
        Assert.That(retried, Is.Not.Null);
        Assert.That(retried!.StartNumber, Is.EqualTo(sent.StartNumber));
        Assert.That(retried.RequestSize, Is.EqualTo(sent.RequestSize));
    }


    [Test]
    public void IsFinished_returns_false_when_headers_not_downloaded()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        TestSyncConfig syncConfig = new()
        {
            FastSync = true,
            DownloadBodiesInFastSync = true,
            DownloadReceiptsInFastSync = true,
            PivotNumber = 1,
        };

        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(2).WithStateRoot(TestItem.KeccakA).TestObject);
        blockTree.SyncPivot.Returns((1UL, Keccak.Zero));

        HeadersSyncFeed feed = new(
            blockTree,
            Substitute.For<ISyncPeerPool>(),
            syncConfig,
            Substitute.For<ISyncReport>(),
            Substitute.For<IPoSSwitcher>(),
            LimboLogs.Instance,
            Substitute.For<IChainLevelInfoRepository>(),
            Substitute.For<IHeaderStore>());

        Assert.That(feed.IsFinished, Is.False);
    }

    [Test]
    public void When_lowestInsertedHeaderHasNoTD_then_fetchFromBlockTreeAgain()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        using HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = TestItem.KeccakA.ToString(),
                PivotTotalDifficulty = "1000",
            },
            syncReport: new NullSyncReport(),
            poSSwitcher: Substitute.For<IPoSSwitcher>(),
            logManager: LimboLogs.Instance,
            chainLevelInfoRepository: Substitute.For<IChainLevelInfoRepository>(),
            headerStore: Substitute.For<IHeaderStore>(),
            totalDifficultyStrategy: new CumulativeTotalDifficultyStrategy());
        blockTree.SyncPivot.Returns((1000UL, TestItem.KeccakA));

        BlockHeader header = Build.A.BlockHeader.WithNumber(900).TestObject;
        header.Difficulty = 10;
        header.TotalDifficulty = null;
        blockTree.LowestInsertedHeader.Returns(header);

        BlockHeader header2 = Build.A.BlockHeader.WithNumber(900).TestObject;
        header2.Difficulty = 10;
        header2.TotalDifficulty = 1000;
        blockTree.FindHeader(header.Number, BlockTreeLookupOptions.RequireCanonical).Returns(header2);

        feed.InitializeFeed();
    }

    [Test]
    public void When_cant_determine_pivot_total_difficulty_then_throw()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        using HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = TestItem.KeccakA.ToString(),
                PivotTotalDifficulty = "1000",
            },
            syncReport: new NullSyncReport(),
            poSSwitcher: Substitute.For<IPoSSwitcher>(),
            logManager: LimboLogs.Instance,
            chainLevelInfoRepository: Substitute.For<IChainLevelInfoRepository>(),
            headerStore: Substitute.For<IHeaderStore>(),
            totalDifficultyStrategy: new CumulativeTotalDifficultyStrategy());
        blockTree.SyncPivot.Returns((1010UL, TestItem.KeccakB));

        Action act = () => feed.InitializeFeed();
        Assert.That(act, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task Should_Limit_BatchSize_ToEstimate()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        using HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: syncPeerPool,
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = TestItem.KeccakA.ToString(),
                PivotTotalDifficulty = "1000",
            },
            syncReport: new NullSyncReport(),
            poSSwitcher: Substitute.For<IPoSSwitcher>(),
            logManager: LimboLogs.Instance,
            chainLevelInfoRepository: Substitute.For<IChainLevelInfoRepository>(),
            headerStore: Substitute.For<IHeaderStore>(),
            totalDifficultyStrategy: new CumulativeTotalDifficultyStrategy());
        blockTree.SyncPivot.Returns((1000UL, TestItem.KeccakB));

        syncPeerPool.EstimateRequestLimit(RequestType.Headers, Arg.Any<IPeerAllocationStrategy>(), AllocationContexts.Headers, default)
            .Returns(Task.FromResult<int?>(5));

        feed.InitializeFeed();
        HeadersSyncBatch? req = await feed.PrepareRequest(default);
        Assert.That(req!.RequestSize, Is.EqualTo(5));
    }

    private class ResettableHeaderSyncFeed(
        IBlockTree? blockTree,
        ISyncPeerPool? syncPeerPool,
        ISyncConfig? syncConfig,
        ISyncReport? syncReport,
        ILogManager? logManager,
        ulong? hangOnBlockNumber = null,
        ulong? hangOnBlockNumberAfterInsert = null,
        ManualResetEventSlim? hangLatch = null,
        bool alwaysStartHeaderSync = false
        ) : HeadersSyncFeed(blockTree, syncPeerPool, syncConfig, syncReport, Substitute.For<IPoSSwitcher>(), logManager,
            Substitute.For<IChainLevelInfoRepository>(), Substitute.For<IHeaderStore>(), alwaysStartHeaderSync: alwaysStartHeaderSync)
    {
        private readonly ManualResetEventSlim? _hangLatch = hangLatch;
        private readonly ulong? _hangOnBlockNumber = hangOnBlockNumber;
        private readonly ulong? _hangOnBlockNumberAfterInsert = hangOnBlockNumberAfterInsert;

        public void Reset()
        {
            base.PostFinishCleanUp();
            InitializeFeed();
        }

        protected override void InsertHeaders(IReadOnlyList<BlockHeader> headersToAdd)
        {
            foreach (BlockHeader header in headersToAdd)
            {
                if (header.Number == _hangOnBlockNumber)
                {
                    _hangLatch!.Wait();
                }
            }

            base.InsertHeaders(headersToAdd);

            foreach (BlockHeader header in headersToAdd)
            {
                if (header.Number == _hangOnBlockNumberAfterInsert)
                {
                    _hangLatch!.Wait();
                }
            }
        }
    }

    private class TestableHeadersSyncFeed(
        IBlockTree? blockTree,
        ISyncPeerPool? syncPeerPool,
        ISyncConfig? syncConfig,
        ISyncReport? syncReport,
        ILogManager? logManager
        ) : HeadersSyncFeed(blockTree, syncPeerPool, syncConfig, syncReport, Substitute.For<IPoSSwitcher>(), logManager,
            Substitute.For<IChainLevelInfoRepository>(), Substitute.For<IHeaderStore>())
    {
        public Exception? ThrowOnInsert { get; set; }
        public BlockHeader? PinnedLowestInserted { get; set; }
        public IReadOnlyCollection<HeadersSyncBatch> Pending => _pending;

        protected override BlockHeader? LowestInsertedBlockHeader
        {
            get => PinnedLowestInserted ?? base.LowestInsertedBlockHeader;
            set => base.LowestInsertedBlockHeader = value;
        }

        protected override int InsertHeaders(HeadersSyncBatch batch) =>
            ThrowOnInsert is null ? base.InsertHeaders(batch) : throw ThrowOnInsert;
    }

    private class DestinationHeaderSyncFeed(
        IBlockTree? blockTree,
        ISyncPeerPool? syncPeerPool,
        ISyncConfig? syncConfig,
        ISyncReport? syncReport,
        ILogManager? logManager,
        ulong destinationNumber
        ) : HeadersSyncFeed(blockTree, syncPeerPool, syncConfig, syncReport, Substitute.For<IPoSSwitcher>(), logManager,
            Substitute.For<IChainLevelInfoRepository>(), Substitute.For<IHeaderStore>())
    {
        public ulong DestinationNumber { get; set; } = destinationNumber;

        protected override ulong HeadersDestinationNumber => DestinationNumber;
    }

}
