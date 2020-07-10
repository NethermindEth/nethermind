//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    public class FastReceiptsSyncFeedTests
    {
        private class Scenario
        {
            public Scenario(int nonEmptyBlocks, int txPerBlock, int emptyBlocks = 0)
            {
                Blocks = new Block[_pivotNumber + 1];
                Blocks[0] = Build.A.Block.Genesis.TestObject;

                Block parent = Blocks[0];
                for (int blockNumber = 1; blockNumber <= _pivotNumber; blockNumber++)
                {
                    Block block = Build.A.Block
                        .WithParent(parent)
                        .WithTransactions(blockNumber > _pivotNumber - nonEmptyBlocks ? txPerBlock : 0).TestObject;
                    
                    if (blockNumber > emptyBlocks - nonEmptyBlocks - emptyBlocks)
                    {
                        Blocks[blockNumber] = block;
                    }

                    if (blockNumber == _pivotNumber - nonEmptyBlocks - emptyBlocks + 1)
                    {
                        LowestInsertedBody = block;
                    }

                    parent = block;
                }

                BlocksByHash = Blocks.ToDictionary(b => b.Hash, b => b);
            }

            public Dictionary<Keccak, Block> BlocksByHash;

            public Block[] Blocks;
            
            public Block? LowestInsertedBody;
        }

        private IReceiptStorage _receiptStorage;
        private ISyncPeerPool _syncPeerPool;
        private ISpecProvider _specProvider;
        private ISyncModeSelector _selector;
        private FastReceiptsSyncFeed _feed;
        private ISyncConfig _syncConfig;
        private ISyncReport _syncReport;
        private IBlockTree _blockTree;

        private static long _pivotNumber = 1024;

        private static Scenario _256BodiesWithOneTxEach;
        private static Scenario _64BodiesWithOneTxEach;
        private static Scenario _64BodiesWithOneTxEachFollowedByEmpty;

        private MeasuredProgress _measuredProgress;
        private MeasuredProgress _measuredProgressQueue;

        static FastReceiptsSyncFeedTests()
        {
            _256BodiesWithOneTxEach = new Scenario(256, 1);
            _64BodiesWithOneTxEach = new Scenario(64, 1);
            _64BodiesWithOneTxEachFollowedByEmpty = new Scenario(64, 1, 1024 - 64);
        }

        [SetUp]
        public void Setup()
        {
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _blockTree = Substitute.For<IBlockTree>();

            _syncConfig = new SyncConfig {FastBlocks = true};
            _syncConfig.PivotNumber = _pivotNumber.ToString();
            _syncConfig.PivotHash = Keccak.Zero.ToString();

            _specProvider = Substitute.For<ISpecProvider>();
            _syncPeerPool = Substitute.For<ISyncPeerPool>();
            _syncReport = Substitute.For<ISyncReport>();

            _measuredProgress = new MeasuredProgress();
            _measuredProgressQueue = new MeasuredProgress();
            _syncReport.FastBlocksReceipts.Returns(_measuredProgress);
            _syncReport.ReceiptsInQueue.Returns(_measuredProgressQueue);

            _selector = Substitute.For<ISyncModeSelector>();

            _feed = new FastReceiptsSyncFeed(
                _selector,
                _specProvider,
                _blockTree,
                _receiptStorage,
                _syncPeerPool,
                _syncConfig,
                _syncReport,
                LimboLogs.Instance);
        }

        [Test]
        public void Should_throw_when_fast_blocks_not_enabled()
        {
            _syncConfig = new SyncConfig {FastBlocks = false};
            Assert.Throws<InvalidOperationException>(
                () => _feed = new FastReceiptsSyncFeed(
                    _selector,
                    _specProvider,
                    _blockTree,
                    _receiptStorage,
                    _syncPeerPool,
                    _syncConfig,
                    _syncReport,
                    LimboLogs.Instance));
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
            _feed.PrepareRequest().Result.Should().BeNull();
        }

        [Test]
        public void Can_create_a_request_when_everything_ready()
        {
            int expectedBatchSize = 128;
            LoadScenario(_256BodiesWithOneTxEach);
            ReceiptsSyncBatch request = _feed.PrepareRequest().Result;
            request.Should().NotBeNull();
            request.MinNumber.Should().Be(_pivotNumber - expectedBatchSize + 1);
            request.Blocks.Length.Should().Be(expectedBatchSize);
            request.Predecessors.Length.Should().Be(expectedBatchSize);
            request.Request.Length.Should().Be(expectedBatchSize);
            request.StartNumber.Should().Be(_pivotNumber - expectedBatchSize + 1);
            request.EndNumber.Should().Be(_pivotNumber);
            request.On.Should().Be(long.MaxValue);
            request.Description.Should().NotBeNull();
            request.Prioritized.Should().Be(true);
        }
        
        [Test]
        public void Returns_same_batch_until_filled()
        {
            LoadScenario(_256BodiesWithOneTxEach);
            ReceiptsSyncBatch request = _feed.PrepareRequest().Result;
            _feed.HandleResponse(request);
            ReceiptsSyncBatch request2 = _feed.PrepareRequest().Result;
            request2.Should().Be(request);
        }
        
        [Test]
        public void Can_create_non_geth_requests()
        {
            int expectedBatchSize = 256;
            _syncConfig.UseGethLimitsInFastBlocks = false;
            LoadScenario(_256BodiesWithOneTxEach, _syncConfig);
            ReceiptsSyncBatch request = _feed.PrepareRequest().Result;
            request.Should().NotBeNull();
            request.MinNumber.Should().Be(_pivotNumber - expectedBatchSize + 1);
            request.Blocks.Length.Should().Be(expectedBatchSize);
            request.Predecessors.Length.Should().Be(expectedBatchSize);
            request.Request.Length.Should().Be(expectedBatchSize);
            request.StartNumber.Should().Be(_pivotNumber - expectedBatchSize + 1);
            request.EndNumber.Should().Be(_pivotNumber);
            request.On.Should().Be(long.MaxValue);
            request.Description.Should().NotBeNull();
            request.Prioritized.Should().Be(true);
        }

        [Test]
        public void Can_create_a_smaller_request()
        {
            int expectedBatchSize = 64;
            LoadScenario(_64BodiesWithOneTxEach);
            ReceiptsSyncBatch request = _feed.PrepareRequest().Result;
            request.Should().NotBeNull();
            request.MinNumber.Should().Be(_pivotNumber - expectedBatchSize + 1);
            request.Blocks.Length.Should().Be(expectedBatchSize);
            request.Predecessors.Length.Should().Be(expectedBatchSize);
            request.Request.Length.Should().Be(expectedBatchSize);
            request.StartNumber.Should().Be(_pivotNumber - expectedBatchSize + 1);
            request.EndNumber.Should().Be(_pivotNumber);
            request.On.Should().Be(long.MaxValue);
            request.Description.Should().NotBeNull();
            request.Prioritized.Should().Be(true);
        }
        
        [Test]
        public void Can_create_a_final_batch()
        {
            int expectedBatchSize = 64;
            LoadScenario(_64BodiesWithOneTxEachFollowedByEmpty);
            ReceiptsSyncBatch request = _feed.PrepareRequest().Result;
            request.Should().NotBeNull();
            request.MinNumber.Should().Be(961);
            request.Blocks.Length.Should().Be(expectedBatchSize);
            request.Predecessors.Length.Should().Be(expectedBatchSize);
            request.Request.Length.Should().Be(expectedBatchSize);
            request.StartNumber.Should().Be(961);
            request.EndNumber.Should().Be(_pivotNumber);
            request.On.Should().Be(long.MaxValue);
            request.Description.Should().NotBeNull();
            request.Prioritized.Should().Be(true);
            request.IsFinal.Should().BeTrue();
        }

        [Test]
        public void When_configured_to_skip_receipts_then_finishes_immediately()
        {
            LoadScenario(_256BodiesWithOneTxEach);
            _syncConfig.DownloadReceiptsInFastSync = false;

            ReceiptsSyncBatch request = _feed.PrepareRequest().Result;
            request.Should().BeNull();
            _feed.CurrentState.Should().Be(SyncFeedState.Finished);
            _measuredProgress.HasEnded.Should().BeTrue();
            _measuredProgressQueue.HasEnded.Should().BeTrue();
        }

        [Test]
        public void Does_not_create_the_request_if_lag_behind_bodies_requirement_is_not_satisfied()
        {
            LoadScenario(_256BodiesWithOneTxEach);
            _feed.LagBehindBodies = 1024;
            _feed.PrepareRequest().Result.Should().BeNull();
        }

        [Test]
        public void Default_lag_is_correct()
        {
            _feed.LagBehindBodies.Should().Be(FastBlocksLags.ForReceipts);
        }

        private void LoadScenario(Scenario scenario)
        {
            LoadScenario(scenario, new SyncConfig{FastBlocks = true});
        }
        
        private void LoadScenario(Scenario scenario, ISyncConfig syncConfig)
        {
            _syncConfig = syncConfig;
            _syncConfig.PivotNumber = _pivotNumber.ToString();
            _syncConfig.PivotHash = scenario.Blocks.Last().Hash.ToString();

            _feed = new FastReceiptsSyncFeed(
                _selector,
                _specProvider,
                _blockTree,
                _receiptStorage,
                _syncPeerPool,
                _syncConfig,
                _syncReport,
                LimboLogs.Instance);

            _feed.LagBehindBodies = 0;

            _blockTree.Genesis.Returns(scenario.Blocks[0].Header);
            _blockTree.FindBlock(Keccak.Zero, BlockTreeLookupOptions.None)
                .ReturnsForAnyArgs(ci =>
                    scenario.BlocksByHash.ContainsKey(ci.Arg<Keccak>())
                        ? scenario.BlocksByHash[ci.Arg<Keccak>()]
                        : null);

            _receiptStorage.LowestInsertedReceiptBlock.Returns((long?) null);
            _blockTree.LowestInsertedBody.Returns(scenario.LowestInsertedBody);
        }
        
        [Test]
        public void Can_fully_sync_with_full_batches_when_lots_of_peers_are_available()
        {
            LoadScenario(_256BodiesWithOneTxEach);
            
            /* we have only 256 receipts altogether but we start with many peers
               so most of our requests will be empty */
            
            List<ReceiptsSyncBatch> batches = new List<ReceiptsSyncBatch>();
            for (int i = 0; i < 100; i++)
            {
                batches.Add(_feed.PrepareRequest().Result);
            }
            
            
        }
    }
}