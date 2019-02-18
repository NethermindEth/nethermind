/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class QueueBasedSyncManagerTests
    {
        private static Block _genesisBlock = Build.A.Block.Genesis.TestObject;

        private class SyncPeerMock : ISynchronizationPeer
        {
            private readonly bool _causeTimeoutOnInit;
            private readonly bool _causeTimeoutOnBlocks;
            public List<Block> Blocks { get; set; } = new List<Block>();

            public Block HeadBlock => Blocks.Last();

            public BlockHeader HeadHeader => HeadBlock.Header;

            public SyncPeerMock(string peerName, bool causeTimeoutOnInit = false, bool causeTimeoutOnBlocks = false)
            {
                _causeTimeoutOnInit = causeTimeoutOnInit;
                _causeTimeoutOnBlocks = causeTimeoutOnBlocks;
                Blocks.Add(_genesisBlock);
                ClientId = peerName;
            }

            public Guid SessionId { get; } = Guid.NewGuid();
            public bool IsFastSyncSupported => false;

            public Node Node { get; } = new Node(Build.A.PrivateKey.TestObject.PublicKey, "127.0.0.1", 1234);

            public string ClientId { get; }

            public UInt256 TotalDifficultyOnSessionStart =>
                (UInt256)((Blocks?.LastOrDefault()?.Difficulty ?? UInt256.Zero) * (BigInteger)((UInt256)(Blocks?.Count ?? 0)- UInt256.One)
                + _genesisBlock.Difficulty);

            public Task<Block[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
            {
                if (_causeTimeoutOnBlocks)
                {
                    return Task.FromException<Block[]>(new TimeoutException());
                }

                Block[] result = new Block[blockHashes.Length];
                for (int i = 0; i < blockHashes.Length; i++)
                {
                    foreach (Block block in Blocks)
                    {
                        if (block.Hash == blockHashes[i])
                        {
                            result[i] = block;
                        }
                    }
                }

                return Task.FromResult(result);
            }

            public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
            {
                if (skip != 0)
                {
                    return Task.FromException<BlockHeader[]>(new TimeoutException());
                }

                int filled = 0;
                bool started = false;
                BlockHeader[] result = new BlockHeader[maxBlocks];
                foreach (Block block in Blocks)
                {
                    if (block.Hash == blockHash)
                    {
                        started = true;
                    }

                    if (started)
                    {
                        result[filled++] = block.Header;
                    }
                    
                    if (filled >= maxBlocks)
                    {
                        break;
                    }
                }

                return Task.FromResult(result);
            }

            public Task<BlockHeader[]> GetBlockHeaders(UInt256 number, int maxBlocks, int skip, CancellationToken token)
            {
                int filled = 0;
                bool started = false;
                BlockHeader[] result = new BlockHeader[maxBlocks];
                foreach (Block block in Blocks)
                {
                    if (block.Number == number)
                    {
                        started = true;
                    }

                    if (started)
                    {
                        result[filled++] = block.Header;
                    }

                    if (filled >= maxBlocks)
                    {
                        break;
                    }
                }

                return Task.FromResult(result);
            }

            public Task<BlockHeader> GetHeadBlockHeader(CancellationToken token)
            {
                if (_causeTimeoutOnInit)
                {
                    return Task.FromException<BlockHeader>(new TimeoutException());
                }

                return Task.FromResult(Blocks.Last().Header);
            }

            public void SendNewBlock(Block block)
            {
                ReceivedBlocks.Push(block);
            }

            public Stack<Block> ReceivedBlocks { get; set; } = new Stack<Block>();
            
            public void SendNewTransaction(Transaction transaction)
            {
            }

            public Task<TransactionReceipt[][]> GetReceipts(Keccak[] blockHash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<byte[][]> GetNodeData(Keccak[] hashes, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public void AddBlocksUpTo(int i, int branchStart = 0, byte branchIndex = 0)
            {
                Block block = Blocks.Last();
                for (UInt256 j = block.Number; j < i; j++)
                {
                    block = Build.A.Block.WithParent(block).WithExtraData(j < branchStart ? Bytes.Empty : new byte[] {branchIndex}).TestObject;
                    Blocks.Add(block);
                }
            }
            
            public void AddHighDifficultyBlocksUpTo(int i, int branchStart = 0, byte branchIndex = 0)
            {
                Block block = Blocks.Last();
                for (UInt256 j = block.Number; j < i; j++)
                {
                    block = Build.A.Block.WithParent(block).WithDifficulty(2000000).WithExtraData(j < branchStart ? Bytes.Empty : new byte[] {branchIndex}).TestObject;
                    Blocks.Add(block);
                }
            }
        }

        public class When
        {
            public static SyncingContext Syncing => new SyncingContext();
        }

        public class SyncingContext
        {
            private Dictionary<string, ISynchronizationPeer> _peers = new Dictionary<string, ISynchronizationPeer>();

            private BlockTree BlockTree { get; set; }

            private ISynchronizationManager SyncManager { get; set; }

            public SyncingContext()
            {
                MemDb stateDb = new MemDb();
                BlockTree = new BlockTree(new MemDb(), new MemDb(), new SingleReleaseSpecProvider(Constantinople.Instance, 1), NullTransactionPool.Instance, LimboLogs.Instance);
                SyncManager = new QueueBasedSyncManager(
                    stateDb,
                    BlockTree,
                    TestBlockValidator.AlwaysValid,
                    TestHeaderValidator.AlwaysValid,
                    TestTransactionValidator.AlwaysValid,
                    LimboLogs.Instance,
                    new BlockchainConfig(),
                    new NodeStatsManager(new StatsConfig(), LimboLogs.Instance), 
                    new PerfService(LimboLogs.Instance),
                    NullReceiptStorage.Instance);


                SyncManager.Start();
                SyncManager.SyncEvent += (sender, args) => TestContext.WriteLine(args.SyncStatus);
            }

            public SyncingContext BestKnownNumberIs(UInt256 number)
            {
                Assert.AreEqual(number, BlockTree.BestKnownNumber);
                return this;
            }

            public SyncingContext BestSuggestBlockIs(BlockHeader blockHeader)
            {
                Assert.AreSame(blockHeader, BlockTree.BestSuggested);
                return this;
            }

            public SyncingContext BlockIsKnown()
            {
                Assert.True(BlockTree.IsKnownBlock(_blockHeader.Number, _blockHeader.Hash));
                return this;
            }

            public SyncingContext HeaderIs(BlockHeader header)
            {
                Assert.AreSame(header, _blockHeader);
                return this;
            }

            public SyncingContext BlockHasNumber(UInt256 number)
            {
                Assert.AreEqual(number, _blockHeader.Number);
                return this;
            }

            public SyncingContext BlockIsSameAsGenesis()
            {
                Assert.AreSame(BlockTree.Genesis, _blockHeader);
                return this;
            }

            private BlockHeader _blockHeader;

            public SyncingContext Genesis
            {
                get
                {
                    _blockHeader = BlockTree.Genesis;
                    return this;
                }
            }

            public SyncingContext Wait(int milliseconds)
            {
                Thread.Sleep(milliseconds);
                return this;
            }

            public SyncingContext Wait()
            {
                return Wait(WaitTime);
            }

            public SyncingContext After(Action action)
            {
                action();
                return this;
            }

            public SyncingContext BestSuggested
            {
                get
                {
                    _blockHeader = BlockTree.BestSuggested;
                    return this;
                }
            }

            public SyncingContext AfterProcessingGenesis()
            {
                Block genesis = _genesisBlock;
                BlockTree.SuggestBlock(genesis);
                BlockTree.UpdateMainChain(genesis);
                return this;
            }

            public SyncingContext AfterPeerIsAdded(ISynchronizationPeer syncPeer)
            {
                _peers.TryAdd(syncPeer.ClientId, syncPeer);
                SyncManager.AddPeer(syncPeer);
                return this;
            }

            public SyncingContext AfterPeerIsRemoved(ISynchronizationPeer syncPeer)
            {
                _peers.Remove(syncPeer.ClientId);
                SyncManager.RemovePeer(syncPeer);
                return this;
            }

            public SyncingContext AfterNewBlockMessage(Block block, ISynchronizationPeer peer)
            {
                block.TotalDifficulty = (UInt256)(block.Difficulty * ((BigInteger)block.Number + 1));
                SyncManager.AddNewBlock(block, peer.Node.Id);
                return this;
            }
            
            public SyncingContext AfterHintBlockMessage(Block block, ISynchronizationPeer peer)
            {
                SyncManager.HintBlock(block.Hash, block.Number, peer.Node.Id);
                return this;
            }

            public SyncingContext PeerCountIs(long i)
            {
                Assert.AreEqual(i, Metrics.SyncPeers);
                return this;
            }

            public SyncingContext WaitAMoment()
            {
                return Wait(Moment);
            }

            public SyncingContext Stop()
            {
                var task = new Task(async () => { await SyncManager.StopAsync();});
                task.RunSynchronously();
                return this;
            }
        }

        [SetUp]
        public void Setup()
        {
        }
        
        [SetUp]
        public void TearDown()
        {
        }

        [Test]
        public void Init_condition_are_as_expected()
        {
            When.Syncing
                .AfterProcessingGenesis()
                .BestKnownNumberIs(0)
                .Genesis.BlockIsKnown()
                .BestSuggested.BlockIsSameAsGenesis().Stop();
        }

        [Test]
        public void Can_sync_with_one_peer_straight()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggested.BlockIsSameAsGenesis().Stop();
        }

        [Test]
        public void Can_sync_with_one_peer_straight_and_extend_chain()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(3);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader).Stop();
        }

        [Test]
        public void Will_ignore_blocks_it_does_not_know_about()
        {
            // testing the test framework here
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .After(() => peerA.AddBlocksUpTo(2))
                .Wait()
                .BestSuggested.BlockHasNumber(1).Stop();
        }

        [Test]
        public void Can_extend_chain_by_one_on_new_block_message()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .After(() => peerA.AddBlocksUpTo(2))
                .AfterNewBlockMessage(peerA.HeadBlock, peerA)
                .BestSuggested.HeaderIs(peerA.HeadHeader).Stop();
        }
        
        [Test]
        public void Can_reorg_on_new_block_message()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(3);
            
            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(3);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .AfterPeerIsAdded(peerB)
                .Wait()
                .After(() => peerB.AddBlocksUpTo(6))
                .AfterNewBlockMessage(peerB.HeadBlock, peerB)
                .Wait()
                .BestSuggested.HeaderIs(peerB.HeadHeader).Stop();
        }
        
        [Test]
        [Ignore("Not supported for now - still analyzing this scenario")]
        public void Can_reorg_on_hint_block_message()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(3);
            
            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(3);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .AfterPeerIsAdded(peerB)
                .Wait()
                .After(() => peerB.AddBlocksUpTo(6))
                .AfterHintBlockMessage(peerB.HeadBlock, peerB)
                .Wait()
                .BestSuggested.HeaderIs(peerB.HeadHeader).Stop();
        }
        
        [Test]
        public void Can_extend_chain_by_one_on_block_hint_message()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .After(() => peerA.AddBlocksUpTo(2))
                .AfterHintBlockMessage(peerA.HeadBlock, peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader).Stop();
        }

        [Test]
        public void Can_extend_chain_by_more_than_one_on_new_block_message()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .After(() => peerA.AddBlocksUpTo(8))
                .AfterNewBlockMessage(peerA.HeadBlock, peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader).Stop();
        }

        [Test]
        public void Can_sync_when_best_peer_is_timing_out()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .After(() => peerA.AddBlocksUpTo(8))
                .AfterNewBlockMessage(peerA.HeadBlock, peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader).Stop();
        }

        [Test]
        public void Will_ignore_new_block_that_is_far_ahead()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            SyncPeerMock badPeer = new SyncPeerMock("B", false, true);
            badPeer.AddBlocksUpTo(20);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(badPeer)
                .Wait()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .BestSuggested.BlockHasNumber(1).Stop();
        }
        
        [Test]
        public void Will_inform_connecting_peer_about_the_alternative_branch_with_same_difficulty()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(2);
            
            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(2, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .AfterPeerIsAdded(peerB)
                .Wait()
                .BestSuggested.BlockHasNumber(2).Stop();
            
            Assert.AreNotEqual(peerB.HeadBlock.Hash, peerA.HeadBlock.Hash);
            Assert.AreEqual(peerB.ReceivedBlocks.Peek().Hash, peerA.HeadBlock.Hash);
        }

        [Test]
        public void Will_not_add_same_peer_twice()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .AfterPeerIsAdded(peerA)
                .WaitAMoment()
                .PeerCountIs(1)
                .BestSuggested.BlockHasNumber(1).Stop();
        }
        
        [Test]
        public void Will_remove_peer_when_init_fails()
        {
            SyncPeerMock peerA = new SyncPeerMock("A", true, true);
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .WaitAMoment()
                .PeerCountIs(0).Stop();
        }


        [Test]
        public void Can_remove_peers()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            SyncPeerMock peerB = new SyncPeerMock("B");

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .AfterPeerIsAdded(peerB)
                .WaitAMoment()
                .PeerCountIs(2)
                .AfterPeerIsRemoved(peerB)
                .WaitAMoment()
                .PeerCountIs(1)
                .AfterPeerIsRemoved(peerA)
                .WaitAMoment()
                .PeerCountIs(0).Stop();
        }

        [Test]
        public void Can_reorg_on_add_peer()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(QueueBasedSyncManager.MaxBatchSize, 0, 0);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(QueueBasedSyncManager.MaxBatchSize * 2, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader)
                .AfterPeerIsAdded(peerB)
                .Wait()
                .BestSuggested.HeaderIs(peerB.HeadHeader).Stop();
        }
        
        [Test]
        public void Can_reorg_based_on_total_difficulty()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(10, 0, 0);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddHighDifficultyBlocksUpTo(6, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader)
                .AfterPeerIsAdded(peerB)
                .Wait()
                .BestSuggested.HeaderIs(peerB.HeadHeader).Stop();
        }
        
        [Test]
        [Ignore("Not supported for now - still analyzing this scenario")]
        public void Can_extend_chain_on_hint_block_when_high_difficulty_low_number()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(10, 0, 0);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddHighDifficultyBlocksUpTo(5, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .AfterPeerIsAdded(peerB)
                .Wait()
                .After(() => peerB.AddHighDifficultyBlocksUpTo(6, 0, 1))
                .AfterHintBlockMessage(peerB.HeadBlock, peerB)
                .BestSuggested.HeaderIs(peerB.HeadHeader).Stop();
        }
        
        [Test]
        public void Can_extend_chain_on_new_block_when_high_difficulty_low_number()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(10, 0, 0);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddHighDifficultyBlocksUpTo(6, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .AfterPeerIsAdded(peerB)
                .Wait()
                .After(() => peerB.AddHighDifficultyBlocksUpTo(6, 0, 1))
                .AfterNewBlockMessage(peerB.HeadBlock, peerB)
                .BestSuggested.HeaderIs(peerB.HeadHeader).Stop();
        }

        [Test]
        public void Will_not_reorganize_on_same_chain_length()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(10, 0, 0);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(10, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader)
                .AfterPeerIsAdded(peerB)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader).Stop();
        }
        
        [Test]
        public void Will_not_reorganize_more_than_max_reorg_length()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(QueueBasedSyncManager.MaxReorganizationLength + 1, 0, 0);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(QueueBasedSyncManager.MaxReorganizationLength + 2, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader)
                .AfterPeerIsAdded(peerB)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader).Stop();
        }
        
        [Test]
        public void Can_sync_more_than_a_batch()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(QueueBasedSyncManager.MaxBatchSize * 3, 0, 0);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader).Stop();
        }
        
        [Test]
        public void Can_sync_exactly_one_batch()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(QueueBasedSyncManager.MaxBatchSize, 0, 0);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .Wait()
                .BestSuggested.HeaderIs(peerA.HeadHeader)
                .Stop();
        }
        
        [Test]
        public void Can_stop()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(QueueBasedSyncManager.MaxBatchSize, 0, 0);

            When.Syncing
                .Stop();
        }

        private const int Moment = 50;
        private const int WaitTime = 1000;
    }
}