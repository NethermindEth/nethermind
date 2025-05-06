// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Synchronization.Test.FastBlocks;

public class ReceiptsSyncFeedTests
{
    private class Scenario
    {
        public Scenario(ISpecProvider specProvider, int nonEmptyBlocks, int txPerBlock, int emptyBlocks = 0)
        {
            Blocks = new Block[_pivotNumber + 1];
            Blocks[0] = Build.A.Block.Genesis.TestObject;

            Block parent = Blocks[0]!;
            for (int blockNumber = 1; blockNumber <= _pivotNumber; blockNumber++)
            {
                Block block = Build.A.Block
                    .WithParent(parent)
                    .WithTransactions(blockNumber > _pivotNumber - nonEmptyBlocks ? txPerBlock : 0, specProvider).TestObject;

                if (blockNumber > _pivotNumber - nonEmptyBlocks - emptyBlocks)
                    Blocks[blockNumber] = block;

                if (blockNumber == _pivotNumber - nonEmptyBlocks - emptyBlocks + 1)
                    LowestInsertedBody = block;

                parent = block;
            }

            BlocksByHash = Blocks
                .Where(static b => b is not null)
                .ToDictionary(static b => b!.Hash!, static b => b!);
        }

        public Dictionary<Hash256, Block> BlocksByHash { get; }
        public Block?[] Blocks { get; }
        public Block? LowestInsertedBody { get; }
    }

    private static readonly ISpecProvider _specProvider;
    private IReceiptStorage _receiptStorage = null!;
    private ISyncPointers _syncPointers = null!;
    private ISyncPeerPool _syncPeerPool = null!;
    private ReceiptsSyncFeed _feed = null!;
    private ISyncConfig _syncConfig = null!;
    private ISyncReport _syncReport = null!;
    private IBlockTree _blockTree = null!;
    private IDb _metadataDb = null!;

    private static long _pivotNumber = 1024;

    private static readonly Scenario _1024BodiesWithOneTxEach;
    private static readonly Scenario _256BodiesWithOneTxEach;
    private static readonly Scenario _64BodiesWithOneTxEach;
    private static readonly Scenario _64BodiesWithOneTxEachFollowedByEmpty;

    private ProgressLogger _progressLogger = null!;

    static ReceiptsSyncFeedTests()
    {
        _specProvider = new TestSingleReleaseSpecProvider(Istanbul.Instance);
        _1024BodiesWithOneTxEach = new Scenario(_specProvider, 1024, 1);
        _256BodiesWithOneTxEach = new Scenario(_specProvider, 256, 1);
        _64BodiesWithOneTxEach = new Scenario(_specProvider, 64, 1);
        _64BodiesWithOneTxEachFollowedByEmpty = new Scenario(_specProvider, 64, 1, 1024 - 64);
    }

    [SetUp]
    public void Setup()
    {
        _receiptStorage = Substitute.For<IReceiptStorage>();
        _syncPointers = new MemorySyncPointers();
        _blockTree = Substitute.For<IBlockTree>();
        _metadataDb = new TestMemDb();

        _syncConfig = new TestSyncConfig { FastSync = true };
        _syncConfig.PivotNumber = _pivotNumber.ToString();
        _syncConfig.PivotHash = Keccak.Zero.ToString();
        _blockTree.SyncPivot.Returns((_pivotNumber, Keccak.Zero));

        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _syncReport = Substitute.For<ISyncReport>();

        _progressLogger = new ProgressLogger("Receipts", LimboLogs.Instance);
        _syncReport.FastBlocksReceipts.Returns(_progressLogger);

        _feed = CreateFeed();
    }

    [TearDown]
    public void TearDown()
    {
        _feed?.Dispose();
        _syncPeerPool?.DisposeAsync();
        _syncReport?.Dispose();
        _metadataDb?.Dispose();
    }

    private ReceiptsSyncFeed CreateFeed()
    {
        return new ReceiptsSyncFeed(
            _specProvider,
            _blockTree,
            _receiptStorage,
            _syncPointers,
            _syncPeerPool,
            _syncConfig,
            _syncReport,
            _metadataDb,
            LimboLogs.Instance);
    }

