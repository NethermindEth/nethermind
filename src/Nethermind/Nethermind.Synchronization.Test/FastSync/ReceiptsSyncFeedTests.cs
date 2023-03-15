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
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Test.Synchronization;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    public class ReceiptsSyncFeedTests
    {
        private class Scenario
        {
            public Scenario(ISpecProvider specProvider, int nonEmptyBlocks, int txPerBlock, int emptyBlocks = 0)
            {
                Blocks = new Block[_pivotNumber + 1];
                Blocks[0] = Build.A.Block.Genesis.TestObject;

                Block parent = Blocks[0];
                for (int blockNumber = 1; blockNumber <= _pivotNumber; blockNumber++)
                {
                    Block block = Build.A.Block
                        .WithParent(parent)
                        .WithTransactions(blockNumber > _pivotNumber - nonEmptyBlocks ? txPerBlock : 0, specProvider).TestObject;

                    if (blockNumber > _pivotNumber - nonEmptyBlocks - emptyBlocks)
                    {
                        Blocks[blockNumber] = block;
                    }

                    if (blockNumber == _pivotNumber - nonEmptyBlocks - emptyBlocks + 1)
                    {
                        LowestInsertedBody = block;
                    }

                    parent = block;
                }

                BlocksByHash = Blocks.Where(b => b is not null).ToDictionary(b => b.Hash, b => b);
            }

            public Dictionary<Keccak, Block> BlocksByHash;

            public Block[] Blocks;

            public Block LowestInsertedBody;
        }

        private static ISpecProvider _specProvider;
        private IReceiptStorage _receiptStorage;
        private ISyncPeerPool _syncPeerPool;
        private ISyncModeSelector _selector;
        private ReceiptsSyncFeed _feed;
        private ISyncConfig _syncConfig;
        private ISyncReport _syncReport;
        private IBlockTree _blockTree;

        private static long _pivotNumber = 1024;

        private static Scenario _1024BodiesWithOneTxEach;
        private static Scenario _256BodiesWithOneTxEach;
        private static Scenario _64BodiesWithOneTxEach;
        private static Scenario _64BodiesWithOneTxEachFollowedByEmpty;

        private MeasuredProgress _measuredProgress;
        private MeasuredProgress _measuredProgressQueue;

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
            _blockTree = Substitute.For<IBlockTree>();

            _syncConfig = new SyncConfig { FastBlocks = true };
            _syncConfig.PivotNumber = _pivotNumber.ToString();
            _syncConfig.PivotHash = Keccak.Zero.ToString();

            _syncPeerPool = Substitute.For<ISyncPeerPool>();
            _syncReport = Substitute.For<ISyncReport>();

            _measuredProgress = new MeasuredProgress();
            _measuredProgressQueue = new MeasuredProgress();
            _syncReport.FastBlocksReceipts.Returns(_measuredProgress);
            _syncReport.ReceiptsInQueue.Returns(_measuredProgressQueue);

            _selector = Substitute.For<ISyncModeSelector>();

            _feed = new ReceiptsSyncFeed(
                _selector,
                _specProvider,
                _blockTree,
                _receiptStorage,
                _syncPeerPool,
                _syncConfig,
                _syncReport,
                new EmptyBlockProcessingQueue(),
                LimboLogs.Instance);
        }

        [Test]
        public void Should_throw_when_fast_blocks_not_enabled()
        {
            _syncConfig = new SyncConfig { FastBlocks = false };
            Assert.Throws<InvalidOperationException>(
                () => _feed = new ReceiptsSyncFeed(
                    _selector,
                    _specProvider,
                    _blockTree,
                    _receiptStorage,
                    _syncPeerPool,
                    _syncConfig,
                    _syncReport,
                    new EmptyBlockProcessingQueue(),
                    LimboLogs.Instance));
        }

        [Test]
        public void Should_finish_on_start_when_receipts_not_stored()
        {
            _feed = new ReceiptsSyncFeed(
                _selector,
                _specProvider,
                _blockTree,
                NullReceiptStorage.Instance,
                _syncPeerPool,
                _syncConfig,
                _syncReport,
                new EmptyBlockProcessingQueue(),
                LimboLogs.Instance);

            ReceiptsSyncBatch? request = _feed.PrepareRequest();
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
            _feed.StateChanged += (s, e) => state = e.NewState;
            _feed.Activate();
            state.Should().Be(SyncFeedState.Active);
        }

        [Test]
        public void Feed_id_should_not_be_zero()
        {
            _feed.FeedId.Should().NotBe(0);
        }

        [Test]
        public void When_no_bodies_downloaded_then_request_will_be_empty()
        {
            _feed.PrepareRequest().Should().BeNull();
        }

        [Test]
        public void Returns_same_batch_until_filled()
        {
            LoadScenario(_256BodiesWithOneTxEach);
            ReceiptsSyncBatch request = _feed.PrepareRequest();
            _feed.HandleResponse(request);
            ReceiptsSyncBatch request2 = _feed.PrepareRequest();
            request2!.MinNumber.Should().Be(request!.MinNumber);
        }

        [Test]
        public void Can_create_a_final_batch()
        {
            LoadScenario(_64BodiesWithOneTxEachFollowedByEmpty);
            ReceiptsSyncBatch request = _feed.PrepareRequest();
            request.Should().NotBeNull();
            request!.MinNumber.Should().Be(1024);
            request.Prioritized.Should().Be(true);
        }

        [Test]
        public void When_configured_to_skip_receipts_then_finishes_immediately()
        {
            LoadScenario(_256BodiesWithOneTxEach);
            _syncConfig.DownloadReceiptsInFastSync = false;

            ReceiptsSyncBatch request = _feed.PrepareRequest();
            request.Should().BeNull();
            _feed.CurrentState.Should().Be(SyncFeedState.Finished);
            _measuredProgress.HasEnded.Should().BeTrue();
            _measuredProgressQueue.HasEnded.Should().BeTrue();
        }

        private void LoadScenario(Scenario scenario)
        {
            LoadScenario(scenario, new SyncConfig { FastBlocks = true });
        }

        private void LoadScenario(Scenario scenario, ISyncConfig syncConfig)
        {
            _syncConfig = syncConfig;
            _syncConfig.PivotNumber = _pivotNumber.ToString();
            _syncConfig.PivotHash = scenario.Blocks.Last().Hash.ToString();

            _feed = new ReceiptsSyncFeed(
                _selector,
                _specProvider,
                _blockTree,
                _receiptStorage,
                _syncPeerPool,
                _syncConfig,
                _syncReport,
                new EmptyBlockProcessingQueue(),
                LimboLogs.Instance);

            _blockTree.Genesis.Returns(scenario.Blocks[0].Header);
            _blockTree.FindCanonicalBlockInfo(Arg.Any<long>()).Returns(
                ci =>
                {
                    Block block = scenario.Blocks[ci.Arg<long>()];
                    if (block is null)
                    {
                        return null;
                    }

                    BlockInfo blockInfo = new(block.Hash, block.TotalDifficulty ?? 0);
                    blockInfo.BlockNumber = ci.Arg<long>();
                    return blockInfo;
                });

            _blockTree.FindBlock(Keccak.Zero, BlockTreeLookupOptions.None)
                .ReturnsForAnyArgs(ci =>
                    scenario.BlocksByHash.TryGetValue(ci.Arg<Keccak>(), out Block value) ? value : null);

            _blockTree.FindHeader(Keccak.Zero, BlockTreeLookupOptions.None)
                .ReturnsForAnyArgs(ci =>
                    scenario.BlocksByHash.TryGetValue(ci.Arg<Keccak>(), out Block value) ? value.Header
                        : null);

            _receiptStorage.LowestInsertedReceiptBlockNumber.Returns((long?)null);
            _blockTree.LowestInsertedBodyNumber.Returns(scenario.LowestInsertedBody.Number);
        }

        [Test]
        public void Can_create_receipts_batches_for_all_bodies_inserted_and_then_generate_null_batches_for_other_peers()
        {
            LoadScenario(_256BodiesWithOneTxEach);

            /* we have only 256 receipts altogether but we start with many peers
               so most of our requests will be empty */

            List<ReceiptsSyncBatch> batches = new();
            for (int i = 0; i < 100; i++)
            {
                batches.Add(_feed.PrepareRequest());
            }

            for (int i = 0; i < 2; i++)
            {
                batches[i].Should().NotBeNull();
                batches[i].ToString().Should().NotBeNull();
            }

            for (int i = 2; i < 100; i++)
            {
                batches[i].Should().BeNull();
            }
        }

        [Test]
        public async Task If_receipts_root_comes_invalid_then_reports_breach_of_protocol()
        {
            LoadScenario(_1024BodiesWithOneTxEach);
            ReceiptsSyncBatch batch = _feed.PrepareRequest();
            batch!.Response = new TxReceipt[batch.Infos.Length][];

            // default receipts that we use when constructing receipt root for tests have stats code 0
            // so by using 1 here we create a different tx root
            batch.Response[0] = new[] { Build.A.Receipt.WithStatusCode(1).TestObject };

            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            batch.ResponseSourcePeer = peerInfo;

            SyncResponseHandlingResult handlingResult = await _feed.HandleResponse(batch);
            handlingResult.Should().Be(SyncResponseHandlingResult.NoProgress);

            _syncPeerPool.Received().ReportBreachOfProtocol(peerInfo, InitiateDisconnectReason.InvalidReceiptRoot, Arg.Any<string>());
        }

        private static void FillBatchResponses(ReceiptsSyncBatch batch)
        {
            batch.Response = new TxReceipt[batch.Infos.Length][];
            for (int i = 0; i < batch.Response.Length; i++)
            {
                batch.Response[i] = new[] { Build.A.Receipt.TestObject };
            }
        }

        [Test]
        public void Can_sync_final_batch()
        {
            LoadScenario(_64BodiesWithOneTxEach);
            ReceiptsSyncBatch batch = _feed.PrepareRequest();

            FillBatchResponses(batch);
            _feed.HandleResponse(batch);
            _receiptStorage.LowestInsertedReceiptBlockNumber.Returns(1);
            _feed.PrepareRequest().Should().Be(null);

            _feed.CurrentState.Should().Be(SyncFeedState.Finished);
        }
    }
}
