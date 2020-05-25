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

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Db.Blooms;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.None)]
    [TestFixture(SynchronizerType.Fast)]
    [TestFixture(SynchronizerType.Full)]
    public class SyncThreadsTests
    {
        private readonly SynchronizerType _synchronizerType;
        private List<SyncTestContext> _peers;
        private SyncTestContext _originPeer;
        private static Block _genesis;

        public SyncThreadsTests(SynchronizerType synchronizerType)
        {
            _synchronizerType = synchronizerType;
        }

        private int remotePeersCount = 2;

        [SetUp]
        public void Setup()
        {
            _peers = new List<SyncTestContext>();
            for (int i = 0; i < remotePeersCount + 1; i++)
            {
                _peers.Add(CreateSyncManager(i));
            }

            _originPeer = _peers[0];
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (SyncTestContext peer in _peers)
            {
                await peer.StopAsync();
            }
        }

        [Test]
        public void Setup_is_correct()
        {
            foreach (SyncTestContext peer in _peers)
            {
                Assert.AreEqual(_genesis.Header, peer.SyncServer.Head);
            }
        }

        private void ConnectAllPeers()
        {
            for (int localIndex = 0; localIndex < _peers.Count; localIndex++)
            {
                SyncTestContext localPeer = _peers[localIndex];
                for (int remoteIndex = 0; remoteIndex < _peers.Count; remoteIndex++)
                {
                    if (localIndex == remoteIndex)
                    {
                        continue;
                    }

                    SyncTestContext remotePeer = _peers[remoteIndex];
                    localPeer.PeerPool.AddPeer(new SyncPeerMock(remotePeer.Tree, TestItem.PublicKeys[localIndex], $"PEER{localIndex}", remotePeer.SyncServer, TestItem.PublicKeys[remoteIndex], $"PEER{remoteIndex}"));
                }
            }
        }

        private const int _waitTime = 1000;

        [Test, Ignore("travis failures")]
        public void Can_sync_when_connected()
        {
            ConnectAllPeers();

            var headBlock = ProduceBlocks(_chainLength);

            SemaphoreSlim waitEvent = new SemaphoreSlim(0);
            foreach (var peer in _peers)
            {
                peer.Tree.NewHeadBlock += (s, e) =>
                {
                    if (e.Block.Number == _chainLength) waitEvent.Release();
                };
            }

            for (int i = 0; i < _peers.Count; i++)
            {
                waitEvent.Wait(_waitTime);
            }

            for (int i = 0; i < _peers.Count; i++)
            {
                Assert.AreEqual(headBlock.Header.Number, _peers[i].SyncServer.Head.Number, i.ToString());
                Assert.AreEqual(_originPeer.StateProvider.GetBalance(headBlock.Beneficiary), _peers[i].StateProvider.GetBalance(headBlock.Beneficiary), i + " balance");
                Assert.AreEqual(_originPeer.StateProvider.GetBalance(TestItem.AddressB), _peers[i].StateProvider.GetBalance(TestItem.AddressB), i + " balance B");
            }
        }

        private Block ProduceBlocks(int chainLength)
        {
            Block headBlock = _genesis;
            AutoResetEvent resetEvent = new AutoResetEvent(false);
            _originPeer.Tree.NewHeadBlock += (s, e) =>
            {
                resetEvent.Set();
                headBlock = e.Block;
            };

            for (int i = 0; i < chainLength; i++)
            {
                Transaction transaction = new Transaction();
                transaction.Value = (UInt256) BigInteger.Divide((BigInteger) 1.Ether(), _chainLength);
                transaction.SenderAddress = TestItem.AddressA;
                transaction.To = TestItem.AddressB;
                transaction.Nonce = (UInt256) i;
                transaction.GasLimit = 21000;
                transaction.GasPrice = 20.GWei();
                transaction.Hash = transaction.CalculateHash();
                _originPeer.Ecdsa.Sign(TestItem.PrivateKeyA, transaction);
                _originPeer.TxPool.AddTransaction(transaction, TxHandlingOptions.None);
                if (!resetEvent.WaitOne(1000))
                {
                    throw new Exception($"Failed to produce block {i + 1}");
                }
            }

            return headBlock;
        }

        private int _chainLength = 100;

        [Test, Ignore("Fails when running with other tests due to pool starvation in NUnit adapter")]
        public void Can_sync_when_initially_disconnected()
        {
            foreach (var peer in _peers)
            {
                Assert.AreEqual(_genesis.Hash, peer.SyncServer.Head.Hash, "genesis hash");
            }

            var headBlock = ProduceBlocks(_chainLength);

            SemaphoreSlim waitEvent = new SemaphoreSlim(0);
            foreach (var peer in _peers)
            {
                peer.Tree.NewHeadBlock += (s, e) =>
                {
                    if (e.Block.Number == _chainLength) waitEvent.Release();
                };
            }

            ConnectAllPeers();

            for (int i = 0; i < _peers.Count; i++)
            {
                waitEvent.Wait(_waitTime);
            }

            for (int i = 0; i < _peers.Count; i++)
            {
                Assert.AreEqual(headBlock.Header.Number, _peers[i].SyncServer.Head.Number, i.ToString());
                Assert.AreEqual(_originPeer.StateProvider.GetBalance(headBlock.Beneficiary), _peers[i].StateProvider.GetBalance(headBlock.Beneficiary), i + " balance");
                Assert.AreEqual(_originPeer.StateProvider.GetBalance(TestItem.AddressB), _peers[i].StateProvider.GetBalance(TestItem.AddressB), i + " balance B");
            }
        }

        private class SyncTestContext
        {
            public IEthereumEcdsa Ecdsa { get; set; }
            public ITxPool TxPool { get; set; }
            public ISyncServer SyncServer { get; set; }
            public ISyncPeerPool PeerPool { get; set; }
            public IBlockchainProcessor BlockchainProcessor { get; set; }
            public ISynchronizer Synchronizer { get; set; }
            public IBlockTree Tree { get; set; }
            public IStateProvider StateProvider { get; set; }

            public DevBlockProducer BlockProducer { get; set; }
            public ConsoleAsyncLogger Logger { get; set; }

            public async Task StopAsync()
            {
                await (BlockchainProcessor?.StopAsync() ?? Task.CompletedTask);
                await (BlockProducer?.StopAsync() ?? Task.CompletedTask);
                await (PeerPool?.StopAsync() ?? Task.CompletedTask);
                await (Synchronizer?.StopAsync() ?? Task.CompletedTask);
                Logger?.Flush();
            }
        }

        private SyncTestContext CreateSyncManager(int index)
        {
            var logManager = NoErrorLimboLogs.Instance;
            ConsoleAsyncLogger logger = new ConsoleAsyncLogger(LogLevel.Debug, "PEER " + index + " ");
//            var logManager = new OneLoggerLogManager(logger);
            var specProvider = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, MainnetSpecProvider.Instance.ChainId);

            var dbProvider = new MemDbProvider();
            IDb blockDb = dbProvider.BlocksDb;
            IDb headerDb = dbProvider.HeadersDb;
            IDb blockInfoDb = dbProvider.BlockInfosDb;
            ISnapshotableDb codeDb = dbProvider.CodeDb;
            ISnapshotableDb stateDb = dbProvider.StateDb;

            var stateReader = new StateReader(stateDb, codeDb, logManager);
            var stateProvider = new StateProvider(stateDb, codeDb, logManager);
            stateProvider.CreateAccount(TestItem.AddressA, 10000.Ether());
            stateProvider.Commit(specProvider.GenesisSpec);
            stateProvider.CommitTree();
            stateProvider.RecalculateStateRoot();
            stateDb.Commit();

            var storageProvider = new StorageProvider(stateDb, stateProvider, logManager);
            var receiptStorage = new InMemoryReceiptStorage();

            var ecdsa = new EthereumEcdsa(specProvider.ChainId, logManager);
            var txPool = new TxPool.TxPool(new InMemoryTxStorage(), Timestamper.Default, ecdsa, specProvider, new TxPoolConfig(), stateProvider, logManager);
            var tree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), specProvider, txPool, NullBloomStorage.Instance, logManager);
            var blockhashProvider = new BlockhashProvider(tree, LimboLogs.Instance);
            var virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, specProvider, logManager);

            var sealValidator = Always.Valid;
            var headerValidator = new HeaderValidator(tree, sealValidator, specProvider, logManager);
            var txValidator = Always.Valid;
            var ommersValidator = new OmmersValidator(tree, headerValidator, logManager);
            var blockValidator = new BlockValidator(txValidator, headerValidator, ommersValidator, specProvider, logManager);

            ISyncConfig syncConfig = _synchronizerType == SynchronizerType.Fast ? SyncConfig.WithFastSync : SyncConfig.WithFullSyncOnly;

            var rewardCalculator = new RewardCalculator(specProvider);
            var txProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, logManager);
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, txProcessor, stateDb, codeDb, stateProvider, storageProvider, txPool, receiptStorage, logManager);

            var step = new TxSignaturesRecoveryStep(ecdsa, txPool, logManager);
            var processor = new BlockchainProcessor(tree, blockProcessor, step, logManager, BlockchainProcessor.Options.Default);

            var nodeStatsManager = new NodeStatsManager(new StatsConfig(), logManager);
            var syncPeerPool = new SyncPeerPool(tree, nodeStatsManager, 25, logManager);

            StateProvider devState = new StateProvider(stateDb, codeDb, logManager);
            StorageProvider devStorage = new StorageProvider(stateDb, devState, logManager);
            var devEvm = new VirtualMachine(devState, devStorage, blockhashProvider, specProvider, logManager);
            var devTxProcessor = new TransactionProcessor(specProvider, devState, devStorage, devEvm, logManager);
            var devBlockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, devTxProcessor, stateDb, codeDb, devState, devStorage, txPool, receiptStorage, logManager);
            var devChainProcessor = new BlockchainProcessor(tree, devBlockProcessor, step, logManager, BlockchainProcessor.Options.NoReceipts);
            var transactionSelector = new TxPoolTxSource(txPool, stateReader, logManager);
            var producer = new DevBlockProducer(transactionSelector, devChainProcessor, stateProvider, tree, processor, txPool, Timestamper.Default, logManager);
            
            SyncProgressResolver resolver = new SyncProgressResolver(tree, receiptStorage, stateDb, new MemDb(), syncConfig, logManager);
            MultiSyncModeSelector selector = new MultiSyncModeSelector(resolver, syncPeerPool, syncConfig, logManager);
            Synchronizer synchronizer = new Synchronizer(
                dbProvider,
                MainnetSpecProvider.Instance,
                tree,
                NullReceiptStorage.Instance,
                blockValidator,
                sealValidator,
                syncPeerPool,
                nodeStatsManager,
                StaticSelector.Full,
                syncConfig,
                logManager);
            var syncServer = new SyncServer(stateDb, codeDb, tree, receiptStorage, Always.Valid, Always.Valid, syncPeerPool, selector, syncConfig, logManager);

            ManualResetEventSlim waitEvent = new ManualResetEventSlim();
            tree.NewHeadBlock += (s, e) => waitEvent.Set();

            if (index == 0)
            {
                _genesis = Build.A.Block.Genesis.WithStateRoot(stateProvider.StateRoot).TestObject;
                producer.Start();
            }

            syncPeerPool.Start();
            synchronizer.Start();
            processor.Start();
            tree.SuggestBlock(_genesis);

            if (!waitEvent.Wait(1000))
            {
                throw new Exception("No genesis");
            }

            SyncTestContext context = new SyncTestContext();
            context.Ecdsa = ecdsa;
            context.BlockchainProcessor = processor;
            context.PeerPool = syncPeerPool;
            context.StateProvider = stateProvider;
            context.Synchronizer = synchronizer;
            context.SyncServer = syncServer;
            context.Tree = tree;
            context.BlockProducer = producer;
            context.TxPool = txPool;
            context.Logger = logger;
            return context;
        }
    }
}