    [Test]
    public void Should_throw_when_fast_block_not_enabled()
    {
        _syncConfig = new TestSyncConfig { FastSync = false };
        Assert.Throws<InvalidOperationException>(
            () => _feed = new ReceiptsSyncFeed(
                _specProvider,
                _blockTree,
                _receiptStorage,
                _syncPointers,
                _syncPeerPool,
                _syncConfig,
                _syncReport,
                _metadataDb,
                LimboLogs.Instance));
    }

    [Test]
    public async Task Should_finish_on_start_when_receipts_not_stored()
    {
        _syncPointers.LowestInsertedReceiptBlockNumber = 0;
        _feed = new ReceiptsSyncFeed(
            _specProvider,
            _blockTree,
            NullReceiptStorage.Instance,
            _syncPointers,
            _syncPeerPool,
            _syncConfig,
            _syncReport,
            _metadataDb,
            LimboLogs.Instance);
        _feed.InitializeFeed();

        using ReceiptsSyncBatch? request = await _feed.PrepareRequest();
        request.Should().BeNull();
        _feed.CurrentState.Should().Be(SyncFeedState.Finished);
    }

    [Test]
    public void Contexts_are_correct()
    {
        _feed.Contexts.Should().Be(AllocationContexts.Receipts);
    }

    [Test]
    public void Should_be_multifeed()
    {
        _feed.IsMultiFeed.Should().BeTrue();
    }

    [Test]
    public void Should_start_dormant()
    {
        _feed.CurrentState.Should().Be(SyncFeedState.Dormant);
    }

    [Test]
    public void When_activating_should_emit_an_event()
    {
        SyncFeedState state = SyncFeedState.Dormant;
        _feed.StateChanged += (_, e) => state = e.NewState;
        _feed.Activate();
        state.Should().Be(SyncFeedState.Active);
    }

    [Test]
    public void When_no_bodies_downloaded_then_request_will_be_empty()
    {
        _feed.InitializeFeed();
        _feed.PrepareRequest().Result.Should().BeNull();
    }

    [Test]
    public async Task Returns_same_batch_until_filled()
    {
        LoadScenario(_256BodiesWithOneTxEach);
        using ReceiptsSyncBatch? request = await _feed.PrepareRequest();
        _feed.HandleResponse(request);
        using ReceiptsSyncBatch? request2 = await _feed.PrepareRequest();
        request2?.MinNumber.Should().Be(request?.MinNumber);
    }

    [Test]
    public async Task Can_create_a_final_batch()
    {
        LoadScenario(_64BodiesWithOneTxEachFollowedByEmpty);
        using ReceiptsSyncBatch? request = await _feed.PrepareRequest();
        request.Should().NotBeNull();
        request!.MinNumber.Should().Be(1024);
        request.Prioritized.Should().Be(true);
    }

    [Test]
    public async Task When_configured_to_skip_receipts_then_finishes_immediately()
    {
        LoadScenario(_256BodiesWithOneTxEach);
        _syncConfig.DownloadReceiptsInFastSync = false;

        using ReceiptsSyncBatch? request = await _feed.PrepareRequest();
        request.Should().BeNull();
        _feed.CurrentState.Should().Be(SyncFeedState.Finished);
        _progressLogger.HasEnded.Should().BeTrue();
    }

