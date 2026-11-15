// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Stats.SyncLimits;
using Nethermind.History;

namespace Nethermind.Synchronization.Test.FastBlocks;

public class ReceiptsSyncFeedTests
{
    private class Scenario
    {
        public Scenario(ISpecProvider specProvider, int nonEmptyBlocks, int txPerBlock, int emptyBlocks = 0)
        {
            int pivot = checked((int)_pivotNumber);
            Blocks = new Block[pivot + 1];
            Blocks[0] = Build.A.Block.Genesis.TestObject;

            Block parent = Blocks[0]!;
            for (int blockNumber = 1; blockNumber <= pivot; blockNumber++)
            {
                Block block = Build.A.Block
                    .WithParent(parent)
                    .WithTransactions(blockNumber > pivot - nonEmptyBlocks ? txPerBlock : 0, specProvider).TestObject;

                if (blockNumber > pivot - nonEmptyBlocks - emptyBlocks)
                    Blocks[blockNumber] = block;

                if (blockNumber == pivot - nonEmptyBlocks - emptyBlocks + 1)
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
    private IReceiptStorage _receiptStorage;
#pragma warning disable NUnit1032
    private IReceiptsInsertBatch _insertBatch;
#pragma warning restore NUnit1032
    private ISyncPointers _syncPointers;
    private ISyncPeerPool _syncPeerPool;
    private ReceiptsSyncFeed _feed;
    private ISyncConfig _syncConfig;
    private ISyncReport _syncReport;
    private IBlockTree _blockTree;
    private IBlockStore _blockStore;
    private static readonly BlockDecoder BlockDecoder = new();
    private IDb _metadataDb;
    private IHistoryPruner _historyPruner;

    private static ulong _pivotNumber = 1024;

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
        _insertBatch = Substitute.For<IReceiptsInsertBatch>();
        _receiptStorage.StartInsertBatch().Returns(_insertBatch);
        _syncPointers = new MemorySyncPointers();
        _blockTree = Substitute.For<IBlockTree>();
        _blockStore = Substitute.For<IBlockStore>();
        _historyPruner = Substitute.For<IHistoryPruner>();
        _metadataDb = new TestMemDb();

        _syncConfig = new TestSyncConfig
        {
            FastSync = true,
            PivotNumber = _pivotNumber,
            PivotHash = Keccak.Zero.ToString()
        };
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

    private ReceiptsSyncFeed CreateFeed(IReceiptsRootCalculator? receiptsRootCalculator = null) =>
        new(
            _specProvider,
            _blockTree,
            _blockStore,
            _receiptStorage,
            receiptsRootCalculator ?? ReceiptsRootCalculator.Instance,
            _syncPointers,
            _syncPeerPool,
            _syncConfig,
            _syncReport,
            _historyPruner,
            _metadataDb,
            LimboLogs.Instance);

    [Test]
    public void Should_throw_when_fast_block_not_enabled()
    {
        _syncConfig = new TestSyncConfig { FastSync = false };
        Assert.Throws<InvalidOperationException>(
            () => _feed = new ReceiptsSyncFeed(
                _specProvider,
                _blockTree,
                _blockStore,
                _receiptStorage,
                ReceiptsRootCalculator.Instance,
                _syncPointers,
                _syncPeerPool,
                _syncConfig,
                _syncReport,
                _historyPruner,
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
            _blockStore,
            NullReceiptStorage.Instance,
            ReceiptsRootCalculator.Instance,
            _syncPointers,
            _syncPeerPool,
            _syncConfig,
            _syncReport,
            _historyPruner,
            _metadataDb,
            LimboLogs.Instance);
        _feed.InitializeFeed();

        using ReceiptsSyncBatch? request = await _feed.PrepareRequest();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request, Is.Null);
            Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Finished));
        }
    }

    [Test]
    public void Contexts_are_correct() =>
        Assert.That(_feed.Contexts, Is.EqualTo(AllocationContexts.Receipts));

    [Test]
    public void Should_be_multifeed() =>
        Assert.That(_feed.IsMultiFeed, Is.True);

