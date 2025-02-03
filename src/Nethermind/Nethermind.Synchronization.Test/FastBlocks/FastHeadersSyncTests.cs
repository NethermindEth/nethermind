// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
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
            HeadersSyncFeed _ = new HeadersSyncFeed(
                blockTree: blockTree,
                syncPeerPool: Substitute.For<ISyncPeerPool>(),
                syncConfig: new TestSyncConfig(),
                syncReport: Substitute.For<ISyncReport>(),
                logManager: LimboLogs.Instance);
        });

        return Task.CompletedTask;
    }

    [Test]
    public async Task Can_prepare_3_requests_in_a_row()
    {
        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;

        HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = "1000",
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            syncReport: Substitute.For<ISyncReport>(),
            logManager: LimboLogs.Instance);

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

        IBlockTree blockTree = Build.A.BlockTree().TestObject;
        for (int i = 500; i < 1000; i++)
        {
            blockTree.Insert(forkedBlockTree.FindHeader(i)!).Should().Be(AddBlockResult.Added);
        }

        ISyncReport syncReport = Substitute.For<ISyncReport>();
        syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
        syncReport.HeadersInQueue.Returns(new MeasuredProgress());
        HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = pivotBlock.Number.ToString(),
                PivotHash = pivotBlock.Hash!.ToString(),
                PivotTotalDifficulty = pivotBlock.TotalDifficulty.ToString()!,
            },
            syncReport: syncReport,
            logManager: LimboLogs.Instance);

        feed.InitializeFeed();
        while (true)
        {
            var batch = await feed.PrepareRequest();
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

        ISyncReport syncReport = Substitute.For<ISyncReport>();
        syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
        syncReport.HeadersInQueue.Returns(new MeasuredProgress());

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());

        ManualResetEventSlim hangLatch = new(false);
        BlockHeader pivot = remoteBlockTree.FindHeader(1000, BlockTreeLookupOptions.None)!;
        ResettableHeaderSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: syncPeerPool,
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = "1000",
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

        ISyncReport syncReport = Substitute.For<ISyncReport>();
        syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
        syncReport.HeadersInQueue.Returns(new MeasuredProgress());

        BlockHeader pivot = remoteBlockTree.FindHeader(500, BlockTreeLookupOptions.None)!;
        using ResettableHeaderSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = "500",
                PivotHash = pivot.Hash!.Bytes.ToHexString(),
                PivotTotalDifficulty = pivot.TotalDifficulty!.ToString()!
            },
            syncReport: syncReport,
            logManager: LimboLogs.Instance);

        feed.InitializeFeed();

        void FulfillBatch(HeadersSyncBatch batch)
        {
            batch.Response = remoteBlockTree.FindHeaders(
                remoteBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash, batch.RequestSize, 0,
                false)!;
        }

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

        ISyncReport syncReport = Substitute.For<ISyncReport>();
        syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
        syncReport.HeadersInQueue.Returns(new MeasuredProgress());

        BlockHeader pivot = remoteBlockTree.FindHeader(2000, BlockTreeLookupOptions.None)!;
        using HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = pivot.Number.ToString(),
                PivotHash = pivot.Hash!.ToString(),
                PivotTotalDifficulty = pivot.TotalDifficulty.ToString()!,
            },
            syncReport: syncReport,
            logManager: LimboLogs.Instance);

        feed.InitializeFeed();

        void FulfillBatch(HeadersSyncBatch batch)
        {
            batch.Response = remoteBlockTree.FindHeaders(
                remoteBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash, batch.RequestSize, 0,
                false)!;
        }

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

        // HandleDependantBatch would start from first batch in batches, stopped at second last, not processing the last one
        using HeadersSyncBatch newBatch = (await feed.PrepareRequest())!;
        blockTree.LowestInsertedHeader!.Number.Should().Be(batches[^2].StartNumber);

        // New batch would be at end of batch 5 (batch 6).
        newBatch.EndNumber.Should().Be(batches[^1].StartNumber - 1);
        batches.DisposeItems();
    }

    [Test]
    public async Task Can_reset_and_not_hang_when_a_batch_is_processing()
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(501).TestObject;

        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;

        ISyncReport syncReport = Substitute.For<ISyncReport>();
        syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
        syncReport.HeadersInQueue.Returns(new MeasuredProgress());

        ManualResetEventSlim hangLatch = new(false);

        BlockHeader pivot = remoteBlockTree.FindHeader(500, BlockTreeLookupOptions.None)!;
        ResettableHeaderSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = "500",
                PivotHash = pivot.Hash!.Bytes.ToHexString(),
                PivotTotalDifficulty = pivot.TotalDifficulty!.ToString()!
            },
            syncReport: syncReport,
            logManager: LimboLogs.Instance,
            hangOnBlockNumber: 400,
            hangLatch: hangLatch
        );

        feed.InitializeFeed();

        void FulfillBatch(HeadersSyncBatch batch)
        {
            batch.Response = remoteBlockTree.FindHeaders(
                remoteBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash, batch.RequestSize, 0,
                false)!;
        }

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
        blockTree.LowestInsertedHeader!.Number.Should().Be(batch2.StartNumber);
    }

    [Test]
    public async Task Can_keep_returning_nulls_after_all_batches_were_prepared()
    {
        BlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;
        HeadersSyncFeed feed = new(
            blockTree: blockTree,
            syncPeerPool: Substitute.For<ISyncPeerPool>(),
            syncConfig: new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = "1000",
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            syncReport: Substitute.For<ISyncReport>(),
            logManager: LimboLogs.Instance);

        for (int i = 0; i < 10; i++)
        {
            await feed.PrepareRequest();
        }

        using HeadersSyncBatch? result = await feed.PrepareRequest();
        result.Should().BeNull();
    }

    [Test]
    public async Task Finishes_when_all_downloaded()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1000).TestObject);
        ISyncReport report = Substitute.For<ISyncReport>();
        report.HeadersInQueue.Returns(new MeasuredProgress());
        MeasuredProgress measuredProgress = new();
        report.FastBlocksHeaders.Returns(measuredProgress);
        HeadersSyncFeed feed = new(blockTree, Substitute.For<ISyncPeerPool>(), new TestSyncConfig { FastSync = true, PivotNumber = "1000", PivotHash = Keccak.Zero.ToString(), PivotTotalDifficulty = "1000" }, report, LimboLogs.Instance);
        await feed.PrepareRequest();
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
        using HeadersSyncBatch? result = await feed.PrepareRequest();

        result.Should().BeNull();
        feed.CurrentState.Should().Be(SyncFeedState.Finished);
        measuredProgress.HasEnded.Should().BeTrue();
    }

    [Test]
    public async Task Can_resume_downloading_from_parent_of_lowest_inserted_header()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader
            .WithNumber(500)
            .WithTotalDifficulty(10_000_000)
            .TestObject);

        ISyncReport report = Substitute.For<ISyncReport>();
        report.HeadersInQueue.Returns(new MeasuredProgress());
        report.FastBlocksHeaders.Returns(new MeasuredProgress());

        HeadersSyncFeed feed = new(blockTree, Substitute.For<ISyncPeerPool>(), new TestSyncConfig { FastSync = true, PivotNumber = "1000", PivotHash = Keccak.Zero.ToString(), PivotTotalDifficulty = "1000" }, report, LimboLogs.Instance);
        feed.InitializeFeed();
        using HeadersSyncBatch? result = await feed.PrepareRequest();

        result.Should().NotBeNull();
        result!.EndNumber.Should().Be(499);
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
    //All empty reponse
    [TestCase(0, 192, 1, false, false)]
    //All null reponse
    [TestCase(0, 192, 1, false, true)]
    public async Task Can_insert_all_good_headers_from_dependent_batch_with_missing_or_null_headers(int nullIndex, int count, int increment, bool shouldReport, bool useNulls)
    {
        var peerChain = CachedBlockTreeBuilder.OfLength(1000);
        var pivotHeader = peerChain.FindHeader(998)!;
        var syncConfig = new TestSyncConfig { FastSync = true, PivotNumber = pivotHeader.Number.ToString(), PivotHash = pivotHeader.Hash!.ToString(), PivotTotalDifficulty = pivotHeader.TotalDifficulty.ToString()! };

        IBlockTree localBlockTree = Build.A.BlockTree(peerChain.FindBlock(0, BlockTreeLookupOptions.None)!, null).WithSyncConfig(syncConfig).TestObject;
        const int lowestInserted = 999;
        localBlockTree.Insert(peerChain.Head!, BlockTreeInsertBlockOptions.SaveHeader);

        ISyncReport report = Substitute.For<ISyncReport>();
        report.HeadersInQueue.Returns(new MeasuredProgress());
        report.FastBlocksHeaders.Returns(new MeasuredProgress());

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        using HeadersSyncFeed feed = new(localBlockTree, syncPeerPool, syncConfig, report, new TestLogManager(LogLevel.Trace));
        feed.InitializeFeed();
        using HeadersSyncBatch? firstBatch = await feed.PrepareRequest();
        using HeadersSyncBatch? dependentBatch = await feed.PrepareRequest();
        dependentBatch!.ResponseSourcePeer = new PeerInfo(Substitute.For<ISyncPeer>());

        void FillBatch(HeadersSyncBatch batch, long start, bool applyNulls)
        {
            int c = count;
            List<BlockHeader?> list = Enumerable.Range((int)start, batch.RequestSize)
                .Select(i => peerChain.FindBlock(i, BlockTreeLookupOptions.None)!.Header)
                .ToList<BlockHeader?>();
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

        FillBatch(firstBatch!, lowestInserted - firstBatch!.RequestSize, false);
        FillBatch(dependentBatch, lowestInserted - dependentBatch.RequestSize * 2, true);
        long targetHeaderInDependentBatch = dependentBatch.StartNumber;

        feed.HandleResponse(dependentBatch);
        feed.HandleResponse(firstBatch);

        using HeadersSyncBatch? thirdbatch = await feed.PrepareRequest();
        FillBatch(thirdbatch!, thirdbatch!.StartNumber, false);
        feed.HandleResponse(thirdbatch);
        using HeadersSyncBatch? fourthbatch = await feed.PrepareRequest();
        FillBatch(fourthbatch!, fourthbatch!.StartNumber, false);
        feed.HandleResponse(fourthbatch);
        using HeadersSyncBatch? fifthbatch = await feed.PrepareRequest();

        Assert.That(localBlockTree.LowestInsertedHeader!.Number, Is.LessThanOrEqualTo(targetHeaderInDependentBatch));
        syncPeerPool.Received(shouldReport ? 1 : 0).ReportBreachOfProtocol(Arg.Any<PeerInfo>(), Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public async Task Does_not_download_persisted_header()
    {
        var peerChain = CachedBlockTreeBuilder.OfLength(1000);
        var pivotHeader = peerChain.FindHeader(999)!;
        var syncConfig = new TestSyncConfig { FastSync = true, PivotNumber = pivotHeader.Number.ToString(), PivotHash = pivotHeader.Hash!.ToString(), PivotTotalDifficulty = pivotHeader.TotalDifficulty.ToString()! };

        IBlockTree localBlockTree = Build.A.BlockTree(peerChain.FindBlock(0, BlockTreeLookupOptions.None)!, null).WithSyncConfig(syncConfig).TestObject;

        // Insert some chain
        for (int i = 0; i < 600; i++)
        {
            localBlockTree.SuggestHeader(peerChain.FindHeader(i)!).Should().Be(AddBlockResult.Added);
        }

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        ISyncReport report = Substitute.For<ISyncReport>();
        report.HeadersInQueue.Returns(new MeasuredProgress());
        report.FastBlocksHeaders.Returns(new MeasuredProgress());
        using HeadersSyncFeed feed = new(localBlockTree, syncPeerPool, syncConfig, report, new TestLogManager(LogLevel.Trace));
        feed.InitializeFeed();

        void FillBatch(HeadersSyncBatch batch)
        {
            batch.Response = Enumerable.Range((int)batch.StartNumber!, batch.RequestSize)
                .Select(i => peerChain.FindBlock(i, BlockTreeLookupOptions.None)!.Header)
                .ToPooledList<BlockHeader?>(batch.RequestSize);
        }

        using HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
        batch1.StartNumber.Should().Be(808);

        using HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;
        batch2.StartNumber.Should().Be(616);

        using HeadersSyncBatch batch3 = (await feed.PrepareRequest())!;
        batch3.StartNumber.Should().Be(424);

        (await feed.PrepareRequest()).Should().Be(null);

        FillBatch(batch1);
        FillBatch(batch2);
        FillBatch(batch3);

        feed.HandleResponse(batch1);
        feed.HandleResponse(batch2);
        feed.HandleResponse(batch3);

        // The dependency batch is processed during prepare request.
        (await feed.PrepareRequest()).Should().Be(null);

        localBlockTree.LowestInsertedHeader?.Number.Should().Be(0);
    }

    [Test]
    public async Task Will_never_lose_batch_on_invalid_batch()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1000).TestObject);
        ISyncReport report = Substitute.For<ISyncReport>();
        report.HeadersInQueue.Returns(new MeasuredProgress());
        MeasuredProgress measuredProgress = new();
        report.FastBlocksHeaders.Returns(measuredProgress);
        HeadersSyncFeed feed = new(
            blockTree,
            Substitute.For<ISyncPeerPool>(),
            new TestSyncConfig
            {
                FastSync = true,
                PivotNumber = "1000",
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            }, report, LimboLogs.Instance);
        feed.InitializeFeed();

        List<HeadersSyncBatch> batches = new();
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

        batches.Count.Should().Be(totalBatchCount);
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
            PivotNumber = "1",
        };

        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(2).WithStateRoot(TestItem.KeccakA).TestObject);

        HeadersSyncFeed feed = new(
            blockTree,
            Substitute.For<ISyncPeerPool>(),
            syncConfig,
            Substitute.For<ISyncReport>(),
            LimboLogs.Instance);

        Assert.That(feed.IsFinished, Is.False);
    }

    private class ResettableHeaderSyncFeed : HeadersSyncFeed
    {
        private readonly ManualResetEventSlim? _hangLatch;
        private readonly long? _hangOnBlockNumber;
        private readonly long? _hangOnBlockNumberAfterInsert;

        public ResettableHeaderSyncFeed(
            IBlockTree? blockTree,
            ISyncPeerPool? syncPeerPool,
            ISyncConfig? syncConfig,
            ISyncReport? syncReport,
            ILogManager? logManager,
            long? hangOnBlockNumber = null,
            long? hangOnBlockNumberAfterInsert = null,
            ManualResetEventSlim? hangLatch = null,
            bool alwaysStartHeaderSync = false
        ) : base(blockTree, syncPeerPool, syncConfig, syncReport, logManager, alwaysStartHeaderSync: alwaysStartHeaderSync)
        {
            _hangOnBlockNumber = hangOnBlockNumber;
            _hangOnBlockNumberAfterInsert = hangOnBlockNumberAfterInsert;
            _hangLatch = hangLatch;
        }

        public void Reset()
        {
            base.PostFinishCleanUp();
            InitializeFeed();
        }

        protected override AddBlockResult InsertToBlockTree(BlockHeader header)
        {
            if (header.Number == _hangOnBlockNumber)
            {
                _hangLatch!.Wait();
            }

            AddBlockResult insertOutcome = _blockTree.Insert(header);
            if (header.Number == _hangOnBlockNumberAfterInsert)
            {
                _hangLatch!.Wait();
            }
            if (insertOutcome is AddBlockResult.Added or AddBlockResult.AlreadyKnown)
            {
                SetExpectedNextHeaderToParent(header);
            }

            return insertOutcome;
        }
    }

}
