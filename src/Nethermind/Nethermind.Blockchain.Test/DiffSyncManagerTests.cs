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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class DiffSyncManagerTests
    {
        private static Block _genesisBlock = Build.A.Block.Genesis.TestObject;
        
        private class SyncPeerMock : ISynchronizationPeer
        {   
            public List<Block> Blocks { get; set; } = new List<Block>();

            public SyncPeerMock(string peerName)
            {
                NodeStats = new NodeStatsLight(new Node(NodeId), new StatsConfig(), LimboLogs.Instance);
                Blocks.Add(_genesisBlock);
                ClientId = peerName;
            }

            public bool IsFastSyncSupported => false;

            public NodeId NodeId { get; } = new NodeId(Build.A.PrivateKey.TestObject.PublicKey);

            public INodeStats NodeStats { get; }

            public string ClientId { get; }

            public Task<Block[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
            {
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
                    throw new NotImplementedException();
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
                }

                return Task.FromResult(result);
            }

            public Task<BlockHeader[]> GetBlockHeaders(UInt256 number, int maxBlocks, int skip, CancellationToken token)
            {
                if (skip != 0)
                {
                    throw new NotImplementedException();
                }

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
                }

                return Task.FromResult(result);
            }

            public Task<Keccak> GetHeadBlockHash(CancellationToken token)
            {
                return Task.FromResult(Blocks.Last().Hash);
            }

            public Task<UInt256> GetHeadBlockNumber(CancellationToken token)
            {
                return Task.FromResult(Blocks.Last().Number);
            }

            public Task<UInt256> GetHeadDifficulty(CancellationToken token)
            {
                return Task.FromResult(Blocks.Last().Difficulty);
            }

            public void SendNewBlock(Block block)
            {
            }

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
        }

        private readonly TimeSpan _standardTimeoutUnit = TimeSpan.FromMilliseconds(5000);

        public class MockPerfService : IPerfService
        {
            public Guid StartPerfCalc()
            {
                return Guid.NewGuid();
            }

            public void EndPerfCalc(Guid id, string logMsg)
            {
            }

            public long? EndPerfCalc(Guid id)
            {
                return 0;
            }

            public bool LogOnDebug { get; set; } = false;
        }

        public class When
        {
            public static SyncingContext Syncing => new SyncingContext();
        }

        public class SyncingContext
        {
            private Dictionary<string, ISynchronizationPeer> _peers = new Dictionary<string, ISynchronizationPeer>();

            private BlockTree BlockTree { get; set; }

            private DiffSyncManager SyncManager { get; set; }

            public SyncingContext()
            {
                MemDb stateDb = new MemDb();
                BlockTree = new BlockTree(new MemDb(), new MemDb(), new SingleReleaseSpecProvider(Constantinople.Instance, 1), NullTransactionPool.Instance, LimboLogs.Instance);
                SyncManager = new DiffSyncManager(
                    stateDb,
                    BlockTree,
                    TestBlockValidator.AlwaysValid,
                    TestHeaderValidator.AlwaysValid,
                    TestTransactionValidator.AlwaysValid,
                    LimboLogs.Instance,
                    new BlockchainConfig(),
                    new MockPerfService(),
                    NullReceiptStorage.Instance);
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

            public SyncingContext BlockIs(BlockHeader header)
            {
                Assert.AreSame(header, _blockHeader);
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
                _peers.Add(syncPeer.ClientId, syncPeer);
                return this;
            }
        }

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void Init_condition_are_as_expected()
        {
            When.Syncing
                .AfterProcessingGenesis()
                .BestKnownNumberIs(0)
                .Genesis.BlockIsKnown()
                .BestSuggested.BlockIsSameAsGenesis();
        }

        [Test]
        public void Can_sync_with_one_peer_straight()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggested.BlockIsSameAsGenesis();
        }
    }
}