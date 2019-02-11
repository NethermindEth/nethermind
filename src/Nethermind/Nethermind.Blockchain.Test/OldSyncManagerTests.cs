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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class SynchronizationManagerTests
    {
        private readonly TimeSpan _standardTimeoutUnit = TimeSpan.FromMilliseconds(1000);
        
        [SetUp]
        public void Setup()
        {
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            _stateDb = new MemDb();
            _receiptsDb = new MemDb();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            BlockchainConfig quickConfig = new BlockchainConfig();
            quickConfig.SyncTimerInterval = 100;

            IHeaderValidator headerValidator = Build.A.HeaderValidator.ThatAlwaysReturnsTrue.TestObject;
            IBlockValidator blockValidator = Build.A.BlockValidator.ThatAlwaysReturnsTrue.TestObject;
            ITransactionValidator transactionValidator = Build.A.TransactionValidator.ThatAlwaysReturnsTrue.TestObject;

            _manager = new QueueBasedSyncManager(_stateDb, _blockTree, blockValidator, headerValidator, transactionValidator, NullLogManager.Instance, quickConfig, new NodeStatsManager(new StatsConfig(), LimboLogs.Instance),  new PerfService(NullLogManager.Instance), _receiptStorage);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _manager.StopAsync();
        }

        private IDb _stateDb;
        private IDb _receiptsDb;
        private IBlockTree _blockTree;
        private IBlockTree _remoteBlockTree;
        private IReceiptStorage _receiptStorage;
        private Block _genesisBlock;
        private ISynchronizationManager _manager;

        [Test]
        public void Retrieves_missing_blocks_in_batches()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(QueueBasedSyncManager.MaxBatchSize * 2).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            _manager.Start();
            _manager.AddPeer(peer);
            
            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.AreEqual(QueueBasedSyncManager.MaxBatchSize * 2 - 1, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public void Syncs_with_empty_peer()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);
            
            _manager.Start();
            _manager.AddPeer(peer);
            
            Assert.AreEqual(0, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public void Syncs_when_knows_more_blocks()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(QueueBasedSyncManager.MaxBatchSize * 2).TestObject;
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) => { resetEvent.Set(); };
            _manager.Start();
            _manager.AddPeer(peer);
            
            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.AreEqual(QueueBasedSyncManager.MaxBatchSize * 2 - 1, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public void Can_resync_if_missed_a_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(QueueBasedSyncManager.MaxBatchSize).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);

            SemaphoreSlim semaphore = new SemaphoreSlim(0);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) semaphore.Release(1);
            };
            _manager.Start();
            _manager.AddPeer(peer);

            BlockTreeBuilder.ExtendTree(_remoteBlockTree, QueueBasedSyncManager.MaxBatchSize * 2);
            _manager.AddNewBlock(_remoteBlockTree.RetrieveHeadBlock(), peer.Node.Id);
            
            semaphore.Wait(_standardTimeoutUnit);
            semaphore.Wait(_standardTimeoutUnit);

            Assert.AreEqual(QueueBasedSyncManager.MaxBatchSize * 2 - 1, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public void Can_add_new_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(QueueBasedSyncManager.MaxBatchSize).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            
            _manager.Start();
            _manager.AddPeer(peer);

            Block block = Build.A.Block.WithParent(_remoteBlockTree.Head).TestObject;
            _manager.AddNewBlock(block, peer.Node.Id);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(QueueBasedSyncManager.MaxBatchSize - 1, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public void Can_sync_on_split_of_length_1()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISynchronizationPeer miner1 = new SynchronizationPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            
            _manager.Start();

            _manager.AddPeer(miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner before split");

            Block splitBlock = Build.A.Block.WithParent(miner1Tree.FindParent(miner1Tree.Head)).WithDifficulty(miner1Tree.Head.Difficulty - 1).TestObject;
            Block splitBlockChild = Build.A.Block.WithParent(splitBlock).TestObject;

            miner1Tree.SuggestBlock(splitBlock);
            miner1Tree.UpdateMainChain(splitBlock);
            miner1Tree.SuggestBlock(splitBlockChild);
            miner1Tree.UpdateMainChain(splitBlockChild);

            Assert.AreEqual(splitBlockChild.Hash, miner1Tree.BestSuggested.Hash, "split as expected");

            resetEvent.Reset();

            _manager.AddNewBlock(splitBlockChild, miner1.Node.Id);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner after split");
        }

        [Test]
        public void  Can_sync_on_split_of_length_6()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISynchronizationPeer miner1 = new SynchronizationPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            
            _manager.Start();
            _manager.AddPeer(miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner before split");

            miner1Tree.AddBranch(7, 0, 1);

            Assert.AreNotEqual(miner1Tree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client does not agree with miner after split");

            resetEvent.Reset();

            _manager.AddNewBlock(miner1Tree.RetrieveHeadBlock(), miner1.Node.Id);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner after split");
        }

        [Test]
        [Ignore("Review sync manager tests")]
        public async Task Does_not_do_full_sync_when_not_needed()
        {
            BlockTree minerTree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISynchronizationPeer miner1 = new SynchronizationPeerMock(minerTree);

            AutoResetEvent resetEvent = new AutoResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            
            _manager.Start();
            _manager.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(minerTree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISynchronizationPeer miner2 = Substitute.For<ISynchronizationPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.AreEqual(newBlock.Number, await miner2.GetHeadBlockHeader(Arg.Any<CancellationToken>()), "number as expected");

            _manager.AddPeer(miner2);
            resetEvent.WaitOne(_standardTimeoutUnit);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default(CancellationToken));
        }

        [Test]
        [Ignore("Review sync manager tests")]
        public async Task Does_not_do_full_sync_when_not_needed_with_split()
        {
            BlockTree minerTree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISynchronizationPeer miner1 = new SynchronizationPeerMock(minerTree);

            AutoResetEvent resetEvent = new AutoResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            
            _manager.Start();
            _manager.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(minerTree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISynchronizationPeer miner2 = Substitute.For<ISynchronizationPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.AreEqual(newBlock.Number, await miner2.GetHeadBlockHeader(Arg.Any<CancellationToken>()), "number as expected");

            _manager.AddPeer(miner2);
            resetEvent.WaitOne(_standardTimeoutUnit);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default(CancellationToken));
        }

        [Test]
        public void Can_retrieve_node_values()
        {
            _stateDb.Set(TestItem.KeccakA, TestItem.RandomDataA);
            byte[][] values = _manager.GetNodeData(new[] {TestItem.KeccakA, TestItem.KeccakB});
            Assert.AreEqual(2, values.Length, "data.Length");
            Assert.AreEqual(TestItem.RandomDataA, values[0], "data[0]");
            Assert.AreEqual(null, values[1], "data[1]");
        }

        [Test]
        public void Can_retrieve_empty_receipts()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(2).TestObject;
            Block block0 = _blockTree.FindBlock(0);
            Block block1 = _blockTree.FindBlock(1);

            TransactionReceipt[][] transactionReceipts = _manager.GetReceipts(new[] {block0.Hash, block1.Hash, TestItem.KeccakA});

            Assert.AreEqual(3, transactionReceipts.Length, "data.Length");
            Assert.AreEqual(0, transactionReceipts[0].Length, "data[0]");
            Assert.AreEqual(0, transactionReceipts[1].Length, "data[1]");
            Assert.AreEqual(0, transactionReceipts[2].Length, "data[2]");
        }
    }
}