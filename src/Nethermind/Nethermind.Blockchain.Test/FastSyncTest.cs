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
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Stats;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class FastSyncTest
    {
        private List<(ISynchronizationManager SyncManager, IBlockTree Tree)> _peers;
        private (ISynchronizationManager SyncManager, IBlockTree Tree) _localPeer;
        private (ISynchronizationManager SyncManager, IBlockTree Tree) _remotePeer1;
        private (ISynchronizationManager SyncManager, IBlockTree Tree) _remotePeer2;
        private (ISynchronizationManager SyncManager, IBlockTree Tree) _remotePeer3;
        private static Block _genesis = Build.A.Block.Genesis.TestObject;

        [SetUp]
        public void Setup()
        {
            _peers = new List<(ISynchronizationManager, IBlockTree)>();
            for (int i = 0; i < 4; i++)
            {
                _peers.Add(CreateSyncManager($"PEER_{i}."));    
            }
            
            _localPeer = _peers[0];
            _remotePeer1 = _peers[1];
            _remotePeer2 = _peers[2];
            _remotePeer3 = _peers[3];
        }

        [Test]
        public void Setup_is_correct()
        {
            foreach ((ISynchronizationManager SyncManager, IBlockTree Tree) peer in _peers)
            {
                Assert.AreEqual(_genesis.Header, peer.SyncManager.Head);    
            }
        }

        private void ConnectAllPeers()
        {
            for (int localIndex = 0; localIndex < _peers.Count; localIndex++)
            {
                (ISynchronizationManager SyncManager, IBlockTree Tree) localPeer = _peers[localIndex];
                for (int remoteIndex = 0; remoteIndex < _peers.Count; remoteIndex++)
                {
                    if (localIndex == remoteIndex)
                    {
                        continue;
                    }
                    
                    (ISynchronizationManager SyncManager, IBlockTree Tree) remotePeer = _peers[remoteIndex];
                    localPeer.SyncManager.AddPeer(new SynchronizationPeerMock(remotePeer.Tree, TestItem.PublicKeys[localIndex], $"PEER{localIndex}", remotePeer.SyncManager, TestItem.PublicKeys[remoteIndex], $"PEER{remoteIndex}"));
                }
            }
        }

        [Test]
        public void Can_sync_when_connected()
        {            
            ConnectAllPeers();
            int chainLength = 5000;

            var headBlock = _genesis;
            for (int i = 0; i < chainLength; i++)
            {
                var block = Build.A.Block.WithParent(headBlock).WithTotalDifficulty((headBlock.TotalDifficulty ?? 0) + 1).TestObject;
                _remotePeer1.Tree.SuggestBlock(block);
                headBlock = block;
            }
            
            ManualResetEventSlim waitEvent = new ManualResetEventSlim();
            _localPeer.Tree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == chainLength)
                {
                    waitEvent.Set();
                }
            };
                
            waitEvent.Wait(10000);
            Assert.AreEqual(headBlock.Header, _localPeer.SyncManager.Head);
        }

        private static (ISynchronizationManager, BlockTree) CreateSyncManager(string prefix)
        {
//            var logManager = NoErrorLimboLogs.Instance;
            var logManager = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Trace, prefix));

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