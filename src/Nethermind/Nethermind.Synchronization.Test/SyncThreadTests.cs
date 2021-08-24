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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Spec;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.All)]
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
        [Retry(3)] // experiencing some flakiness
        public void Setup_is_correct()
        {
            foreach (SyncTestContext peer in _peers)
            {
                Assert.AreEqual(_genesis.Header.Hash, peer.SyncServer.Head?.Hash);
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
                    localPeer.PeerPool.AddPeer(new SyncPeerMock(remotePeer.Tree, TestItem.PublicKeys[localIndex],
                        $"PEER{localIndex}", remotePeer.SyncServer, TestItem.PublicKeys[remoteIndex],
                        $"PEER{remoteIndex}"));
                }
            }
        }

        private const int WaitTime = 1000;

        [Test, Ignore("travis failures")]
        public void Can_sync_when_connected()
        {
            ConnectAllPeers();

            Block headBlock = ProduceBlocks(_chainLength);

            SemaphoreSlim waitEvent = new(0);
            foreach (SyncTestContext peer in _peers)
            {
                peer.Tree.NewHeadBlock += (_, e) =>
                {
                    if (e.Block.Number == _chainLength) waitEvent.Release();
                };
            }

            for (int i = 0; i < _peers.Count; i++)
            {
                waitEvent.Wait(WaitTime);
            }

            for (int i = 0; i < _peers.Count; i++)
            {
                Assert.AreEqual(headBlock.Header.Number, _peers[i].SyncServer.Head!.Number, i.ToString());
                Assert.AreEqual(_originPeer.StateProvider.GetBalance(headBlock.Beneficiary),
                    _peers[i].StateProvider.GetBalance(headBlock.Beneficiary), i + " balance");
                Assert.AreEqual(_originPeer.StateProvider.GetBalance(TestItem.AddressB),
                    _peers[i].StateProvider.GetBalance(TestItem.AddressB), i + " balance B");
            }
        }

        private Block ProduceBlocks(int chainLength)
        {
            Block headBlock = _genesis;
            AutoResetEvent resetEvent = new(false);
            _originPeer.Tree.NewHeadBlock += (_, e) =>
            {
                resetEvent.Set();
                headBlock = e.Block;
            };

            for (int i = 0; i < chainLength; i++)
            {
                Transaction transaction = new();

                1.Ether().Divide((UInt256)_chainLength, out UInt256 txValue);
                transaction.Value = txValue;
                transaction.SenderAddress = TestItem.AddressA;
                transaction.To = TestItem.AddressB;
                transaction.Nonce = (UInt256)i;
                transaction.GasLimit = 21000;
                transaction.GasPrice = 20.GWei();
                transaction.Hash = transaction.CalculateHash();
                _originPeer.Ecdsa.Sign(TestItem.PrivateKeyA, transaction);
                _originPeer.TxPool.SubmitTx(transaction, TxHandlingOptions.None);
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
            foreach (SyncTestContext peer in _peers)
            {
                Assert.AreEqual(_genesis.Hash, peer.SyncServer.Head!.Hash, "genesis hash");
            }

            Block headBlock = ProduceBlocks(_chainLength);

            SemaphoreSlim waitEvent = new(0);
            foreach (SyncTestContext peer in _peers)
            {
                peer.Tree.NewHeadBlock += (_, e) =>
                {
                    if (e.Block.Number == _chainLength) waitEvent.Release();
                };
            }

            ConnectAllPeers();

            for (int i = 0; i < _peers.Count; i++)
            {
                waitEvent.Wait(WaitTime);
            }

            for (int i = 0; i < _peers.Count; i++)
            {
                Assert.AreEqual(headBlock.Header.Number, _peers[i].SyncServer.Head!.Number, i.ToString());
                Assert.AreEqual(_originPeer.StateProvider.GetBalance(headBlock.Beneficiary),
                    _peers[i].StateProvider.GetBalance(headBlock.Beneficiary), i + " balance");
                Assert.AreEqual(_originPeer.StateProvider.GetBalance(TestItem.AddressB),
                    _peers[i].StateProvider.GetBalance(TestItem.AddressB), i + " balance B");
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
            NoErrorLimboLogs logManager = NoErrorLimboLogs.Instance;
            ConsoleAsyncLogger logger = new(LogLevel.Debug, "PEER " + index + " ");
//            var logManager = new OneLoggerLogManager(logger);
            SingleReleaseSpecProvider specProvider =
                new(ConstantinopleFix.Instance, MainnetSpecProvider.Instance.ChainId);

            IDbProvider dbProvider = TestMemDbProvider.Init();
            IDb blockDb = dbProvider.BlocksDb;
            IDb headerDb = dbProvider.HeadersDb;
            IDb blockInfoDb = dbProvider.BlockInfosDb;
            IDb codeDb = dbProvider.CodeDb;
            IDb stateDb = dbProvider.StateDb;

            TrieStore trieStore = new(stateDb, LimboLogs.Instance);
            StateReader stateReader = new(trieStore, codeDb, logManager);
            StateProvider stateProvider = new(trieStore, codeDb, logManager);
            stateProvider.CreateAccount(TestItem.AddressA, 10000.Ether());
            stateProvider.Commit(specProvider.GenesisSpec);
            stateProvider.CommitTree(0);
            stateProvider.RecalculateStateRoot();

            StorageProvider storageProvider = new(trieStore, stateProvider, logManager);
            InMemoryReceiptStorage receiptStorage = new();

            EthereumEcdsa ecdsa = new(specProvider.ChainId, logManager);
            BlockTree tree = new(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb),
                specProvider, NullBloomStorage.Instance, logManager);
            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, tree);

            TxPool.TxPool txPool = new(ecdsa, new ChainHeadInfoProvider(specProvider, tree, stateReader), 
                new TxPoolConfig(), new TxValidator(specProvider.ChainId), logManager, transactionComparerProvider.GetDefaultComparer());
            BlockhashProvider blockhashProvider = new(tree, LimboLogs.Instance);
            VirtualMachine virtualMachine =
                new(stateProvider, storageProvider, blockhashProvider, specProvider, logManager);

            Always sealValidator = Always.Valid;
            HeaderValidator headerValidator = new(tree, sealValidator, specProvider, logManager);
            Always txValidator = Always.Valid;
            OmmersValidator ommersValidator = new(tree, headerValidator, logManager);
            BlockValidator blockValidator =
                new(txValidator, headerValidator, ommersValidator, specProvider, logManager);

            ISyncConfig syncConfig = _synchronizerType == SynchronizerType.Fast
                ? SyncConfig.WithFastSync
                : SyncConfig.WithFullSyncOnly;

            RewardCalculator rewardCalculator = new(specProvider);
            TransactionProcessor txProcessor =
                new(specProvider, stateProvider, storageProvider, virtualMachine, logManager);

            BlockProcessor blockProcessor = new(
                specProvider,
                blockValidator,
                rewardCalculator,
                new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, stateProvider),
                stateProvider,
                storageProvider,
                receiptStorage,
                NullWitnessCollector.Instance,
                logManager);

            RecoverSignatures step = new(ecdsa, txPool, specProvider, logManager);
            BlockchainProcessor processor = new(tree, blockProcessor, step, logManager,
                BlockchainProcessor.Options.Default);

            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            NodeStatsManager nodeStatsManager = new(timerFactory, logManager);
            SyncPeerPool syncPeerPool = new(tree, nodeStatsManager, 25, logManager);

            StateProvider devState = new(trieStore, codeDb, logManager);
            StorageProvider devStorage = new(trieStore, devState, logManager);
            VirtualMachine devEvm = new(devState, devStorage, blockhashProvider, specProvider, logManager);
            TransactionProcessor devTxProcessor = new(specProvider, devState, devStorage, devEvm, logManager);

            BlockProcessor devBlockProcessor = new(
                specProvider,
                blockValidator,
                rewardCalculator,
                new BlockProcessor.BlockProductionTransactionsExecutor(devTxProcessor, devState, devStorage, specProvider, logManager),
                devState,
                devStorage,
                receiptStorage,
                NullWitnessCollector.Instance,
                logManager);

            BlockchainProcessor devChainProcessor = new(tree, devBlockProcessor, step, logManager,
                BlockchainProcessor.Options.NoReceipts);
            ITxFilterPipeline txFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(LimboLogs.Instance, specProvider);
            TxPoolTxSource transactionSelector = new(txPool, specProvider, transactionComparerProvider, logManager, txFilterPipeline);
            DevBlockProducer producer = new(
                transactionSelector,
                devChainProcessor,
                stateProvider, 
                tree,
                new BuildBlocksRegularly(TimeSpan.FromMilliseconds(50)).IfPoolIsNotEmpty(txPool),
                Timestamper.Default,
                specProvider,
                new MiningConfig(),
                logManager);

            SyncProgressResolver resolver = new(
                tree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, logManager);
            MultiSyncModeSelector selector = new(resolver, syncPeerPool, syncConfig, logManager);
            Synchronizer synchronizer = new(
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
            SyncServer syncServer = new(
                stateDb,
                codeDb,
                tree,
                receiptStorage,
                Always.Valid,
                Always.Valid,
                syncPeerPool,
                selector,
                syncConfig,
                NullWitnessCollector.Instance,
                logManager);

            ManualResetEventSlim waitEvent = new();
            tree.NewHeadBlock += (_, _) => waitEvent.Set();

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

            SyncTestContext context = new();
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
