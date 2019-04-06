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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Stats;
using Nethermind.Store;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture(SynchronizerType.Fast)]
    [TestFixture(SynchronizerType.Full)]
    public class SyncThreadsTests
    {
        private readonly SynchronizerType _synchronizerType;
        private List<(ISyncServer SyncManager, IEthSyncPeerPool PeerPool, IBlockchainProcessor BlockchainProcessor, ISynchronizer Synchronizer, IBlockTree Tree)> _peers;
        private (ISyncServer SyncServer, IEthSyncPeerPool PeerPool, IBlockchainProcessor BlockchainProcessor, ISynchronizer Synchronizer, IBlockTree Tree) _localPeer;
        private (ISyncServer SyncServer, IEthSyncPeerPool PeerPool, IBlockchainProcessor BlockchainProcessor, ISynchronizer Synchronizer, IBlockTree Tree) _remotePeer1;
        private (ISyncServer SyncServer, IEthSyncPeerPool PeerPool, IBlockchainProcessor BlockchainProcessor, ISynchronizer Synchronizer, IBlockTree Tree) _remotePeer2;
        private (ISyncServer SyncServer, IEthSyncPeerPool PeerPool, IBlockchainProcessor BlockchainProcessor, ISynchronizer Synchronizer, IBlockTree Tree) _remotePeer3;
        private static Block _genesis = Build.A.Block.Genesis.TestObject;

        public SyncThreadsTests(SynchronizerType synchronizerType)
        {
            _synchronizerType = synchronizerType;
        }
        
        [SetUp]
        public void Setup()
        {
            _peers = new List<(ISyncServer, IEthSyncPeerPool, IBlockchainProcessor, ISynchronizer, IBlockTree)>();
            for (int i = 0; i < 4; i++)
            {
                _peers.Add(CreateSyncManager($"PEER_{i}."));    
            }
            
            _localPeer = _peers[0];
            _remotePeer1 = _peers[1];
            _remotePeer2 = _peers[2];
            _remotePeer3 = _peers[3];
        }
        
        [TearDown]
        public async Task TearDown()
        {
            foreach ((ISyncServer SyncManager, IEthSyncPeerPool PeerPool, IBlockchainProcessor BlockchainProcessor, ISynchronizer Synchronizer, IBlockTree Tree) peer in _peers)
            {
                await peer.PeerPool.StopAsync();
                await peer.BlockchainProcessor.StopAsync();
                await peer.Synchronizer.StopAsync();
            }
        }

        [Test]
        public void Setup_is_correct()
        {
            foreach ((ISyncServer SyncManager, IEthSyncPeerPool EthSyncPeerPool, IBlockchainProcessor, ISynchronizer Synchronizer, IBlockTree Tree) peer in _peers)
            {
                Assert.AreEqual(_genesis.Header, peer.SyncManager.Head);    
            }
        }

        private void ConnectAllPeers()
        {
            for (int localIndex = 0; localIndex < _peers.Count; localIndex++)
            {
                (ISyncServer SyncManager, IEthSyncPeerPool PeerPool, IBlockchainProcessor, ISynchronizer Synchronizer, IBlockTree Tree) localPeer = _peers[localIndex];
                for (int remoteIndex = 0; remoteIndex < _peers.Count; remoteIndex++)
                {
                    if (localIndex == remoteIndex)
                    {
                        continue;
                    }
                    
                    (ISyncServer SyncManager, IEthSyncPeerPool PeerPool, IBlockchainProcessor, ISynchronizer Synchronizer, IBlockTree Tree) remotePeer = _peers[remoteIndex];
                    localPeer.PeerPool.AddPeer(new SyncPeerMock(remotePeer.Tree, TestItem.PublicKeys[localIndex], $"PEER{localIndex}", remotePeer.SyncManager, TestItem.PublicKeys[remoteIndex], $"PEER{remoteIndex}"));
                }
            }
        }

        [Test]
        public void Can_sync_when_connected()
        {   
            ConnectAllPeers();
            int chainLength = 10000;

            var headBlock = _genesis;
            for (int i = 0; i < chainLength; i++)
            {
                var block = Build.A.Block.WithParent(headBlock).WithTotalDifficulty((headBlock.TotalDifficulty ?? 0) + 1).TestObject;
                headBlock = block;
                _remotePeer1.Tree.SuggestBlock(block);
            }
            
            SemaphoreSlim waitEvent = new SemaphoreSlim(0);
            _localPeer.Tree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == chainLength) waitEvent.Release();
            };
            
            _remotePeer1.Tree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == chainLength) waitEvent.Release();
            };
            
            _remotePeer2.Tree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == chainLength) waitEvent.Release();
            };
            
            _remotePeer3.Tree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == chainLength) waitEvent.Release();
            };
            
            waitEvent.Wait(10000);
            waitEvent.Wait(10000);
            waitEvent.Wait(10000);
            waitEvent.Wait(10000);
                
            Assert.AreEqual(headBlock.Header.Hash, _localPeer.SyncServer.Head.Hash, "local");
            Assert.AreEqual(headBlock.Header.Hash, _remotePeer1.SyncServer.Head.Hash, "remote1");
            Assert.AreEqual(headBlock.Header.Hash, _remotePeer2.SyncServer.Head.Hash, "remote2");
            Assert.AreEqual(headBlock.Header.Hash, _remotePeer3.SyncServer.Head.Hash, "remote3");
        }
        
        [Test]
        public void Can_sync_when_initially_disconnected()
        {            
            int chainLength = 10000;

            var headBlock = _genesis;
            for (int i = 0; i < chainLength; i++)
            {
                var block = Build.A.Block.WithParent(headBlock).WithTotalDifficulty((headBlock.TotalDifficulty ?? 0) + 1).TestObject;
                headBlock = block;
                _remotePeer1.Tree.SuggestBlock(block);
            }
            
            SemaphoreSlim waitEvent = new SemaphoreSlim(0);
            _localPeer.Tree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == chainLength)
                {
                    Console.WriteLine($"LOCAL {e.Block.Number} {e.Block.Hash}");
                    waitEvent.Release();
                }
            };
            
            _remotePeer1.Tree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == chainLength)
                {
                    Console.WriteLine($"1 {e.Block.Number} {e.Block.Hash}");
                    waitEvent.Release();
                }
            };
            
            _remotePeer2.Tree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == chainLength)
                {
                    Console.WriteLine($"2 {e.Block.Number} {e.Block.Hash}");
                    waitEvent.Release();
                }
            };
            
            _remotePeer3.Tree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == chainLength)
                {
                    Console.WriteLine($"3 {e.Block.Number} {e.Block.Hash}");
                    waitEvent.Release();
                }
            };
            
            Assert.AreEqual(_genesis.Hash, _localPeer.SyncServer.Head.Hash, "local before");
            Assert.AreEqual(_genesis.Hash, _remotePeer2.SyncServer.Head.Hash, "peer 2 before");
            Assert.AreEqual(_genesis.Hash, _remotePeer3.SyncServer.Head.Hash, "peer 3 before");
            
            ConnectAllPeers();
            waitEvent.Wait(10000);
            waitEvent.Wait(10000);
            waitEvent.Wait(10000);
            waitEvent.Wait(10000);
            
            Assert.AreEqual(headBlock.Header.Hash, _localPeer.SyncServer.Head.Hash, "local");
            Assert.AreEqual(headBlock.Header.Hash, _remotePeer1.SyncServer.Head.Hash, "peer 1");
            Assert.AreEqual(headBlock.Header.Hash, _remotePeer2.SyncServer.Head.Hash, "peer 2");
            Assert.AreEqual(headBlock.Header.Hash, _remotePeer3.SyncServer.Head.Hash, "peer 3");
        }

        private (ISyncServer, IEthSyncPeerPool, IBlockchainProcessor, ISynchronizer, IBlockTree) CreateSyncManager(string prefix)
        {
//            var logManager = NoErrorLimboLogs.Instance;
            var logManager = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Debug, prefix));

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

            var sealValidator = TestSealValidator.AlwaysValid;
            var headerValidator = new HeaderValidator(tree, sealValidator, specProvider, logManager);
            var txValidator = TestTxValidator.AlwaysValid;
            var ommersValidator = new OmmersValidator(tree, headerValidator, logManager);
            var blockValidator = new BlockValidator(txValidator, headerValidator, ommersValidator, specProvider, logManager);
            
            var txProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, logManager);
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, NoBlockRewards.Instance, txProcessor, stateDb, codeDb, traceDb, stateProvider, storageProvider, NullTransactionPool.Instance, receiptStorage, logManager);
            var step = new TxSignaturesRecoveryStep(ecdsa, NullTransactionPool.Instance);
            var processor = new BlockchainProcessor(tree, blockProcessor, step, logManager, true, true);

            var nodeStatsManager = new NodeStatsManager(new StatsConfig(), logManager);
            var syncPeerPool = new EthSyncPeerPool(tree, nodeStatsManager, new SyncConfig(), logManager);

            ISynchronizer synchronizer;
            switch (_synchronizerType)
            {
                case SynchronizerType.Full:
                    synchronizer = new FullSynchronizer(
                        tree,
                        blockValidator,
                        sealValidator,
                        txValidator,
                        syncPeerPool, new SyncConfig(), logManager);
                    break;
                case SynchronizerType.Fast:
                    NodeDataDownloader downloader = new NodeDataDownloader(codeDb, stateDb, logManager);
                    synchronizer = new FastSynchronizer(
                        tree,
                        headerValidator,
                        sealValidator,
                        txValidator,
                        syncPeerPool, new SyncConfig(), downloader, logManager);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            
            var syncServer = new SyncServer(stateDb, tree, receiptStorage, TestSealValidator.AlwaysValid, syncPeerPool, synchronizer, logManager);

            ManualResetEventSlim waitEvent = new ManualResetEventSlim();
            tree.NewHeadBlock += (s, e) => waitEvent.Set();

            syncPeerPool.Start();
            synchronizer.Start();
            processor.Start();
            tree.SuggestBlock(_genesis);

            if (!waitEvent.Wait(1000))
            {
                throw new Exception("No genesis");
            }

            return (syncServer, syncPeerPool, processor, synchronizer, tree);
        }
    }
}