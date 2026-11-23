// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
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
using Nethermind.Serialization.Rlp;
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
    private IBlockStore _syncingToBlockStore;
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
        BlockTreeBuilder syncingToBuilder = Build.A.BlockTree().WithBlocksDb(_blocksDb);
        _syncingToBlockTree = syncingToBuilder.TestObject;
        _syncingToBlockStore = syncingToBuilder.BlockStore;

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
        _feed = CreateFeed(CreateBlockValidator());
    }

    private static BlockValidator CreateBlockValidator() =>
        new(Always.Valid, Always.Valid, Always.Valid, MainnetSpecProvider.Instance, LimboLogs.Instance);

    private BodiesSyncFeed CreateFeed(IBlockValidator blockValidator, ISyncReport? syncReport = null) =>
        new(
            MainnetSpecProvider.Instance,
            _syncingToBlockTree,
            blockValidator,
            _syncPointers,
            _syncPeerPool,
            _syncConfig,
            syncReport ?? new NullSyncReport(),
            _historyPruner,
            _blocksDb,
            _metadataDb,
            LimboLogs.Instance,
            flushDbInterval: 10
        );

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
            req.Response = new OwnedBlockBodies(req.Infos.Take(8).Select((info) =>
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
    public async Task Saves_body_progress_only_after_flushing_bodies_when_skipping_existing_blocks()
    {
        const ulong chainLength = 7_600;
        const ulong presentBodies = 7_400;
        using FullFlushDb blocksDb = new();
        FlushTrackingSyncPointers syncPointers = new(blocksDb);
        using BodiesSyncFeed feed = CreateFeedWithPresentBodies(blocksDb, syncPointers, chainLength, presentBodies);

        using BodiesSyncBatch? batch = await feed.PrepareRequest();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(syncPointers.LowestInsertedBodyNumber, Is.LessThan(chainLength - 1 - presentBodies / 2));
            Assert.That(syncPointers.SavedWithoutFlush, Is.False);
        }
    }

    [Test]
    public void Keeps_body_progress_when_flushing_bodies_fails()
    {
        const ulong chainLength = 7_600;
        const ulong presentBodies = 7_400;
        using FullFlushDb blocksDb = new();
        FlushTrackingSyncPointers syncPointers = new(blocksDb);
        using BodiesSyncFeed feed = CreateFeedWithPresentBodies(blocksDb, syncPointers, chainLength, presentBodies);
        blocksDb.FailFlush = true;

        Assert.That(() => feed.PrepareRequest(), Throws.InstanceOf<IOException>());
        Assert.That(syncPointers.LowestInsertedBodyNumber, Is.EqualTo(chainLength - 1));
    }

    private BodiesSyncFeed CreateFeedWithPresentBodies(FullFlushDb blocksDb, FlushTrackingSyncPointers syncPointers, ulong chainLength, ulong presentBodies)
    {
        BlockTree syncingFrom = Build.A.BlockTree().OfChainLength(chainLength).TestObject;
        Block pivot = syncingFrom.FindBlock(chainLength - 1, BlockTreeLookupOptions.None)!;

        IBlockTree syncingTo = Build.A.BlockTree().WithBlocksDb(blocksDb).TestObject;
        for (ulong i = 1; i < chainLength; i++)
        {
            Block block = syncingFrom.FindBlock(i, BlockTreeLookupOptions.None)!;
            syncingTo.Insert(block.Header);
            if (i > pivot.Number - presentBodies)
            {
                syncingTo.Insert(block);
            }
        }

        syncingTo.SyncPivot = (pivot.Number, pivot.Hash!);
        TestSyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotHash = pivot.Hash!.ToString(),
            PivotNumber = pivot.Number,
            AncientBodiesBarrier = 0,
            DownloadBodiesInFastSync = true,
        };
        blocksDb.Flush(false);
        syncPointers.LowestInsertedBodyNumber = pivot.Number;

        BodiesSyncFeed feed = new(
            MainnetSpecProvider.Instance,
            syncingTo,
            CreateBlockValidator(),
            syncPointers,
            _syncPeerPool,
            syncConfig,
            new NullSyncReport(),
            _historyPruner,
            blocksDb,
            _metadataDb,
            LimboLogs.Instance);
        feed.InitializeFeed();
        return feed;
    }

    private sealed class FullFlushDb : TestMemDb, IDbMeta
    {
        public int FullFlushCount { get; private set; }
        public bool FailFlush { get; set; }

        public override void Flush(bool onlyWal)
        {
            if (FailFlush) return;
            base.Flush(onlyWal);
            FullFlushCount += onlyWal ? 0 : 1;
        }

        void IDbMeta.FlushOrThrow()
        {
            if (FailFlush) throw new IOException("Flush failed");
            Flush(false);
        }
    }

    private sealed class FlushTrackingSyncPointers(FullFlushDb blocksDb) : ISyncPointers
    {
        private ulong? _lowestInsertedBodyNumber;
        private int _flushCountAtLastSave = blocksDb.FullFlushCount;

        public bool SavedWithoutFlush { get; private set; }

        public ulong? LowestInsertedBodyNumber
        {
            get => _lowestInsertedBodyNumber;
            set
            {
                if (value != _lowestInsertedBodyNumber)
                {
                    SavedWithoutFlush |= blocksDb.FullFlushCount == _flushCountAtLastSave;
                    _flushCountAtLastSave = blocksDb.FullFlushCount;
                }

                _lowestInsertedBodyNumber = value;
            }
        }

        public ulong? LowestInsertedReceiptBlockNumber { get; set; }
        public ulong? LowestInsertedBlockAccessListBlockNumber { get; set; }
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
    public async Task ShouldHandleSparseBodyResponseWithoutReportingBreach([Values] bool encodedBodies)
    {
        _feed.InitializeFeed();

        BodiesSyncBatch req = (await _feed.PrepareRequest())!;
        Block firstBlock = _syncingFromBlockTree.FindBlock(req.Infos[0]!.BlockNumber, BlockTreeLookupOptions.None)!;
        Block skippedBlock = _syncingFromBlockTree.FindBlock(req.Infos[1]!.BlockNumber, BlockTreeLookupOptions.None)!;
        Block thirdBlock = _syncingFromBlockTree.FindBlock(req.Infos[2]!.BlockNumber, BlockTreeLookupOptions.None)!;
        // The third body is rejected against the skipped header before it matches, so it is the body whose
        // transaction root gets cached. A differing root on the fourth makes the match below depend on that
        // cache being cleared.
        Block fourthBlock = _syncingFromBlockTree.FindBlock(req.Infos[3]!.BlockNumber, BlockTreeLookupOptions.None)!;
        Assert.That(fourthBlock.Header.TxRoot, Is.Not.EqualTo(thirdBlock.Header.TxRoot), "the two bodies must differ for this to assert anything");
        req.Response = CreateResponse(encodedBodies, firstBlock, thirdBlock, fourthBlock);
        req.ResponseSourcePeer = new PeerInfo(Substitute.For<ISyncPeer>());

        SyncResponseHandlingResult result = _feed.HandleResponse(req);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK));
            Assert.That(_syncingToBlockTree.FindBlock(firstBlock.Hash!, BlockTreeLookupOptions.None, firstBlock.Number), Is.Not.Null);
            Assert.That(_syncingToBlockTree.FindBlock(skippedBlock.Hash!, BlockTreeLookupOptions.None, skippedBlock.Number), Is.Null);
            Assert.That(_syncingToBlockTree.FindBlock(thirdBlock.Hash!, BlockTreeLookupOptions.None, thirdBlock.Number), Is.Not.Null);
            Assert.That(_syncingToBlockTree.FindBlock(fourthBlock.Hash!, BlockTreeLookupOptions.None, fourthBlock.Number), Is.Not.Null);
            Assert.That(_syncingToBlockStore.GetRlp(thirdBlock.Number, thirdBlock.Hash!), Is.EqualTo(BlockDecoder.Encode(thirdBlock)));
        }
        _syncPeerPool.DidNotReceive().ReportBreachOfProtocol(
            Arg.Any<PeerInfo>(),
            Arg.Any<DisconnectReason>(),
            Arg.Any<string>());
    }

    [Test]
    public async Task ShouldReportBreachForBodyThatDoesNotDecode([Values] bool rlpError)
    {
        _feed.InitializeFeed();

        using BodiesSyncBatch req = (await _feed.PrepareRequest())!;
        Block firstBlock = _syncingFromBlockTree.FindBlock(req.Infos[0]!.BlockNumber, BlockTreeLookupOptions.None)!;
        Block secondBlock = _syncingFromBlockTree.FindBlock(req.Infos[1]!.BlockNumber, BlockTreeLookupOptions.None)!;
        // A body whose uncles item is a byte string instead of a sequence, or one whose uncle decode fails otherwise.
        byte[] malformedBody = rlpError ? [0xc2, 0xc0, 0x80] : ThrowingHeaderDecoder.BodyWithUncle();
        req.Response = CreateEncodedResponse(
            rlpError ? BlockBodyDecoder.Instance : ThrowingHeaderDecoder.BodyDecoder,
            BlockBodyDecoder.Instance.Encode(firstBlock.Body), malformedBody);
        req.ResponseSourcePeer = new PeerInfo(Substitute.For<ISyncPeer>());

        SyncResponseHandlingResult result = _feed.HandleResponse(req);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK));
            Assert.That(_syncingToBlockTree.FindBlock(firstBlock.Hash!, BlockTreeLookupOptions.None, firstBlock.Number), Is.Not.Null);
            Assert.That(_syncingToBlockTree.FindBlock(secondBlock.Hash!, BlockTreeLookupOptions.None, secondBlock.Number), Is.Null);
        }
        _syncPeerPool.Received(1).ReportBreachOfProtocol(
            Arg.Any<PeerInfo>(),
            DisconnectReason.BreachOfProtocol,
            Arg.Any<string>());
    }

    private static readonly BlockDecoder BlockDecoder = new();

    private static OwnedBlockBodies CreateResponse(bool encodedBodies, params Block[] blocks) =>
        encodedBodies
            ? OwnedBlockBodies.FromEncodedBlocks([.. blocks.Select(static block => BlockDecoder.Encode(block))])
            : new OwnedBlockBodies([.. blocks.Select(static block => block.Body)]);

    private static OwnedBlockBodies CreateEncodedResponse(BlockBodyDecoder decoder, params byte[][] bodies)
    {
        IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(bodies.Sum(static body => body.Length));
        Range[] ranges = new Range[bodies.Length];
        int offset = 0;
        for (int i = 0; i < bodies.Length; i++)
        {
            bodies[i].CopyTo(memory.Memory[offset..]);
            ranges[i] = new Range(offset, offset + bodies[i].Length);
            offset += bodies[i].Length;
        }

        return new OwnedBlockBodies(memory, ranges, decoder);
    }

    [Test]
    public async Task ShouldValidateAnUnmatchedBodyOnceRegardlessOfRequestedHeaderCount()
    {
        IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
        blockValidator
            .ValidateBodyAgainstHeader(Arg.Any<BlockHeader>(), Arg.Any<BlockBody>(), out Arg.Any<string?>())
            .Returns(false);
        using BodiesSyncFeed feed = CreateFeed(blockValidator);
        feed.InitializeFeed();

        using BodiesSyncBatch req = (await feed.PrepareRequest())!;
        int requestedHeaders = req.Infos.Count(static (info) => info is not null);
        Assert.That(requestedHeaders, Is.GreaterThan(1), "the batch must span several headers for this to assert anything");

        // A body that matches none of the requested headers keeps the response index pinned, so it is
        // offered to every one of them.
        req.Response = new OwnedBlockBodies([new BlockBody([Build.A.Transaction.WithNonce(12345).TestObject], [])]);
        req.ResponseSourcePeer = new PeerInfo(Substitute.For<ISyncPeer>());

        feed.HandleResponse(req);

        Assert.That(
            blockValidator.ReceivedCalls().Count(static (call) => call.GetMethodInfo().Name == nameof(IBlockValidator.ValidateBodyAgainstHeader)),
            Is.EqualTo(1),
            "an unmatched body must be validated exactly once, not once per requested header");
        _syncPeerPool.Received(1).ReportBreachOfProtocol(
            Arg.Any<PeerInfo>(),
            DisconnectReason.InvalidTxOrUncle,
            Arg.Any<string>());
    }

    [Test]
    public async Task ShouldRecoverOnInsertFailure()
    {
        _feed.InitializeFeed();
        using BodiesSyncBatch req = (await _feed.PrepareRequest())!;

        req.Response = new OwnedBlockBodies(req.Infos.Take(8).Select((info) =>
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

    // The history pruner reads this record to recognise a finished descent. The barrier is 10.
    [TestCase(null, null, 10UL)] // no descent yet: records the barrier
    [TestCase(null, 7UL, 10UL)] // no descent yet: replaces an older record
    [TestCase(50UL, null, 10UL)] // a descent without a record: records the barrier
    [TestCase(50UL, 7UL, 7UL)] // a descent with a record: keeps it
    public void Records_the_barrier_the_descent_started_with(ulong? lowestInsertedBody, ulong? recorded, ulong expected)
    {
        _syncConfig.AncientBodiesBarrier = 10;
        _syncPointers.LowestInsertedBodyNumber = lowestInsertedBody;
        if (recorded is not null)
            _metadataDb.Set(MetadataDbKeys.BodiesBarrierWhenStarted, recorded.Value.ToBigEndianByteArrayWithoutLeadingZeros());

        _feed.InitializeFeed();

        Assert.That(_metadataDb.Get(MetadataDbKeys.BodiesBarrierWhenStarted)?.ToULongFromBigEndianByteArrayWithoutLeadingZeros(), Is.EqualTo(expected));
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
    public async Task Progress_reaches_exactly_the_total_when_finished([Values(1UL, 60UL, 99UL)] ulong barrier)
    {
        ProgressLogger progress = new("Old Bodies", LimboLogs.Instance);
        ISyncReport syncReport = Substitute.For<ISyncReport>();
        syncReport.FastBlocksBodies.Returns(progress);
        _syncConfig.AncientBodiesBarrier = barrier;
        _syncPointers.LowestInsertedBodyNumber = barrier;
        using BodiesSyncFeed feed = CreateFeed(CreateBlockValidator(), syncReport);
        feed.InitializeFeed();

        using BodiesSyncBatch? _ = await feed.PrepareRequest();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(feed.IsFinished, Is.True);
            Assert.That(progress.TargetValue, Is.EqualTo(_pivotBlock.Number - barrier));
            Assert.That(progress.CurrentValue, Is.EqualTo(progress.TargetValue));
        }
    }

    [Test]
    public void Completion_marker_clears_when_the_barrier_drops_after_a_finished_download()
    {
        _historyPruner.CutoffBlockNumber.Returns((ulong?)60);
        _syncPointers.LowestInsertedBodyNumber = 60;
        _feed.InitializeFeed();
        Assert.That(_metadataDb.KeyExists(MetadataDbKeys.AncientBodiesDownloadComplete), Is.True);

        _historyPruner.CutoffBlockNumber.Returns((ulong?)null);
        _feed.InitializeFeed();

        Assert.That(_metadataDb.KeyExists(MetadataDbKeys.AncientBodiesDownloadComplete), Is.False);
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