    [TestCase(1, 1024, false, null, false)]
    [TestCase(1, 11051474, false, null, true)]
    [TestCase(1, 11052984, false, null, true)]
    [TestCase(11051474, 11052984, false, null, false)]
    [TestCase(11051474, 11051474, false, null, true)]
    [TestCase(1, 11052985, false, null, false)]
    [TestCase(1, 1024, false, 11052984, false)]
    [TestCase(1, 11051474, false, 11052984, true)]
    [TestCase(1, 11052984, false, 11052984, true)]
    [TestCase(11051474, 11052984, false, 11052984, false)]
    [TestCase(11051474, 11051474, false, 11052984, true)]
    [TestCase(1, 11052985, false, 11052984, false)]
    [TestCase(1, 1024, true, null, false)]
    [TestCase(1, 11051474, true, null, false)]
    [TestCase(1, 11052984, true, null, false)]
    [TestCase(11051474, 11052984, true, null, false)]
    [TestCase(11051474, 11051474, true, null, true)]
    [TestCase(1, 11052985, true, null, false)]
    [TestCase(1, 1024, false, 0, false)]
    [TestCase(1, 11051474, false, 0, false)]
    [TestCase(1, 11052984, false, 0, false)]
    [TestCase(11051474, 11052984, false, 0, false)]
    [TestCase(11051474, 11051474, false, 0, true)]
    [TestCase(1, 11052985, false, 0, false)]
    public void When_finished_sync_with_old_default_barrier_then_finishes_imedietely(
        long AncientBarrierInConfig,
        long? lowestInsertedReceiptBlockNumber,
        bool JustStarted,
        long? previousBarrierInDb,
        bool shouldfinish)
    {
        _syncPointers = Substitute.For<ISyncPointers>();
        _syncConfig.AncientBodiesBarrier = AncientBarrierInConfig;
        _syncConfig.AncientReceiptsBarrier = AncientBarrierInConfig;
        _pivotNumber = AncientBarrierInConfig + 1_000_000;
        _receiptStorage.HasBlock(Arg.Is(_pivotNumber), Arg.Any<Hash256>()).Returns(!JustStarted);
        if (previousBarrierInDb is not null)
            _metadataDb.Set(MetadataDbKeys.ReceiptsBarrierWhenStarted, previousBarrierInDb.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        LoadScenario(_256BodiesWithOneTxEach);
        _syncPointers.LowestInsertedReceiptBlockNumber.Returns(lowestInsertedReceiptBlockNumber);
        _feed.IsFinished.Should().Be(shouldfinish);
    }

    private void LoadScenario(Scenario scenario)
    {
        LoadScenario(scenario, _syncConfig);
    }

    private void LoadScenario(Scenario scenario, ISyncConfig syncConfig)
    {
        _syncConfig = syncConfig;
        _syncConfig.PivotNumber = _pivotNumber.ToString();
        _syncConfig.PivotHash = scenario.Blocks.Last()?.Hash?.ToString();
        _blockTree.SyncPivot.Returns((_pivotNumber, scenario.Blocks.Last()?.Hash!));
        _syncPointers = Substitute.For<ISyncPointers>();

        _feed = new ReceiptsSyncFeed(
            _specProvider,
            _blockTree,
            _receiptStorage,
            _syncPointers,
            _syncPeerPool,
            _syncConfig,
            _syncReport,
            _metadataDb,
            LimboLogs.Instance);
        _feed.InitializeFeed();

        _blockTree.Genesis.Returns(scenario.Blocks[0]!.Header);
        _blockTree.FindCanonicalBlockInfo(Arg.Any<long>()).Returns(
            ci =>
            {
                Block? block = scenario.Blocks[ci.Arg<long>()];
                if (block is null)
                    return null;

                BlockInfo blockInfo = new(block.Hash!, block.TotalDifficulty ?? 0)
                {
                    BlockNumber = ci.Arg<long>(),
                };
                return blockInfo;
            });

        _blockTree.FindBlock(Keccak.Zero, BlockTreeLookupOptions.None)
            .ReturnsForAnyArgs(ci =>
                scenario.BlocksByHash.TryGetValue(ci.Arg<Hash256>(), out Block? value) ? value : null);

        _blockTree.FindHeader(Keccak.Zero, BlockTreeLookupOptions.None)
            .ReturnsForAnyArgs(ci =>
                scenario.BlocksByHash.TryGetValue(ci.Arg<Hash256>(), out Block? value) ? value.Header
                    : null);

        _syncPointers.LowestInsertedReceiptBlockNumber.Returns((long?)null);
    }

    [Test]
    public async Task Can_create_receipts_batches_for_all_bodies_inserted_and_then_generate_null_batches_for_other_peers()
    {
        LoadScenario(_256BodiesWithOneTxEach);

        /* we have only 256 receipts altogether but we start with many peers
           so most of our requests will be empty */

        List<ReceiptsSyncBatch?> batches = new();
        for (int i = 0; i < 100; i++)
        {
            batches.Add(await _feed.PrepareRequest());
        }

        // Expected batches based on actual MaxReceiptFetch
        int expectedBatches = (int)Math.Ceiling(256.0 / GethSyncLimits.MaxReceiptFetch);

        for (int i = 0; i < expectedBatches; i++)
        {
            batches[i].Should().NotBeNull();
            batches[i]!.ToString().Should().NotBeNull();
        }

        for (int i = expectedBatches; i < 100; i++)
        {
            batches[i].Should().BeNull();
        }
    }

    [Test]
    public async Task If_receipts_root_comes_invalid_then_reports_breach_of_protocol()
    {
        LoadScenario(_1024BodiesWithOneTxEach);
        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        var response = new ArrayPoolList<TxReceipt[]?>(batch!.Infos.Length, batch!.Infos.Length);

        // default receipts that we use when constructing receipt root for tests have stats code 0
        // so by using 1 here we create a different tx root
        response[0] = new[] { Build.A.Receipt.WithStatusCode(1).TestObject };

        batch!.Response = response!;

        PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
        batch.ResponseSourcePeer = peerInfo;

        SyncResponseHandlingResult handlingResult = _feed.HandleResponse(batch);
        handlingResult.Should().Be(SyncResponseHandlingResult.NoProgress);

        _syncPeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.InvalidReceiptRoot, Arg.Any<string>());
    }

