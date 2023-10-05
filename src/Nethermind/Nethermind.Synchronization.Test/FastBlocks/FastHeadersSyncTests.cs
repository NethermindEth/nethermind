// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test.FastBlocks
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class FastHeadersSyncTests
    {
        [Test]
        public async Task Will_fail_if_launched_without_fast_blocks_enabled()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree blockTree = new(
                blockDb: memDbProvider.BlocksDb,
                headerDb: memDbProvider.HeadersDb,
                blockInfoDb: memDbProvider.BlockInfosDb,
                chainLevelInfoRepository: new ChainLevelInfoRepository(memDbProvider.BlockInfosDb),
                specProvider: MainnetSpecProvider.Instance,
                bloomStorage: NullBloomStorage.Instance,
                logManager: LimboLogs.Instance);

            Assert.Throws<InvalidOperationException>(() =>
            {
                HeadersSyncFeed _ = new HeadersSyncFeed(
                    syncModeSelector: Substitute.For<ISyncModeSelector>(),
                    blockTree: blockTree,
                    syncPeerPool: Substitute.For<ISyncPeerPool>(),
                    syncConfig: new SyncConfig(),
                    syncReport: Substitute.For<ISyncReport>(),
                    logManager: LimboLogs.Instance);
            });
        }

        [Test]
        public async Task Can_prepare_3_requests_in_a_row()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree blockTree = new(
                blockDb: memDbProvider.BlocksDb,
                headerDb: memDbProvider.HeadersDb,
                blockInfoDb: memDbProvider.BlockInfosDb,
                chainLevelInfoRepository: new ChainLevelInfoRepository(memDbProvider.BlockInfosDb),
                specProvider: MainnetSpecProvider.Instance,
                bloomStorage: NullBloomStorage.Instance,
                logManager: LimboLogs.Instance);
            HeadersSyncFeed feed = new(
                syncModeSelector: Substitute.For<ISyncModeSelector>(),
                blockTree: blockTree,
                syncPeerPool: Substitute.For<ISyncPeerPool>(),
                syncConfig: new SyncConfig
                {
                    FastSync = true,
                    FastBlocks = true,
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
        public async Task When_next_header_hash_update_is_delayed_do_not_drop_peer()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(1001).TestObject;

            BlockTree blockTree = new(memDbProvider.BlocksDb, memDbProvider.HeadersDb, memDbProvider.BlockInfosDb, new ChainLevelInfoRepository(memDbProvider.BlockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);

            ISyncReport syncReport = Substitute.For<ISyncReport>();
            syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
            syncReport.HeadersInQueue.Returns(new MeasuredProgress());

            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());

            ManualResetEventSlim hangLatch = new(false);
            BlockHeader pivot = remoteBlockTree.FindHeader(1000, BlockTreeLookupOptions.None)!;
            ResettableHeaderSyncFeed feed = new(
                syncModeSelector: Substitute.For<ISyncModeSelector>(),
                blockTree: blockTree,
                syncPeerPool: syncPeerPool,
                syncConfig: new SyncConfig
                {
                    FastSync = true,
                    FastBlocks = true,
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
                    false);
                batch.ResponseSourcePeer = peerInfo;
            }

            HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
            HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;
            HeadersSyncBatch batch3 = (await feed.PrepareRequest())!;
            HeadersSyncBatch batch4 = (await feed.PrepareRequest())!;

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

            syncPeerPool.DidNotReceive().ReportBreachOfProtocol(peerInfo, Arg.Any<InitiateDisconnectReason>(), Arg.Any<string>());
        }

        [Test]
        public async Task Can_prepare_several_request_and_ignore_request_from_previous_sequence()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(501).TestObject;

            BlockTree blockTree = new(memDbProvider.BlocksDb, memDbProvider.HeadersDb, memDbProvider.BlockInfosDb, new ChainLevelInfoRepository(memDbProvider.BlockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);

            ISyncReport syncReport = Substitute.For<ISyncReport>();
            syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
            syncReport.HeadersInQueue.Returns(new MeasuredProgress());

            BlockHeader pivot = remoteBlockTree.FindHeader(500, BlockTreeLookupOptions.None)!;
            ResettableHeaderSyncFeed feed = new(
                syncModeSelector: Substitute.For<ISyncModeSelector>(),
                blockTree: blockTree,
                syncPeerPool: Substitute.For<ISyncPeerPool>(),
                syncConfig: new SyncConfig
                {
                    FastSync = true,
                    FastBlocks = true,
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
                    false);
            }

            await feed.PrepareRequest();
            HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
            FulfillBatch(batch1);

            feed.Reset();

            await feed.PrepareRequest();
            HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;
            FulfillBatch(batch2);

            feed.HandleResponse(batch2);
            feed.HandleResponse(batch1);
        }

        [Test]
        public async Task Will_dispatch_when_only_partially_processed_dependency()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(2001).TestObject;

            BlockTree blockTree = new(
                blockDb: memDbProvider.BlocksDb,
                headerDb: memDbProvider.HeadersDb,
                blockInfoDb: memDbProvider.BlockInfosDb,
                chainLevelInfoRepository: new ChainLevelInfoRepository(memDbProvider.BlockInfosDb),
                specProvider: MainnetSpecProvider.Instance,
                bloomStorage: NullBloomStorage.Instance,
                logManager: LimboLogs.Instance);

            ISyncReport syncReport = Substitute.For<ISyncReport>();
            syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
            syncReport.HeadersInQueue.Returns(new MeasuredProgress());

            BlockHeader pivot = remoteBlockTree.FindHeader(2000, BlockTreeLookupOptions.None)!;
            HeadersSyncFeed feed = new(
                syncModeSelector: Substitute.For<ISyncModeSelector>(),
                blockTree: blockTree,
                syncPeerPool: Substitute.For<ISyncPeerPool>(),
                syncConfig: new SyncConfig
                {
                    FastSync = true,
                    FastBlocks = true,
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
                    false);
            }

            // First batch need to be handled first before handle dependencies can do anything
            HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
            FulfillBatch(batch1);
            feed.HandleResponse(batch1);

            HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;
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
            HeadersSyncBatch newBatch = (await feed.PrepareRequest())!;
            blockTree.LowestInsertedHeader!.Number.Should().Be(batches[^2].StartNumber);

            // New batch would be at end of batch 5 (batch 6).
            newBatch.EndNumber.Should().Be(batches[^1].StartNumber - 1);
        }

        [Test]
        public async Task Can_reset_and_not_hang_when_a_batch_is_processing()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(501).TestObject;

            BlockTree blockTree = new(
                blockDb: memDbProvider.BlocksDb,
                headerDb: memDbProvider.HeadersDb,
                blockInfoDb: memDbProvider.BlockInfosDb,
                chainLevelInfoRepository: new ChainLevelInfoRepository(memDbProvider.BlockInfosDb),
                specProvider: MainnetSpecProvider.Instance,
                bloomStorage: NullBloomStorage.Instance,
                logManager: LimboLogs.Instance);

            ISyncReport syncReport = Substitute.For<ISyncReport>();
            syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
            syncReport.HeadersInQueue.Returns(new MeasuredProgress());

            ManualResetEventSlim hangLatch = new(false);

            BlockHeader pivot = remoteBlockTree.FindHeader(500, BlockTreeLookupOptions.None)!;
            ResettableHeaderSyncFeed feed = new(
                syncModeSelector: Substitute.For<ISyncModeSelector>(),
                blockTree: blockTree,
                syncPeerPool: Substitute.For<ISyncPeerPool>(),
                syncConfig: new SyncConfig
                {
                    FastSync = true,
                    FastBlocks = true,
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
                    false);
            }

            HeadersSyncBatch batch1 = (await feed.PrepareRequest())!;
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
            HeadersSyncBatch batch2 = (await feed.PrepareRequest())!;

            FulfillBatch(batch2);
            feed.HandleResponse(batch2);

            // The whole new batch should get processed instead of skipping due to concurrently modified _nextHeaderHash.
            blockTree.LowestInsertedHeader!.Number.Should().Be(batch2.StartNumber);
        }

        [Test]
        public async Task Can_keep_returning_nulls_after_all_batches_were_prepared()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree blockTree = new(
                blockDb: memDbProvider.BlocksDb,
                headerDb: memDbProvider.HeadersDb,
                blockInfoDb: memDbProvider.BlockInfosDb,
                chainLevelInfoRepository: new ChainLevelInfoRepository(memDbProvider.BlockInfosDb),
                specProvider: MainnetSpecProvider.Instance,
                bloomStorage: NullBloomStorage.Instance,
                logManager: LimboLogs.Instance);
            HeadersSyncFeed feed = new(
                syncModeSelector: Substitute.For<ISyncModeSelector>(),
                blockTree: blockTree,
                syncPeerPool: Substitute.For<ISyncPeerPool>(),
                syncConfig: new SyncConfig
                {
                    FastSync = true,
                    FastBlocks = true,
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

            HeadersSyncBatch? result = await feed.PrepareRequest();
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
            HeadersSyncFeed feed = new(Substitute.For<ISyncModeSelector>(), blockTree, Substitute.For<ISyncPeerPool>(), new SyncConfig { FastSync = true, FastBlocks = true, PivotNumber = "1000", PivotHash = Keccak.Zero.ToString(), PivotTotalDifficulty = "1000" }, report, LimboLogs.Instance);
            await feed.PrepareRequest();
            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
            HeadersSyncBatch? result = await feed.PrepareRequest();

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

            HeadersSyncFeed feed = new(Substitute.For<ISyncModeSelector>(), blockTree, Substitute.For<ISyncPeerPool>(), new SyncConfig { FastSync = true, FastBlocks = true, PivotNumber = "1000", PivotHash = Keccak.Zero.ToString(), PivotTotalDifficulty = "1000" }, report, LimboLogs.Instance);
            feed.InitializeFeed();
            HeadersSyncBatch? result = await feed.PrepareRequest();

            result.Should().NotBeNull();
            result!.EndNumber.Should().Be(499);
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
                Substitute.For<ISyncModeSelector>(),
                blockTree,
                Substitute.For<ISyncPeerPool>(),
                new SyncConfig
                {
                    FastSync = true,
                    FastBlocks = true,
                    PivotNumber = "1000",
                    PivotHash = Keccak.Zero.ToString(),
                    PivotTotalDifficulty = "1000"
                }, report, LimboLogs.Instance);
            feed.InitializeFeed();

            List<HeadersSyncBatch> batches = new();
            while (true)
            {
                HeadersSyncBatch? batch = await feed.PrepareRequest();
                if (batch == null) break;
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
                    if (batch == null)
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
                headersSyncBatch.Response = new[] { randomBlockHeader };
                feed.HandleResponse(headersSyncBatch);
            }

            await requestTasks;

            while (true)
            {
                HeadersSyncBatch? batch = await feed.PrepareRequest();
                if (batch == null) break;
                batches.Add(batch);
            }

            batches.Count.Should().Be(totalBatchCount);
        }

        private class ResettableHeaderSyncFeed : HeadersSyncFeed
        {
            private readonly ManualResetEventSlim? _hangLatch;
            private readonly long? _hangOnBlockNumber;
            private readonly long? _hangOnBlockNumberAfterInsert;

            public ResettableHeaderSyncFeed(
                ISyncModeSelector syncModeSelector,
                IBlockTree? blockTree,
                ISyncPeerPool? syncPeerPool,
                ISyncConfig? syncConfig,
                ISyncReport? syncReport,
                ILogManager? logManager,
                long? hangOnBlockNumber = null,
                long? hangOnBlockNumberAfterInsert = null,
                ManualResetEventSlim? hangLatch = null,
                bool alwaysStartHeaderSync = false
            ) : base(syncModeSelector, blockTree, syncPeerPool, syncConfig, syncReport, logManager, alwaysStartHeaderSync)
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
}
