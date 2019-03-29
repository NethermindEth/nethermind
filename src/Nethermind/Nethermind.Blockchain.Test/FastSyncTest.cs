using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class FastSyncTest
    {
        private List<(ISynchronizationManager SyncManager, IBlockTree Tree)> _peers; 
        private static Block _genesis = Build.A.Block.Genesis.TestObject;
        
        [SetUp]
        public void Setup()
        {
            _peers = new List<(ISynchronizationManager, IBlockTree)>();
            _peers.Add(CreateSyncManager());
            _peers.Add(CreateSyncManager());
        }
        
        [Test]
        public void Setup_is_correct()
        {
            Assert.AreEqual(_genesis.Header, _peers[0].SyncManager.Head);
            Assert.AreEqual(_genesis.Header, _peers[1].SyncManager.Head);
        }
        
        [Test]
        public void Can_sync_one_empty_block()
        {
            var node = new Node(TestItem.PublicKeyA, "127.0.0.1", 30303);
            var syncPeer = Substitute.For<ISynchronizationPeer>();
            syncPeer.Node.Returns(node);
            
            var peer = _peers[0];
            peer.SyncManager.AddPeer(syncPeer);
            
            var block1 = Build.A.Block.WithParent(_genesis).WithTotalDifficulty(_genesis.TotalDifficulty.Value + 1).TestObject;
            peer.SyncManager.AddNewBlock(block1, TestItem.PublicKeyA);
            ManualResetEventSlim waitEvent = new ManualResetEventSlim();
            peer.Tree.NewHeadBlock += (s, e) => waitEvent.Set();
            waitEvent.Wait(1000);
            Assert.AreEqual(block1.Header, _peers[0].SyncManager.Head);
        }

        private static (ISynchronizationManager, BlockTree) CreateSyncManager()
        {
            var logManager = NoErrorLimboLogs.Instance;
            
            var specProvider = GoerliSpecProvider.Instance;

            MemDb traceDb = new MemDb();
            MemDb blockDb = new MemDb();
            MemDb blockInfoDb = new MemDb();
            StateDb codeDb = new StateDb();
            StateDb stateDb = new StateDb();

            var stateProvider = new StateProvider(new StateTree(stateDb), codeDb, logManager);
            var storageProvider = new StorageProvider(stateDb, stateProvider, logManager);
            var receiptStorage = new InMemoryReceiptStorage();

            var ecdsa = new EthereumEcdsa(specProvider, logManager);
            var tree = new BlockTree(blockDb, blockInfoDb, specProvider, NullTransactionPool.Instance, logManager);
            var blockhashProvider = new BlockhashProvider(tree);
            var virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, logManager);
            var txProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, logManager);
            var blockProcessor = new BlockProcessor(specProvider, TestBlockValidator.AlwaysValid, NoBlockRewards.Instance, txProcessor, stateDb, codeDb, traceDb, stateProvider, storageProvider, NullTransactionPool.Instance, receiptStorage, logManager);
            var step = new TxSignaturesRecoveryStep(ecdsa, NullTransactionPool.Instance);
            var processor = new BlockchainProcessor(tree, blockProcessor, step, logManager, true, true);

            var nodeStatsManager = new NodeStatsManager(new StatsConfig(), logManager, true);
            var syncManager = new QueueBasedSyncManager(
                stateDb,
                tree,
                TestBlockValidator.AlwaysValid,
                TestSealValidator.AlwaysValid,
                TestTransactionValidator.AlwaysValid,
                logManager,
                new BlockchainConfig(),
                nodeStatsManager,
                new PerfService(NullLogManager.Instance), receiptStorage);
            
            ManualResetEventSlim waitEvent = new ManualResetEventSlim();
            tree.NewHeadBlock += (s, e) => waitEvent.Set();

            syncManager.Start();
            processor.Start();
            tree.SuggestBlock(_genesis);

            if (!waitEvent.Wait(1000))
            {
                throw new Exception("No genesis");
            }
            
            return (syncManager, tree);
        }
    }
}