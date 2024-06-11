// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
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
using Nethermind.Stats;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Config;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Trie;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture(SynchronizerType.Fast)]
    [TestFixture(SynchronizerType.Full)]
    public class SyncThreadsTests
    {
        private readonly SynchronizerType _synchronizerType;
        private List<SyncTestContext> _peers = new();
        private SyncTestContext _originPeer = null!;
        private static Block _genesis = null!;

        public SyncThreadsTests(SynchronizerType synchronizerType)
        {
            _synchronizerType = synchronizerType;
        }

        private readonly int remotePeersCount = 2;

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
        [Retry(20)] // experiencing some flakiness
        public void Setup_is_correct()
        {
            foreach (SyncTestContext peer in _peers)
            {
                Assert.That(peer.SyncServer.Head?.Hash, Is.EqualTo(_genesis.Header.Hash));
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
                    localPeer.PeerPool!.AddPeer(new SyncPeerMock(remotePeer.Tree, TestItem.PublicKeys[localIndex],
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
                Address headBlockBeneficiary = headBlock.Beneficiary!;
                Assert.That(_peers[i].SyncServer.Head!.Number, Is.EqualTo(headBlock.Header.Number), i.ToString());
                Assert.That(_peers[i].StateProvider.GetBalance(headBlockBeneficiary), Is.EqualTo(_originPeer.StateProvider.GetBalance(headBlockBeneficiary)), i + " balance");
                Assert.That(_peers[i].StateProvider.GetBalance(TestItem.AddressB), Is.EqualTo(_originPeer.StateProvider.GetBalance(TestItem.AddressB)), i + " balance B");
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

        private readonly int _chainLength = 100;

        [Test, Ignore("Fails when running with other tests due to pool starvation in NUnit adapter")]
        public void Can_sync_when_initially_disconnected()
        {
            foreach (SyncTestContext peer in _peers)
            {
                Assert.That(peer.SyncServer.Head!.Hash, Is.EqualTo(_genesis.Hash), "genesis hash");
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
                Address headBlockBeneficiary = headBlock.Beneficiary!;
                Assert.That(_peers[i].SyncServer.Head!.Number, Is.EqualTo(headBlock.Header.Number), i.ToString());
                Assert.That(_peers[i].StateProvider.GetBalance(headBlockBeneficiary), Is.EqualTo(_originPeer.StateProvider.GetBalance(headBlockBeneficiary)), i + " balance");
                Assert.That(_peers[i].StateProvider.GetBalance(TestItem.AddressB), Is.EqualTo(_originPeer.StateProvider.GetBalance(TestItem.AddressB)), i + " balance B");
            }
        }

        private class SyncTestContext
        {
            public IEthereumEcdsa Ecdsa { get; set; } = null!;
            public ITxPool TxPool { get; set; } = null!;
            public ISyncServer SyncServer { get; set; } = null!;
            public ISyncPeerPool? PeerPool { get; set; }
            public IBlockchainProcessor? BlockchainProcessor { get; set; }
            public ISynchronizer? Synchronizer { get; set; }
            public IBlockTree Tree { get; set; } = null!;
            public IWorldState StateProvider { get; set; } = null!;

            public DevBlockProducer? BlockProducer { get; set; }
            public IBlockProducerRunner? BlockProducerRunner { get; set; }
            public ConsoleAsyncLogger? Logger { get; set; }

            public async Task StopAsync()
            {
                await (BlockchainProcessor?.StopAsync() ?? Task.CompletedTask);
                await (BlockProducerRunner?.StopAsync() ?? Task.CompletedTask);
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
                new(ConstantinopleFix.Instance, MainnetSpecProvider.Instance.NetworkId, MainnetSpecProvider.Instance.ChainId);

            IDbProvider dbProvider = TestMemDbProvider.Init();
            IDb codeDb = dbProvider.CodeDb;
            IDb stateDb = dbProvider.StateDb;

            TrieStore trieStore = new(stateDb, LimboLogs.Instance);
            StateReader stateReader = new(trieStore, codeDb, logManager);
            WorldState stateProvider = new(trieStore, codeDb, logManager);
            stateProvider.CreateAccount(TestItem.AddressA, 10000.Ether());
            stateProvider.Commit(specProvider.GenesisSpec);
            stateProvider.CommitTree(0);
            stateProvider.RecalculateStateRoot();

            InMemoryReceiptStorage receiptStorage = new();

            EthereumEcdsa ecdsa = new(specProvider.ChainId, logManager);
            BlockTree tree = Build.A.BlockTree().WithoutSettingHead.TestObject;
            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, tree);

            TxPool.TxPool txPool = new(ecdsa,
                new BlobTxStorage(),
                new ChainHeadInfoProvider(specProvider, tree, stateReader),
                new TxPoolConfig(),
                new TxValidator(specProvider.ChainId),
                logManager,
                transactionComparerProvider.GetDefaultComparer());
            BlockhashProvider blockhashProvider = new(tree, specProvider, stateProvider, LimboLogs.Instance);
            CodeInfoRepository codeInfoRepository = new();
            VirtualMachine virtualMachine = new(blockhashProvider, specProvider, codeInfoRepository, logManager);

            Always sealValidator = Always.Valid;
            HeaderValidator headerValidator = new(tree, sealValidator, specProvider, logManager);
            Always txValidator = Always.Valid;
            UnclesValidator unclesValidator = new(tree, headerValidator, logManager);
            BlockValidator blockValidator =
                new(txValidator, headerValidator, unclesValidator, specProvider, logManager);

            ISyncConfig syncConfig = _synchronizerType == SynchronizerType.Fast
                ? SyncConfig.WithFastSync
                : SyncConfig.WithFullSyncOnly;

            RewardCalculator rewardCalculator = new(specProvider);
            TransactionProcessor txProcessor =
                new(specProvider, stateProvider, virtualMachine, codeInfoRepository, logManager);

            BlockProcessor blockProcessor = new(
                specProvider,
                blockValidator,
                rewardCalculator,
                new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, stateProvider),
                stateProvider,
                receiptStorage,
                new BlockhashStore(tree, specProvider, stateProvider),
                logManager);

            RecoverSignatures step = new(ecdsa, txPool, specProvider, logManager);
            BlockchainProcessor processor = new(tree, blockProcessor, step, stateReader, logManager,
                BlockchainProcessor.Options.Default);

            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            NodeStatsManager nodeStatsManager = new(timerFactory, logManager);
            SyncPeerPool syncPeerPool = new(tree, nodeStatsManager, new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), logManager, 25);

            WorldState devState = new(trieStore, codeDb, logManager);
            VirtualMachine devEvm = new(blockhashProvider, specProvider, codeInfoRepository, logManager);
            TransactionProcessor devTxProcessor = new(specProvider, devState, devEvm, codeInfoRepository, logManager);

            BlockProcessor devBlockProcessor = new(
                specProvider,
                blockValidator,
                rewardCalculator,
                new BlockProcessor.BlockProductionTransactionsExecutor(devTxProcessor, devState, specProvider, logManager),
                devState,
                receiptStorage,
                new BlockhashStore(tree, specProvider, devState),
                logManager);

            BlockchainProcessor devChainProcessor = new(tree, devBlockProcessor, step, stateReader, logManager,
                BlockchainProcessor.Options.NoReceipts);
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = 0
            };
            ITxFilterPipeline txFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(LimboLogs.Instance, specProvider, blocksConfig);
            TxPoolTxSource transactionSelector = new(txPool, specProvider, transactionComparerProvider, logManager, txFilterPipeline);
            DevBlockProducer producer = new(
                transactionSelector,
                devChainProcessor,
                stateProvider,
                tree,
                Timestamper.Default,
                specProvider,
                new BlocksConfig(),
                logManager);

            StandardBlockProducerRunner runner = new(
                new BuildBlocksRegularly(TimeSpan.FromMilliseconds(50)).IfPoolIsNotEmpty(txPool),
                tree,
                producer);

            TotalDifficultyBetterPeerStrategy bestPeerStrategy = new(LimboLogs.Instance);
            Pivot pivot = new(syncConfig);
            BlockDownloaderFactory blockDownloaderFactory = new(
                MainnetSpecProvider.Instance,
                blockValidator,
                sealValidator,
                new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance),
                logManager);
            Synchronizer synchronizer = new(
                dbProvider,
                new NodeStorage(dbProvider.StateDb),
                MainnetSpecProvider.Instance,
                tree,
                NullReceiptStorage.Instance,
                syncPeerPool,
                nodeStatsManager,
                syncConfig,
                blockDownloaderFactory,
                pivot,
                Substitute.For<IProcessExitSource>(),
                bestPeerStrategy,
                new ChainSpec(),
                stateReader,
                logManager);

            ISyncModeSelector selector = synchronizer.SyncModeSelector;
            SyncServer syncServer = new(
                trieStore.TrieNodeRlpStore,
                codeDb,
                tree,
                receiptStorage,
                Always.Valid,
                Always.Valid,
                syncPeerPool,
                selector,
                syncConfig,
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                logManager);

            ManualResetEventSlim waitEvent = new();
            tree.NewHeadBlock += (_, _) => waitEvent.Set();

            if (index == 0)
            {
                _genesis = Build.A.Block.Genesis.WithStateRoot(stateProvider.StateRoot).TestObject;
                runner.Start();
            }

            syncPeerPool.Start();
            synchronizer.Start();
            processor.Start();
            tree.SuggestBlock(_genesis);

            if (!waitEvent.Wait(10000))
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
            context.BlockProducerRunner = runner;
            context.TxPool = txPool;
            context.Logger = logger;
            return context;
        }
    }
}