    private static void FillBatchResponses(ReceiptsSyncBatch batch)
    {
        var response = new ArrayPoolList<TxReceipt[]?>(batch.Infos.Length, batch.Infos.Length);
        for (int i = 0; i < response.Count; i++)
        {
            response[i] = new[] { Build.A.Receipt.TestObject };
        }

        batch.Response = response;
    }

    [Test]
    public async Task Can_sync_final_batch()
    {
        LoadScenario(_64BodiesWithOneTxEach);
        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();

        FillBatchResponses(batch!);
        _feed.HandleResponse(batch);
        _syncPointers.LowestInsertedReceiptBlockNumber.Returns(1);
        _feed.PrepareRequest().Result.Should().Be(null);

        _feed.CurrentState.Should().Be(SyncFeedState.Finished);
    }

    [Test]
    public void Is_fast_block_receipts_finished_returns_false_when_receipts_not_downloaded()
    {
        _blockTree = Substitute.For<IBlockTree>();
        _syncConfig = new TestSyncConfig()
        {
            FastSync = true,
            DownloadBodiesInFastSync = true,
            DownloadReceiptsInFastSync = true,
            PivotNumber = "1",
        };

        _blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);

        _syncPointers = Substitute.For<ISyncPointers>();
        _syncPointers.LowestInsertedReceiptBlockNumber.Returns(2);

        ReceiptsSyncFeed feed = CreateFeed();
        Assert.That(feed.IsFinished, Is.False);
    }

    [Test]
    public void Is_fast_block_bodies_finished_returns_true_when_bodies_not_downloaded_and_we_do_not_want_to_download_bodies()
    {
        _blockTree = Substitute.For<IBlockTree>();
        _syncConfig = new TestSyncConfig()
        {
            FastSync = true,
            DownloadBodiesInFastSync = false,
            DownloadReceiptsInFastSync = true,
            PivotNumber = "1",
        };

        _blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
        _syncPointers = Substitute.For<ISyncPointers>();
        _syncPointers.LowestInsertedReceiptBlockNumber.Returns(1);

        ReceiptsSyncFeed feed = CreateFeed();
        feed.InitializeFeed();
        Assert.That(feed.IsFinished, Is.True);
    }

}
