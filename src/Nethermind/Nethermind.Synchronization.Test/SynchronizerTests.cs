//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Db.Blooms;
using Nethermind.State.Witnesses;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [TestFixture(SynchronizerType.Fast)]
    [TestFixture(SynchronizerType.Full)]
    [TestFixture(SynchronizerType.Eth2Merge)]
    [Parallelizable(ParallelScope.All)]
    public class SynchronizerTests
    {
        private readonly SynchronizerType _synchronizerType;

        public SynchronizerTests(SynchronizerType synchronizerType)
        {
            _synchronizerType = synchronizerType;
        }

        private static Block _genesisBlock = Build.A.Block.Genesis.WithDifficulty(100000).WithTotalDifficulty((UInt256)100000).TestObject;

        private class SyncPeerMock : ISyncPeer
        {
            private readonly bool _causeTimeoutOnInit;
            private readonly bool _causeTimeoutOnBlocks;
            private readonly bool _causeTimeoutOnHeaders;
            private List<Block> Blocks { get; } = new List<Block>();

            public Block HeadBlock => Blocks.Last();

            public BlockHeader HeadHeader => HeadBlock.Header;

            public SyncPeerMock(string peerName, bool causeTimeoutOnInit = false, bool causeTimeoutOnBlocks = false, bool causeTimeoutOnHeaders = false)
            {
                _causeTimeoutOnInit = causeTimeoutOnInit;
                _causeTimeoutOnBlocks = causeTimeoutOnBlocks;
                _causeTimeoutOnHeaders = causeTimeoutOnHeaders;
                Blocks.Add(_genesisBlock);
                UpdateHead();
                ClientId = peerName;
            }

            private void UpdateHead()
            {
                HeadHash = HeadBlock.Hash;
                HeadNumber = HeadBlock.Number;
                TotalDifficulty = HeadBlock.TotalDifficulty ?? 0;
            }

            public Node Node { get; } = new Node(Build.A.PrivateKey.TestObject.PublicKey, "127.0.0.1", 1234);

            public string ClientId { get; }
            public Keccak HeadHash { get; set; }
            public long HeadNumber { get; set; }
            public UInt256 TotalDifficulty { get; set; }

            public bool IsInitialized { get; set; }

            public void Disconnect(DisconnectReason reason, string details)
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
            }

            public Task<BlockBody[]> GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
            {
                if (_causeTimeoutOnBlocks)
                {
                    return Task.FromException<BlockBody[]>(new TimeoutException());
                }

                BlockBody[] result = new BlockBody[blockHashes.Count];
                for (int i = 0; i < blockHashes.Count; i++)
                {
                    foreach (Block block in Blocks)
                    {
                        if (block.Hash == blockHashes[i])
                        {
                            result[i] = new BlockBody(block.Transactions, block.Ommers);
                        }
                    }
                }

                return Task.FromResult(result);
            }

            public Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
            {
                if (_causeTimeoutOnHeaders)
                {
                    return Task.FromException<BlockHeader[]>(new TimeoutException());
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

                    if (filled >= maxBlocks)
                    {
                        break;
                    }
                }

                return Task.FromResult(result);
            }

            public async Task<BlockHeader> GetHeadBlockHeader(Keccak hash, CancellationToken token)
            {
                if (_causeTimeoutOnInit)
                {
                    Console.WriteLine("RESPONDING TO GET HEAD BLOCK HEADER WITH EXCEPTION");
                    await Task.FromException<BlockHeader>(new TimeoutException());
                }

                BlockHeader header;
                try
                {
                    header = Blocks.Last().Header;
                }
                catch (Exception)
                {
                    Console.WriteLine("RESPONDING TO GET HEAD BLOCK HEADER EXCEPTION");
                    throw;
                }

                Console.WriteLine($"RESPONDING TO GET HEAD BLOCK HEADER WITH RESULT {header.Number}");
                return header;
            }

            public void NotifyOfNewBlock(Block block, SendBlockPriority priority)
            {
                if (priority == SendBlockPriority.High)
                    ReceivedBlocks.Push(block);
            }

            public ConcurrentStack<Block> ReceivedBlocks { get; } = new ConcurrentStack<Block>();
            
            public event EventHandler Disconnected;

            public PublicKey Id => Node.Id;

            public bool SendNewTransaction(Transaction transaction, bool isPriority) => true;

            public Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public void AddBlocksUpTo(int i, int branchStart = 0, byte branchIndex = 0)
            {
                Block block = Blocks.Last();
                for (long j = block.Number; j < i; j++)
                {
                    block = Build.A.Block.WithDifficulty(1000000).WithParent(block).WithTotalDifficulty(block.TotalDifficulty + 1000000).WithExtraData(j < branchStart ? Array.Empty<byte>() : new [] {branchIndex}).TestObject;
                    Blocks.Add(block);
                }
                
                UpdateHead();
            }

            public void AddHighDifficultyBlocksUpTo(int i, int branchStart = 0, byte branchIndex = 0)
            {
                Block block = Blocks.Last();
                for (long j = block.Number; j < i; j++)
                {
                    block = Build.A.Block.WithParent(block).WithDifficulty(2000000).WithTotalDifficulty(block.TotalDifficulty + 2000000).WithExtraData(j < branchStart ? Array.Empty<byte>() : new [] {branchIndex}).TestObject;
                    Blocks.Add(block);
                }
                
                UpdateHead();
            }

            public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
            {
                throw new NotImplementedException();
            }

            public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
            {
                throw new NotImplementedException();
            }
        }

        private WhenImplementation When => new WhenImplementation(_synchronizerType);

        private class WhenImplementation
        {
            private readonly SynchronizerType _synchronizerType;

            public WhenImplementation(SynchronizerType synchronizerType)
            {
                _synchronizerType = synchronizerType;
            }

            public SyncingContext Syncing => new SyncingContext(_synchronizerType);
        }

        public class SyncingContext
        {
            public static ConcurrentBag<SyncingContext> AllInstances = new ConcurrentBag<SyncingContext>();

            private Dictionary<string, ISyncPeer> _peers = new Dictionary<string, ISyncPeer>();
            private BlockTree BlockTree { get; }

            private ISyncServer SyncServer { get; }

            private ISynchronizer Synchronizer { get; }

            private ISyncPeerPool SyncPeerPool { get; }

//            ILogManager _logManager = LimboLogs.Instance;
            ILogManager _logManager = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Debug));

            private ILogger _logger;

            public SyncingContext(SynchronizerType synchronizerType)
            {
                ISyncConfig GetSyncConfig() =>
                    synchronizerType switch
                    {
                        SynchronizerType.Fast => SyncConfig.WithFastSync,
                        SynchronizerType.Eth2Merge => SyncConfig.WithEth2Merge,
                        SynchronizerType.Full => SyncConfig.WithFullSyncOnly,
                        _ => throw new ArgumentOutOfRangeException(nameof(synchronizerType), synchronizerType, null)
                    };

                _logger = _logManager.GetClassLogger();
                ISyncConfig syncConfig = GetSyncConfig();
                IDbProvider dbProvider = TestMemDbProvider.Init();
                IDb stateDb = new MemDb();
                IDb codeDb = dbProvider.CodeDb;
                MemDb blockInfoDb = new MemDb();
                BlockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), new SingleReleaseSpecProvider(Constantinople.Instance, 1), NullBloomStorage.Instance, _logManager);
                ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
                NodeStatsManager stats = new NodeStatsManager(timerFactory, _logManager);
                SyncPeerPool = new SyncPeerPool(BlockTree, stats, 25, _logManager);

                SyncProgressResolver syncProgressResolver = new SyncProgressResolver(
                    BlockTree,
                    NullReceiptStorage.Instance,
                    stateDb,
                    new MemDb(),
                    new TrieStore(stateDb, LimboLogs.Instance),
                    syncConfig,
                    _logManager);
                MultiSyncModeSelector syncModeSelector = new MultiSyncModeSelector(syncProgressResolver, SyncPeerPool, syncConfig, _logManager);
                Synchronizer = new Synchronizer(
                    dbProvider,
                    MainnetSpecProvider.Instance,
                    BlockTree,
                    NullReceiptStorage.Instance,
                    Always.Valid,
                    Always.Valid,
                    SyncPeerPool,
                    stats,
                    syncModeSelector,
                    syncConfig,
                    _logManager);

                SyncServer = new SyncServer(
                    stateDb,
                    codeDb,
                    BlockTree,
                    NullReceiptStorage.Instance,
                    Always.Valid,
                    Always.Valid,
                    SyncPeerPool,
                    syncModeSelector,
                    syncConfig,
                    new WitnessCollector(new MemDb(), LimboLogs.Instance), 
                    _logManager);
                
                SyncPeerPool.Start();

                Synchronizer.Start();
                Synchronizer.SyncEvent += SynchronizerOnSyncEvent;

                AllInstances.Add(this);
            }

            private void SynchronizerOnSyncEvent(object sender, SyncEventArgs e)
            {
                TestContext.WriteLine(e.SyncEvent);
            }

            public SyncingContext BestKnownNumberIs(long number)
            {
                Assert.AreEqual(number, BlockTree.BestKnownNumber, "best known number");
                return this;
            }
            
            public SyncingContext BlockIsKnown()
            {
                Assert.True(BlockTree.IsKnownBlock(_blockHeader.Number, _blockHeader.Hash), "block is known");
                return this;
            }

            private const int dynamicTimeout = 5000;

            public SyncingContext BestSuggestedHeaderIs(BlockHeader header)
            {
                int waitTimeSoFar = 0;
                _blockHeader = BlockTree.BestSuggestedHeader;
                while (header != _blockHeader && waitTimeSoFar <= dynamicTimeout)
                {
                    _logger.Info($"ASSERTING THAT HEADER IS {header.Number} (WHEN ACTUALLY IS {_blockHeader?.Number})");
                    Thread.Sleep(100);
                    waitTimeSoFar += 100;
                    _blockHeader = BlockTree.BestSuggestedHeader;
                }

                Assert.AreSame(header, _blockHeader, "header");
                return this;
            }

            public SyncingContext BestSuggestedBlockHasNumber(long number)
            {
                _logger.Info($"ASSERTING THAT NUMBER IS {number}");

                int waitTimeSoFar = 0;
                _blockHeader = BlockTree.BestSuggestedHeader;
                while (number != _blockHeader?.Number && waitTimeSoFar <= dynamicTimeout)
                {
                    Thread.Sleep(10);
                    waitTimeSoFar += 10;
                    _blockHeader = BlockTree.BestSuggestedHeader;
                }

                Assert.AreEqual(number, _blockHeader?.Number, "block number");
                return this;
            }

            public SyncingContext BlockIsSameAsGenesis()
            {
                Assert.AreSame(BlockTree.Genesis, _blockHeader, "genesis");
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
                if (_logger.IsInfo) _logger.Info($"WAIT {milliseconds}");
                Thread.Sleep(milliseconds);
                return this;
            }

            public SyncingContext Wait()
            {
                return Wait(WaitTime);
            }

            public SyncingContext WaitUntilInitialized()
            {
                SpinWait.SpinUntil(() => SyncPeerPool.AllPeers.All(p => p.IsInitialized), dynamicTimeout);
                return this;
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
                    _blockHeader = BlockTree.BestSuggestedHeader;
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

            public SyncingContext AfterPeerIsAdded(ISyncPeer syncPeer)
            {
                ((SyncPeerMock) syncPeer).Disconnected += (s, e) => SyncPeerPool.RemovePeer(syncPeer);

                _logger.Info($"PEER ADDED {syncPeer.ClientId}");
                _peers.TryAdd(syncPeer.ClientId, syncPeer);
                SyncPeerPool.AddPeer(syncPeer);
                return this;
            }

            public SyncingContext AfterPeerIsRemoved(ISyncPeer syncPeer)
            {
                _peers.Remove(syncPeer.ClientId);
                SyncPeerPool.RemovePeer(syncPeer);
                return this;
            }

            public SyncingContext AfterNewBlockMessage(Block block, ISyncPeer peer)
            {
                _logger.Info($"NEW BLOCK MESSAGE {block.Number}");
                block.Header.TotalDifficulty = block.Difficulty * (ulong)(block.Number + 1);
                SyncServer.AddNewBlock(block, peer);
                return this;
            }

            public SyncingContext AfterHintBlockMessage(Block block, ISyncPeer peer)
            {
                _logger.Info($"HINT BLOCK MESSAGE {block.Number}");
                SyncServer.HintBlock(block.Hash, block.Number, peer);
                return this;
            }

            public SyncingContext PeerCountIs(long i)
            {
                Assert.AreEqual(i, SyncPeerPool.AllPeers.Count(), "peer count");
                return this;
            }

            public SyncingContext WaitAMoment()
            {
                return Wait(Moment);
            }

            public void Stop()
            {
                Synchronizer.SyncEvent -= SynchronizerOnSyncEvent;
                Task task = new Task(async () =>
                {
                    await Synchronizer.StopAsync();
                    await SyncPeerPool.StopAsync();
                });

                task.RunSynchronously();
            }
        }

        [OneTimeSetUp]
        public void Setup()
        {
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            foreach (SyncingContext syncingContext in SyncingContext.AllInstances)
            {
                syncingContext.Stop();
            }
        }

        [Test, Retry(3)]
        public void Init_condition_are_as_expected()
        {
            When.Syncing
                .AfterProcessingGenesis()
                .BestKnownNumberIs(0)
                .Genesis.BlockIsKnown()
                .BestSuggested.BlockIsSameAsGenesis().Stop();
        }

        [Test, Retry(3)]
        public void Can_sync_with_one_peer_straight()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggested.BlockIsSameAsGenesis().Stop();
        }

        [Test, Retry(3)]
        public void Can_sync_with_one_peer_straight_and_extend_chain()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(3);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader).Stop();
        }
        
        [Test, Retry(3)]
        public void Can_extend_chain_by_one_on_new_block_message()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .WaitUntilInitialized()
                .After(() => peerA.AddBlocksUpTo(2))
                .AfterNewBlockMessage(peerA.HeadBlock, peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader).Stop();
        }

        [Test, Retry(3)]
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
                .WaitUntilInitialized()
                .After(() => peerB.AddBlocksUpTo(6))
                .AfterNewBlockMessage(peerB.HeadBlock, peerB)
                .BestSuggestedHeaderIs(peerB.HeadHeader).Stop();
        }

        [Test, Retry(3)]
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
                .BestSuggestedHeaderIs(peerB.HeadHeader).Stop();
        }

        [Test, Retry(3)]
        public void Can_extend_chain_by_one_on_block_hint_message()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .WaitUntilInitialized()
                .After(() => peerA.AddBlocksUpTo(2))
                .AfterHintBlockMessage(peerA.HeadBlock, peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader).Stop();
        }

        [Test, Retry(3)]
        public void Can_extend_chain_by_more_than_one_on_new_block_message()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .WaitUntilInitialized()
                .After(() => peerA.AddBlocksUpTo(8))
                .AfterNewBlockMessage(peerA.HeadBlock, peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader).Wait().Stop();

            Console.WriteLine("why?");
        }

        [Test, Retry(3)]
        public void Will_ignore_new_block_that_is_far_ahead()
        {
            // this test was designed for no sync-timer sync process
            // now it checks something different
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .WaitUntilInitialized()
                .After(() => peerA.AddBlocksUpTo(16))
                .AfterNewBlockMessage(peerA.HeadBlock, peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader).Stop();
        }

        [Test, Retry(3)]
        public void Can_sync_when_best_peer_is_timing_out()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            SyncPeerMock badPeer = new SyncPeerMock("B", false, false, true);
            badPeer.AddBlocksUpTo(20);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(badPeer)
                .WaitUntilInitialized()
                .AfterPeerIsAdded(peerA)
                .BestSuggestedBlockHasNumber(1).Stop();
        }

        [Test]
        [Parallelizable(ParallelScope.None)]
        public void Will_inform_connecting_peer_about_the_alternative_branch_with_same_difficulty()
        {
            if (_synchronizerType == SynchronizerType.Fast)
            {
                return;
            }

            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(2);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(2, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggestedBlockHasNumber(2)
                .AfterPeerIsAdded(peerB)
                .WaitUntilInitialized()
                .Stop();

            Assert.AreNotEqual(peerB.HeadBlock.Hash, peerA.HeadBlock.Hash);

            Block peerBNewBlock = null;
            SpinWait.SpinUntil(() =>
            {
                bool receivedBlock = peerB.ReceivedBlocks.TryPeek(out peerBNewBlock);
                return receivedBlock && peerBNewBlock.Hash == peerA.HeadBlock.Hash;
            }, WaitTime);

            if (_synchronizerType == SynchronizerType.Eth2Merge)
            {
                Assert.IsNull(peerBNewBlock);
                Assert.AreNotEqual(peerB.HeadBlock.Hash, peerA.HeadBlock.Hash);
            }
            else
            {
                Assert.AreEqual(peerBNewBlock?.Header.Hash!, peerA.HeadBlock.Hash);
            }
        }

        [Test, Retry(3)]
        public void Will_not_add_same_peer_twice()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .AfterPeerIsAdded(peerA)
                .WaitUntilInitialized()
                .PeerCountIs(1)
                .BestSuggestedBlockHasNumber(1).Stop();
        }

        [Test, Retry(3)]
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


        [Test, Retry(3)]
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

        [Test, Retry(3)]
        public void Can_reorg_on_add_peer()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(SyncBatchSize.Max);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(SyncBatchSize.Max * 2, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader)
                .AfterPeerIsAdded(peerB)
                .BestSuggestedHeaderIs(peerB.HeadHeader).Stop();
        }

        [Test, Retry(3)]
        public void Can_reorg_based_on_total_difficulty()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(10);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddHighDifficultyBlocksUpTo(6, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader)
                .AfterPeerIsAdded(peerB)
                .BestSuggestedHeaderIs(peerB.HeadHeader).Stop();
        }

        [Test, Retry(3)]
        [Ignore("Not supported for now - still analyzing this scenario")]
        public void Can_extend_chain_on_hint_block_when_high_difficulty_low_number()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(10);

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
                .BestSuggestedHeaderIs(peerB.HeadHeader).Stop();
        }

        [Test, Retry(3)]
        public void Can_extend_chain_on_new_block_when_high_difficulty_low_number()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(10);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddHighDifficultyBlocksUpTo(6, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .WaitUntilInitialized()
                .AfterPeerIsAdded(peerB)
                .WaitUntilInitialized()
                .After(() => peerB.AddHighDifficultyBlocksUpTo(6, 0, 1))
                .AfterNewBlockMessage(peerB.HeadBlock, peerB)
                .WaitUntilInitialized()
                .BestSuggestedHeaderIs(peerB.HeadHeader).Stop();
        }

        [Test, Retry(3)]
        public void Will_not_reorganize_on_same_chain_length()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(10);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(10, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader)
                .AfterPeerIsAdded(peerB)
                .BestSuggestedHeaderIs(peerA.HeadHeader).Stop();
        }

        [Test, Retry(3)]
        public void Will_not_reorganize_more_than_max_reorg_length()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(BlockDownloader.MaxReorganizationLength + 1);

            SyncPeerMock peerB = new SyncPeerMock("B");
            peerB.AddBlocksUpTo(BlockDownloader.MaxReorganizationLength + 2, 0, 1);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader)
                .AfterPeerIsAdded(peerB)
                .BestSuggestedHeaderIs(peerA.HeadHeader).Stop();
        }

        [Test, Ignore("travis")]
        public void Can_sync_more_than_a_batch()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(SyncBatchSize.Max * 3);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader).Stop();
        }

        [Test, Retry(3)]
        public void Can_sync_exactly_one_batch()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(SyncBatchSize.Max);

            When.Syncing
                .AfterProcessingGenesis()
                .AfterPeerIsAdded(peerA)
                .BestSuggestedHeaderIs(peerA.HeadHeader)
                .Stop();
        }

        [Test, Retry(3)]
        public void Can_stop()
        {
            SyncPeerMock peerA = new SyncPeerMock("A");
            peerA.AddBlocksUpTo(SyncBatchSize.Max);

            When.Syncing
                .Stop();
        }

        private const int Moment = 50;
        private const int WaitTime = 500;
    }
}
