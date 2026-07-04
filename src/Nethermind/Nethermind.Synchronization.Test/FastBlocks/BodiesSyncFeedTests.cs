// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.History;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastBlocks;

public class BodiesSyncFeedTests
{
    private IBlockTree _syncingFromBlockTree;
    private IBlockTree _syncingToBlockTree;
    private TestMemDb _blocksDb;
    private ISyncPointers _syncPointers;
    private BodiesSyncFeed _feed;
    private ISyncConfig _syncConfig;
    private MemDb _metadataDb;
    private Block _pivotBlock;
    private ISyncPeerPool _syncPeerPool;
    private IHistoryPruner _historyPruner;

    [SetUp]
    public void Setup()
    {
        _syncingFromBlockTree = Build.A.BlockTree()
            .WithTransactions(new InMemoryReceiptStorage())
            .OfChainLength(100)
            .TestObject;

        _blocksDb = new TestMemDb();
        _metadataDb = new MemDb();
        _syncPointers = new MemorySyncPointers();
        _syncingToBlockTree = Build.A.BlockTree()
            .WithBlocksDb(_blocksDb)
            .TestObject;

        for (ulong i = 1; i < 100; i++)
        {
            Block block = _syncingFromBlockTree.FindBlock(i, BlockTreeLookupOptions.None)!;
            _syncingToBlockTree.Insert(block.Header);
        }

        _pivotBlock = _syncingFromBlockTree.FindBlock(99, BlockTreeLookupOptions.None)!;

        _syncConfig = new TestSyncConfig()
        {
            FastSync = true,
            PivotHash = _pivotBlock.Hash!.ToString(),
            PivotNumber = _pivotBlock.Number,
            AncientBodiesBarrier = 0,
            DownloadBodiesInFastSync = true,
        };
        _syncingToBlockTree.SyncPivot = (_pivotBlock.Number, _pivotBlock.Hash);

        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _historyPruner = Substitute.For<IHistoryPruner>();
        _feed = new BodiesSyncFeed(
            MainnetSpecProvider.Instance,
            _syncingToBlockTree,
            CreateBlockValidator(),
            _syncPointers,
            _syncPeerPool,
            _syncConfig,
            new NullSyncReport(),
            _historyPruner,
            _blocksDb,
            _metadataDb,
            LimboLogs.Instance,
            flushDbInterval: 10
        );
    }

    private static BlockValidator CreateBlockValidator() =>
        new(Always.Valid, Always.Valid, Always.Valid, MainnetSpecProvider.Instance, LimboLogs.Instance);

    [TearDown]
    public void TearDown()
    {
        _blocksDb?.Dispose();
        _feed?.Dispose();
        _metadataDb?.Dispose();
        _syncPeerPool?.DisposeAsync();
    }

    [Test]
    public async Task ShouldCallFlushPeriodically()
    {
        _feed.InitializeFeed();
        BodiesSyncBatch req = (await _feed.PrepareRequest())!;
        Assert.That(_blocksDb.FlushCount, Is.EqualTo(1));

        async Task HandleAndPrepareNextRequest()
        {
            req.Response = RlpBlockBodies.FromBodies(req.Infos.Take(8).Select((info) =>
                _syncingFromBlockTree.FindBlock(info!.BlockNumber, BlockTreeLookupOptions.None)!.Body).ToArray());

            _feed.HandleResponse(req);
            req.Dispose();

            req = (await _feed.PrepareRequest())!;
        }

        await HandleAndPrepareNextRequest();
        Assert.That(_blocksDb.FlushCount, Is.EqualTo(1));

        await HandleAndPrepareNextRequest();
        Assert.That(_blocksDb.FlushCount, Is.EqualTo(2));

        await HandleAndPrepareNextRequest();
        Assert.That(_blocksDb.FlushCount, Is.EqualTo(2));

        await HandleAndPrepareNextRequest();
        Assert.That(_blocksDb.FlushCount, Is.EqualTo(3));
        req.Dispose();
    }