    [Test]
    public void Should_start_dormant() =>
        Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Dormant));

    [Test]
    public void When_activating_should_emit_an_event()
    {
        SyncFeedState state = SyncFeedState.Dormant;
        _feed.StateChanged += (_, e) => state = e.NewState;
        _feed.Activate();
        Assert.That(state, Is.EqualTo(SyncFeedState.Active));
    }

    [Test]
    public void When_no_bodies_downloaded_then_request_will_be_empty()
    {
        _feed.InitializeFeed();
        Assert.That(_feed.PrepareRequest().Result, Is.Null);
    }

    [Test]
    public async Task Returns_same_batch_until_filled()
    {
        LoadScenario(_256BodiesWithOneTxEach);
        using ReceiptsSyncBatch? request = await _feed.PrepareRequest();
        _feed.HandleResponse(request);
        using ReceiptsSyncBatch? request2 = await _feed.PrepareRequest();
        Assert.That(request2?.MinNumber, Is.EqualTo(request?.MinNumber));
    }

    [Test]
    public async Task Can_create_a_final_batch()
    {
        LoadScenario(_64BodiesWithOneTxEachFollowedByEmpty);
        using ReceiptsSyncBatch? request = await _feed.PrepareRequest();
        Assert.That(request, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request!.MinNumber, Is.EqualTo(1024));
            Assert.That(request.Prioritized, Is.EqualTo(true));
        }
    }

    [Test]
    public async Task When_configured_to_skip_receipts_then_finishes_immediately()
    {
        LoadScenario(_256BodiesWithOneTxEach);
        _syncConfig.DownloadReceiptsInFastSync = false;

        using ReceiptsSyncBatch? request = await _feed.PrepareRequest();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request, Is.Null);
            Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Finished));
            Assert.That(_progressLogger.HasEnded, Is.True);
        }
    }

    [TestCase(1UL, 1024UL, false, null, false)]
    [TestCase(1UL, 1024UL, true, null, false)]
    [TestCase(1UL, 1024UL, false, 0UL, false)]
    public void When_finished_sync_with_old_default_barrier_then_finishes_immediately(
        ulong AncientBarrierInConfig,
        ulong? lowestInsertedReceiptBlockNumber,
        bool JustStarted,
        ulong? previousBarrierInDb,
        bool shouldFinish)
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
        Assert.That(_feed.IsFinished, Is.EqualTo(shouldFinish));
    }

    [Test]
    public async Task When_AncientReceiptsBarrier_exceeds_SyncPivot_then_finishes_immediately()
    {
        // Reproduces the Hoodi case: config PivotNumber unset (0), barrier above chain length,
        // real pivot supplied via _blockTree.SyncPivot.
        Scenario scenario = _256BodiesWithOneTxEach;
        _syncConfig = new TestSyncConfig
        {
            FastSync = true,
            PivotNumber = 0,
            AncientBodiesBarrier = 4_367_322,
            AncientReceiptsBarrier = 4_367_322,
            DownloadReceiptsInFastSync = true,
        };
        _blockTree.SyncPivot.Returns((_pivotNumber, scenario.Blocks.Last()!.Hash!));
        _blockTree.FindCanonicalBlockInfo(Arg.Any<ulong>()).Returns(
            ci =>
            {
                Block? block = scenario.Blocks[(int)ci.Arg<ulong>()];
                return block is null
                    ? null
                    : new BlockInfo(block.Hash!, block.TotalDifficulty ?? 0) { BlockNumber = ci.Arg<ulong>() };
            });
        _receiptStorage.HasBlock(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(false);
        _syncPointers = new MemorySyncPointers();

        _feed = new ReceiptsSyncFeed(
            _specProvider,
            _blockTree,
            _blockStore,
            _receiptStorage,
            ReceiptsRootCalculator.Instance,
            _syncPointers,
            _syncPeerPool,
            _syncConfig,
            _syncReport,
            _historyPruner,
            _metadataDb,
            LimboLogs.Instance);

        _feed.InitializeFeed();
        using ReceiptsSyncBatch? _ = await _feed.PrepareRequest();

        Assert.That(_feed.IsFinished, Is.True);
    }

    // Regression for #9002: decreasing AncientReceiptsBarrier after a partial sync must not leave the feed stuck.
    [Test]
    public void When_AncientReceiptsBarrier_decreased_after_partial_sync_feed_is_not_finished()
    {
        // Previous run reached block 768; restart with a lower barrier of 256.
        _syncConfig.AncientBodiesBarrier = 256;
        _syncConfig.AncientReceiptsBarrier = 256;
        _receiptStorage.HasBlock(Arg.Is(_pivotNumber), Arg.Any<Hash256>()).Returns(true);
        _syncPointers.LowestInsertedReceiptBlockNumber = 768;

        _feed = CreateFeed();
        _feed.InitializeFeed();

        // Receipts 256..767 are still missing — feed must stay active.
        Assert.That(_feed.IsFinished, Is.False);
    }

    private void LoadScenario(Scenario scenario) =>
        LoadScenario(scenario, _syncConfig);

    private void LoadScenario(Scenario scenario, ISyncConfig syncConfig, ISpecProvider? specProvider = null)
    {
        _syncConfig = syncConfig;
        _syncConfig.PivotNumber = _pivotNumber;
        _syncConfig.PivotHash = scenario.Blocks.Last()?.Hash?.ToString();
        _blockTree.SyncPivot.Returns((_pivotNumber, scenario.Blocks.Last()?.Hash!));
        _syncPointers = Substitute.For<ISyncPointers>();

        _feed = new ReceiptsSyncFeed(
            specProvider ?? _specProvider,
            _blockTree,
            _blockStore,
            _receiptStorage,
            ReceiptsRootCalculator.Instance,
            _syncPointers,
            _syncPeerPool,
            _syncConfig,
            _syncReport,
            _historyPruner,
            _metadataDb,
            LimboLogs.Instance);
        _feed.InitializeFeed();

        _blockTree.Genesis.Returns(scenario.Blocks[0]!.Header);
        _blockTree.FindCanonicalBlockInfo(Arg.Any<ulong>()).Returns(
            ci =>
            {
                Block? block = scenario.Blocks[(int)ci.Arg<ulong>()];
                if (block is null)
                    return null;

                BlockInfo blockInfo = new(block.Hash!, block.TotalDifficulty ?? 0)
                {
                    BlockNumber = ci.Arg<ulong>(),
                };
                return blockInfo;
            });

        _blockTree.FindBlock(Keccak.Zero, BlockTreeLookupOptions.None)
            .ReturnsForAnyArgs(ci =>
                scenario.BlocksByHash.TryGetValue(ci.Arg<Hash256>(), out Block? value) ? value : null);

        _blockStore.GetOwnedRlp(default, Keccak.Zero)
            .ReturnsForAnyArgs(ci =>
                scenario.BlocksByHash.TryGetValue(ci.Arg<Hash256>(), out Block? value) ? new ArrayMemoryManager(BlockDecoder.Encode(value)) : null);

        _blockTree.FindHeader(Keccak.Zero, BlockTreeLookupOptions.None)
            .ReturnsForAnyArgs(ci =>
                scenario.BlocksByHash.TryGetValue(ci.Arg<Hash256>(), out Block? value) ? value.Header
                    : null);

        _syncPointers.LowestInsertedReceiptBlockNumber.Returns((ulong?)null);
    }

    [Test]
    public async Task Progress_reaches_exactly_the_total_when_finished([Values(1UL, 512UL)] ulong barrier)
    {
        _syncConfig.AncientBodiesBarrier = barrier;
        _syncConfig.AncientReceiptsBarrier = barrier;
        LoadScenario(_1024BodiesWithOneTxEach);
        _syncPointers.LowestInsertedReceiptBlockNumber.Returns(barrier);

        using ReceiptsSyncBatch? _ = await _feed.PrepareRequest();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Finished));
            Assert.That(_progressLogger.TargetValue, Is.EqualTo(_pivotNumber - barrier));
            Assert.That(_progressLogger.CurrentValue, Is.EqualTo(_progressLogger.TargetValue));
        }
    }

    [Test]
    public async Task Can_create_receipts_batches_for_all_bodies_inserted_and_then_generate_null_batches_for_other_peers()
    {
        LoadScenario(_256BodiesWithOneTxEach);

        /* we have only 256 receipts altogether but we start with many peers
           so most of our requests will be empty */

        List<ReceiptsSyncBatch?> batches = [];
        for (int i = 0; i < 100; i++)
        {
            batches.Add(await _feed.PrepareRequest());
        }

        // Expected batches based on actual MaxReceiptFetch
        int expectedBatches = (int)Math.Ceiling(256.0 / GethSyncLimits.MaxReceiptFetch);

        for (int i = 0; i < expectedBatches; i++)
        {
            Assert.That(batches[i], Is.Not.Null);
            Assert.That(batches[i]!.ToString(), Is.Not.Null);
        }

        for (int i = expectedBatches; i < 100; i++)
        {
            Assert.That(batches[i], Is.Null);
        }
    }

    [Test]
    public async Task If_receipts_root_comes_invalid_then_reports_breach_of_protocol()
    {
        LoadScenario(_1024BodiesWithOneTxEach);
        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        ArrayPoolList<TxReceipt[]?> response = new(batch!.Infos.Length, batch!.Infos.Length)
        {
            // default receipts that we use when constructing receipt root for tests have stats code 0
            // so by using 1 here we create a different tx root
            [0] = new[] { Build.A.Receipt.WithStatusCode(1).TestObject }
        };

        batch!.Response = response!;

        PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
        batch.ResponseSourcePeer = peerInfo;

        SyncResponseHandlingResult handlingResult = _feed.HandleResponse(batch);
        Assert.That(handlingResult, Is.EqualTo(SyncResponseHandlingResult.NoProgress));

        _syncPeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.InvalidReceiptRoot, Arg.Any<string>());
    }

    [Test]
    public async Task Validates_receipts_root_with_timestamp_activated_eip7668()
    {
        // A number-only lookup misses a timestamp fork, so it would expect 256-byte blooms in the root.
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(static ci => ci.Arg<ForkActivation>().Timestamp is null
            ? Bogota.Instance
            : new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = true });
        LoadScenario(new Scenario(specProvider, 1024, 1), _syncConfig, specProvider);

        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        FillBatchResponses(batch!);
        PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
        batch!.ResponseSourcePeer = peerInfo;
        int requested = batch.Infos.Length;

        SyncResponseHandlingResult handlingResult = _feed.HandleResponse(batch);

        Assert.That(handlingResult, Is.EqualTo(SyncResponseHandlingResult.OK));
        _syncPeerPool.DidNotReceiveWithAnyArgs().ReportBreachOfProtocol(default!, default, default!);
        _insertBatch.Received(requested).Insert(Arg.Any<Block>(), Arg.Any<TxReceipt[]>());
    }

    private static void FillBatchResponses(ReceiptsSyncBatch batch)
    {
        ArrayPoolList<TxReceipt[]?> response = new(batch.Infos.Length, batch.Infos.Length);
        for (int i = 0; i < response.Count; i++)
        {
            response[i] = new[] { Build.A.Receipt.TestObject };
        }

        batch.Response = response;
    }

    /// <summary>Gives each block of the batch its own receipts, which its header's receipts root matches.</summary>
    private void FillBatchResponsesThatDifferByBlock(ReceiptsSyncBatch batch, Scenario scenario)
    {
        _blockTree.FindHeader(Keccak.Zero, BlockTreeLookupOptions.None).ReturnsForAnyArgs(ci =>
        {
            BlockHeader header = scenario.BlocksByHash[ci.Arg<Hash256>()].Header.Clone();
            header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(ReceiptsOf(header.Number), _specProvider.GenesisSpec, null);
            return header;
        });

        ArrayPoolList<TxReceipt[]?> response = new(batch.Infos.Length, batch.Infos.Length);
        for (int i = 0; i < response.Count; i++)
        {
            response[i] = ReceiptsOf(batch.Infos[i]!.BlockNumber);
        }

        batch.Response = response;
    }

    private static TxReceipt[] ReceiptsOf(ulong blockNumber) => [Build.A.Receipt.WithGasUsedTotal(blockNumber).TestObject];

    [Test]
    public async Task Stores_receipts_kept_encoded_as_received([Values] bool storeTakesEncoded)
    {
        LoadScenario(_1024BodiesWithOneTxEach);
        List<(ulong BlockNumber, ulong GasUsedTotal)> stored = [];
        _insertBatch.TryInsertNetworkReceipts(default!, default, default).ReturnsForAnyArgs(ci =>
        {
            if (storeTakesEncoded)
            {
                RlpReader receipts = new RlpReader(ci.Arg<ReadOnlyMemory<byte>>().Span).EnterSequence(out _);
                NetworkReceipt.TryRead(ref receipts, withBloom: true, out NetworkReceipt receipt);
                stored.Add((ci.Arg<Block>().Number, receipt.GasUsedTotal));
            }

            return storeTakesEncoded;
        });
        _insertBatch.When(static b => b.Insert(Arg.Any<Block>(), Arg.Any<TxReceipt[]>()))
            .Do(ci => stored.Add((ci.Arg<Block>().Number, ci.Arg<TxReceipt[]>()[0].GasUsedTotal)));

        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        FillBatchResponsesThatDifferByBlock(batch!, _1024BodiesWithOneTxEach);
        using (IOwnedReadOnlyList<TxReceipt[]?> decoded = batch!.Response!)
        {
            batch.Response = Encode(decoded)!;
        }

        int requested = batch.Infos.Length;
        // A store that does not take encoded receipts is asked once only.
        int encodedInserts = storeTakesEncoded ? requested : 1;

        SyncResponseHandlingResult handlingResult = _feed.HandleResponse(batch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handlingResult, Is.EqualTo(SyncResponseHandlingResult.OK));
            Assert.That(stored, Is.EqualTo(batch.Infos.Select(static info => (info!.BlockNumber, info.BlockNumber))));
        }

        _insertBatch.ReceivedWithAnyArgs(encodedInserts).TryInsertNetworkReceipts(default!, default, default);
        _blockStore.ReceivedWithAnyArgs(encodedInserts).GetOwnedRlp(default, default!);
    }

    [Test]
    public async Task Decodes_receipts_whose_root_the_calculator_does_not_compute_encoded()
    {
        LoadScenario(_1024BodiesWithOneTxEach);
        // A calculator for receipts in another form, as a plugin registers it.
        IReceiptsRootCalculator calculator = Substitute.For<IReceiptsRootCalculator>();
        calculator.GetReceiptsRoot(Arg.Any<TxReceipt[]>(), Arg.Any<IReceiptSpec>(), Arg.Any<Hash256?>())
            .Returns(ci => ReceiptsRootCalculator.Instance.GetReceiptsRoot(ci.ArgAt<TxReceipt[]>(0), ci.ArgAt<IReceiptSpec>(1), ci.ArgAt<Hash256?>(2)));
        _feed.Dispose();
        _feed = CreateFeed(calculator);
        _feed.InitializeFeed();

        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        FillBatchResponses(batch!);
        using (IOwnedReadOnlyList<TxReceipt[]?> decoded = batch!.Response!)
        {
            batch.Response = Encode(decoded)!;
        }

        SyncResponseHandlingResult handlingResult = _feed.HandleResponse(batch);

        Assert.That(handlingResult, Is.EqualTo(SyncResponseHandlingResult.OK));
        _insertBatch.DidNotReceiveWithAnyArgs().TryInsertNetworkReceipts(default!, default, default);
        _insertBatch.Received(batch.Infos.Length).Insert(Arg.Any<Block>(), Arg.Any<TxReceipt[]>());
    }

    [Test]
    public async Task Stores_decoded_receipts_with_the_bloom_of_their_logs_when_the_store_takes_no_encoded_ones()
    {
        LoadScenario(_1024BodiesWithOneTxEach);
        static TxReceipt WithLogs() => Build.A.Receipt.WithStatusCode(StatusCode.Success).WithLogs(Build.A.LogEntry.TestObject).TestObject;
        TxReceipt withBloom = WithLogs();
        withBloom.Bloom = new Bloom(withBloom.Logs);
        Hash256 receiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot([withBloom], _specProvider.GenesisSpec, null);
        _blockTree.FindHeader(Keccak.Zero, BlockTreeLookupOptions.None).ReturnsForAnyArgs(ci =>
        {
            BlockHeader header = _1024BodiesWithOneTxEach.BlocksByHash[ci.Arg<Hash256>()].Header.Clone();
            header.ReceiptsRoot = receiptsRoot;
            return header;
        });

        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        using (ArrayPoolList<TxReceipt[]?> zeroBlooms = new(batch!.Infos.Length, batch.Infos.Length))
        {
            // The peer sends the receipts with an all-zero bloom.
            for (int i = 0; i < zeroBlooms.Count; i++) zeroBlooms[i] = [Build.A.Receipt.WithStatusCode(StatusCode.Success).WithLogs(Build.A.LogEntry.TestObject).WithBloom(Bloom.Empty).TestObject];
            batch.Response = Encode(zeroBlooms)!;
        }

        SyncResponseHandlingResult handlingResult = _feed.HandleResponse(batch);

        Assert.That(handlingResult, Is.EqualTo(SyncResponseHandlingResult.OK));
        _insertBatch.Received(batch.Infos.Length).Insert(Arg.Any<Block>(), Arg.Is<TxReceipt[]>(r => r[0].Bloom == withBloom.Bloom));
    }

    [Test]
    public async Task Reports_a_breach_when_receipts_kept_encoded_do_not_decode()
    {
        LoadScenario(_1024BodiesWithOneTxEach);

        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        FillBatchResponses(batch!);
        using (IOwnedReadOnlyList<TxReceipt[]?> decoded = batch!.Response!)
        {
            batch.Response = Encode(decoded, static (ref RlpReader _) => throw new RlpReaderException("malformed"))!;
        }

        // A root the encoded receipts do not give, so the feed decodes them.
        _blockTree.FindHeader(Keccak.Zero, BlockTreeLookupOptions.None).ReturnsForAnyArgs(ci =>
        {
            BlockHeader header = _1024BodiesWithOneTxEach.BlocksByHash[ci.Arg<Hash256>()].Header.Clone();
            header.ReceiptsRoot = TestItem.KeccakA;
            return header;
        });
        batch.ResponseSourcePeer = new PeerInfo(Substitute.For<ISyncPeer>());

        _feed.HandleResponse(batch);

        _syncPeerPool.Received().ReportBreachOfProtocol(Arg.Any<PeerInfo>(), DisconnectReason.InvalidReceiptRoot, Arg.Any<string>());
    }

    private static NetworkBlockReceipts Encode(IOwnedReadOnlyList<TxReceipt[]?> blocks, RefRlpReaderFunc<TxReceipt[]>? decodeBlock = null)
    {
        ReceiptMessageDecoder decoder = new();
        ArrayPoolList<byte> buffer = [with(blocks.Count * 256)];
        Range[] ranges = new Range[blocks.Count];
        for (int i = 0; i < ranges.Length; i++)
        {
            int start = buffer.Count;
            buffer.AddRange(RlpTestHelper.EncodeSequence([.. blocks[i]!.Select(receipt => decoder.Encode(receipt, RlpBehaviors.Eip658Receipts))]));
            ranges[i] = new Range(start, buffer.Count);
        }

        return NetworkBlockReceipts.FromBuffer(buffer, ranges, withBloom: true,
            decodeBlock ?? ((ref RlpReader reader) => reader.DecodeNonNullArray((ref RlpReader item) => decoder.Decode(ref item))!));
    }

    [Test]
    public async Task Inserts_receipts_when_the_block_number_index_has_no_entry()
    {
        LoadScenario(_1024BodiesWithOneTxEach);

        // Given no block number, the block tree resolves one from the block number index first, so a body whose
        // index entry is missing is only reachable when the number the feed already knows is passed along.
        _blockTree.FindBlock(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
            .Returns(ci => ci.ArgAt<ulong?>(2) is null
                ? null
                : _1024BodiesWithOneTxEach.BlocksByHash.GetValueOrDefault(ci.ArgAt<Hash256>(0)));

        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        FillBatchResponses(batch!);
        int requested = batch!.Infos.Length;

        SyncResponseHandlingResult handlingResult = _feed.HandleResponse(batch);

        Assert.That(handlingResult, Is.EqualTo(SyncResponseHandlingResult.OK));
        _insertBatch.Received(requested).Insert(Arg.Any<Block>(), Arg.Any<TxReceipt[]>());
    }

    [Test]
    public async Task Moves_the_receipts_pointer_only_after_the_insert_batch_is_stored()
    {
        LoadScenario(_1024BodiesWithOneTxEach);
        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        FillBatchResponses(batch!);
        ulong? pointerBefore = _syncPointers.LowestInsertedReceiptBlockNumber;
        ulong? pointerDuringWrite = null;
        _insertBatch.When(static b => b.Dispose()).Do(_ =>
        {
            using ReceiptsSyncBatch? concurrent = _feed.PrepareRequest().GetAwaiter().GetResult();
            pointerDuringWrite = _syncPointers.LowestInsertedReceiptBlockNumber;
        });

        SyncResponseHandlingResult handlingResult = _feed.HandleResponse(batch!);
        using ReceiptsSyncBatch? next = await _feed.PrepareRequest();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handlingResult, Is.EqualTo(SyncResponseHandlingResult.OK));
            Assert.That(pointerDuringWrite, Is.EqualTo(pointerBefore));
            Assert.That(_syncPointers.LowestInsertedReceiptBlockNumber, Is.LessThan(pointerBefore!.Value));
            _insertBatch.Received(1).Dispose();
        }
    }

    [Test]
    public async Task If_header_is_missing_locally_then_does_not_report_breach_of_protocol_or_drop_the_rest_of_the_batch()
    {
        LoadScenario(_1024BodiesWithOneTxEach);

        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();
        FillBatchResponses(batch!);
        Hash256 missingHeaderHash = batch!.Infos[0]!.BlockHash;
        _blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
            .Returns(ci => ci.ArgAt<Hash256>(0) == missingHeaderHash
                ? null
                : _1024BodiesWithOneTxEach.BlocksByHash.GetValueOrDefault(ci.ArgAt<Hash256>(0))?.Header);

        PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
        batch.ResponseSourcePeer = peerInfo;

        SyncResponseHandlingResult handlingResult = _feed.HandleResponse(batch);

        Assert.That(handlingResult, Is.EqualTo(SyncResponseHandlingResult.OK));
        _syncPeerPool.DidNotReceiveWithAnyArgs().ReportBreachOfProtocol(default!, default, default!);
        _insertBatch.Received(batch.Infos.Length - 1).Insert(Arg.Any<Block>(), Arg.Any<TxReceipt[]>());
    }

    [Test]
    public async Task Can_sync_final_batch()
    {
        LoadScenario(_64BodiesWithOneTxEach);
        using ReceiptsSyncBatch? batch = await _feed.PrepareRequest();

        FillBatchResponses(batch!);
        _feed.HandleResponse(batch);
        _syncPointers.LowestInsertedReceiptBlockNumber.Returns(1UL);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_feed.PrepareRequest().Result, Is.EqualTo(null));

            Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Finished));
        }
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
            PivotNumber = 1,
        };

        _blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);

        _syncPointers = Substitute.For<ISyncPointers>();
        _syncPointers.LowestInsertedReceiptBlockNumber.Returns(2UL);

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
            PivotNumber = 1,
        };

        _blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
        _syncPointers = Substitute.For<ISyncPointers>();
        _syncPointers.LowestInsertedReceiptBlockNumber.Returns(1UL);

        ReceiptsSyncFeed feed = CreateFeed();
        feed.InitializeFeed();
        Assert.That(feed.IsFinished, Is.True);
    }

    [TestCase(true, true, true, TestName = "NormalizeZeroBlooms recomputes when logs present and bloom empty")]
    [TestCase(false, true, false, TestName = "NormalizeZeroBlooms leaves legitimately empty receipts alone")]
    [TestCase(true, false, false, TestName = "NormalizeZeroBlooms does not overwrite a non-empty bloom")]
    public void NormalizeZeroBlooms(bool hasLogs, bool startBloomEmpty, bool expectRecompute)
    {
        LogEntry[] logs = hasLogs
            ? [Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject]
            : [];
        TxReceipt receipt = Build.A.Receipt.WithLogs(logs).TestObject;
        Bloom initialBloom = startBloomEmpty
            ? Bloom.Empty
            : new Bloom([Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject]);
        receipt.Bloom = initialBloom;
        Bloom expectedBloom = expectRecompute ? new Bloom(receipt.Logs) : initialBloom;

        ReceiptsSyncFeed.NormalizeZeroBlooms([receipt]);

        Assert.That(receipt.Bloom, Is.EqualTo(expectedBloom));
    }
}