    [Test]
    public async Task ShouldNotReDownloadExistingBlock()
    {
        _feed.InitializeFeed();

        _syncingToBlockTree.Insert(_syncingFromBlockTree.FindBlock(_pivotBlock.Number - 2)!);
        _syncingToBlockTree.Insert(_syncingFromBlockTree.FindBlock(_pivotBlock.Number - 4)!);

        using BodiesSyncBatch req = (await _feed.PrepareRequest())!;
        Assert.That(req.Infos
            .Where(static (bi) => bi is not null)
            .Select(static (bi) => bi!.BlockNumber)
            .Take(4), Is.EqualTo([
                _pivotBlock.Number,
                _pivotBlock.Number - 1,
                // Skipped
                _pivotBlock.Number - 3,
                // Skipped
                _pivotBlock.Number - 5]));
    }

    [Test]
    public async Task ShouldHandleSparseBodyResponseWithoutReportingBreach()
    {
        _feed.InitializeFeed();

        BodiesSyncBatch req = (await _feed.PrepareRequest())!;
        Block firstBlock = _syncingFromBlockTree.FindBlock(req.Infos[0]!.BlockNumber, BlockTreeLookupOptions.None)!;
        Block skippedBlock = _syncingFromBlockTree.FindBlock(req.Infos[1]!.BlockNumber, BlockTreeLookupOptions.None)!;
        Block thirdBlock = _syncingFromBlockTree.FindBlock(req.Infos[2]!.BlockNumber, BlockTreeLookupOptions.None)!;
        req.Response = RlpBlockBodies.FromBodies([firstBlock.Body, thirdBlock.Body]);
        req.ResponseSourcePeer = new PeerInfo(Substitute.For<ISyncPeer>());

        SyncResponseHandlingResult result = _feed.HandleResponse(req);

        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK));
        Assert.That(_syncingToBlockTree.FindBlock(firstBlock.Hash!, BlockTreeLookupOptions.None, firstBlock.Number), Is.Not.Null);
        Assert.That(_syncingToBlockTree.FindBlock(skippedBlock.Hash!, BlockTreeLookupOptions.None, skippedBlock.Number), Is.Null);
        Assert.That(_syncingToBlockTree.FindBlock(thirdBlock.Hash!, BlockTreeLookupOptions.None, thirdBlock.Number), Is.Not.Null);
        _syncPeerPool.DidNotReceive().ReportBreachOfProtocol(
            Arg.Any<PeerInfo>(),
            Arg.Any<DisconnectReason>(),
            Arg.Any<string>());
    }

    [Test]
    public async Task ShouldMarkMalformedBodyPendingAndReportBreach()
    {
        _feed.InitializeFeed();

        BodiesSyncBatch req = (await _feed.PrepareRequest())!;
        Block firstBlock = _syncingFromBlockTree.FindBlock(req.Infos[0]!.BlockNumber, BlockTreeLookupOptions.None)!;
        Block secondBlock = _syncingFromBlockTree.FindBlock(req.Infos[1]!.BlockNumber, BlockTreeLookupOptions.None)!;

        // Structurally-valid body whose single "transaction" has a non-canonical length prefix (0xb8 0x01),
        // so it fails raw validation without matching any requested header.
        byte[] corrupt = Bytes.FromHexString("0xc5c3b80100c0");

        req.Response = new RlpBlockBodies(
            [RlpBlockBody.FromBody(firstBlock.Body), RlpBlockBody.FromBodyItem(MemoryPool<byte>.Shared.Rent(0), corrupt)],
            null);
        req.ResponseSourcePeer = new PeerInfo(Substitute.For<ISyncPeer>());

        SyncResponseHandlingResult result = _feed.HandleResponse(req);

        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK));
        Assert.That(_syncingToBlockTree.FindBlock(firstBlock.Hash!, BlockTreeLookupOptions.None, firstBlock.Number), Is.Not.Null);
        Assert.That(_syncingToBlockTree.FindBlock(secondBlock.Hash!, BlockTreeLookupOptions.None, secondBlock.Number), Is.Null);
        _syncPeerPool.Received().ReportBreachOfProtocol(
            Arg.Any<PeerInfo>(),
            DisconnectReason.InvalidTxOrUncle,
            Arg.Any<string>());
        req.Dispose();
    }

    [Test]
    public async Task ShouldRecoverOnInsertFailure()
    {
        _feed.InitializeFeed();
        using BodiesSyncBatch req = (await _feed.PrepareRequest())!;

        req.Response = RlpBlockBodies.FromBodies(req.Infos.Take(8).Select((info) =>
            _syncingFromBlockTree.FindBlock(info!.BlockNumber, BlockTreeLookupOptions.None)!.Body).ToArray());

        int writeCount = 0;
        _blocksDb.WriteFunc = (k, value) =>
        {
            writeCount++;
            if (writeCount == 5)
                throw new Exception("test failure");
            return true;
        };

        Func<SyncResponseHandlingResult> act = () => _feed.HandleResponse(req);
        Assert.That(act, Throws.TypeOf<Exception>());

        using BodiesSyncBatch req2 = (await _feed.PrepareRequest())!;
        Assert.That(req2.Infos[0]!.BlockNumber, Is.EqualTo(95));
    }

    [TestCase(1UL, 99UL, false, null, false)]
    [TestCase(1UL, 99UL, true, null, false)]
    [TestCase(1UL, 99UL, false, 0L, false)]
    public void When_finished_sync_with_old_default_barrier_then_finishes_immediately(
            ulong AncientBarrierInConfig,
            ulong lowestInsertedBlockNumber,
            bool JustStarted,
            long? previousBarrierInDb,
            bool shouldFinish)
    {
        _syncConfig.AncientBodiesBarrier = AncientBarrierInConfig;
        _syncConfig.AncientReceiptsBarrier = AncientBarrierInConfig;
        _syncConfig.PivotNumber = AncientBarrierInConfig + 1_000_000;
        _syncPointers.LowestInsertedBodyNumber = JustStarted ? null : _pivotBlock.Number;
        if (previousBarrierInDb is not null)
            _metadataDb.Set(MetadataDbKeys.BodiesBarrierWhenStarted, previousBarrierInDb.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        _feed.InitializeFeed();
        _syncPointers.LowestInsertedBodyNumber = lowestInsertedBlockNumber;

        Assert.That(_feed.IsFinished, Is.EqualTo(shouldFinish));
    }

    [Test]
    public async Task When_AncientBodiesBarrier_exceeds_SyncPivot_then_finishes_immediately()
    {
        _syncConfig.PivotNumber = 0;
        _syncConfig.AncientBodiesBarrier = 4_367_322;

        _feed.InitializeFeed();
        using BodiesSyncBatch? _ = await _feed.PrepareRequest();

        Assert.That(_feed.IsFinished, Is.True);
    }

    // Regression for #9002: decreasing AncientBodiesBarrier after a partial sync must not leave the feed stuck.
    [Test]
    public async Task When_AncientBodiesBarrier_decreased_after_partial_sync_feed_resumes_download()
    {
        // Previous run downloaded bodies from pivot (99) down to block 60.
        for (ulong i = 60; i <= 99; i++)
        {
            _syncingToBlockTree.Insert(_syncingFromBlockTree.FindBlock(i, BlockTreeLookupOptions.None)!);
        }
        _syncPointers.LowestInsertedBodyNumber = 60;

        // Restart with a lower barrier — blocks 40..59 still need downloading.
        _syncConfig.AncientBodiesBarrier = 40;
        _feed.InitializeFeed();

        Assert.That(_feed.IsFinished, Is.False);

        using BodiesSyncBatch? batch = await _feed.PrepareRequest();
        Assert.That(batch, Is.Not.Null);
        foreach (BlockInfo? info in batch!.Infos.Where(static i => i is not null))
        {
            Assert.That(info!.BlockNumber, Is.InRange(40, 59));
        }
    }

    [Test]
    public async Task ShouldLimitBatchSizeToPeerEstimate()
    {
        _feed.InitializeFeed();
        _syncPeerPool.EstimateRequestLimit(RequestType.Bodies, Arg.Any<IPeerAllocationStrategy>(), AllocationContexts.Bodies, default)
            .Returns(Task.FromResult<int?>(5));
        BodiesSyncBatch req = (await _feed.PrepareRequest())!;
        Assert.That(req.Infos.Length, Is.EqualTo(5));
    }
}
