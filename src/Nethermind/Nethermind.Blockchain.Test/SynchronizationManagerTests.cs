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
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class SynchronizationManagerTests
    {
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

            _manager = new SynchronizationManager(_stateDb, _blockTree, blockValidator, headerValidator, transactionValidator, NullLogManager.Instance, quickConfig, new PerfService(NullLogManager.Instance), _receiptStorage);
        }

        private IDb _stateDb;
        private IDb _receiptsDb;
        private IBlockTree _blockTree;
        private IBlockTree _remoteBlockTree;
        private IReceiptStorage _receiptStorage;
        private Block _genesisBlock;
        private SynchronizationManager _manager;

        [Test]
        public async Task Retrieves_missing_blocks_in_batches()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SynchronizationManager.MaxBatchSize * 2).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            _manager.Start();
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);
            
            resetEvent.WaitOne(2000);
            Assert.AreEqual(SynchronizationManager.MaxBatchSize * 2 - 1, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public async Task Syncs_with_empty_peer()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(0).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);
            
            _manager.Start();
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);
            
            Assert.AreEqual(0, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public async Task Syncs_when_knows_more_blocks()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SynchronizationManager.MaxBatchSize * 2).TestObject;
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(0).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) => { resetEvent.Set(); };
            _manager.Start();
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);
            
            resetEvent.WaitOne(TimeSpan.FromMilliseconds(2000));
            Assert.AreEqual(SynchronizationManager.MaxBatchSize * 2 - 1, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public async Task Can_resync_if_missed_a_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SynchronizationManager.MaxBatchSize).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);

            SemaphoreSlim semaphore = new SemaphoreSlim(0);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) semaphore.Release(1);
            };
            _manager.Start();
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);

            BlockTreeBuilder.ExtendTree(_remoteBlockTree, SynchronizationManager.MaxBatchSize * 2);
            _manager.AddNewBlock(_remoteBlockTree.RetrieveHeadBlock(), peer.NodeId);
            
            semaphore.Wait(TimeSpan.FromMilliseconds(5000));
            semaphore.Wait(TimeSpan.FromMilliseconds(5000));

            Assert.AreEqual(SynchronizationManager.MaxBatchSize * 2 - 1, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public async Task Can_add_new_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SynchronizationManager.MaxBatchSize).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            
            _manager.Start();
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);

            Block block = Build.A.Block.WithParent(_remoteBlockTree.Head).TestObject;
            _manager.AddNewBlock(block, peer.NodeId);

            resetEvent.WaitOne(TimeSpan.FromMilliseconds(2000));

            Assert.AreEqual(SynchronizationManager.MaxBatchSize - 1, (int) _blockTree.BestSuggested.Number);
        }

        [Test]
        public async Task Can_sync_on_split_of_length_1()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISynchronizationPeer miner1 = new SynchronizationPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            
            _manager.Start();

            Task addMiner1Task = _manager.AddPeer(miner1);

            await Task.WhenAll(addMiner1Task);

            resetEvent.WaitOne(TimeSpan.FromSeconds(1));

            Assert.AreEqual(miner1Tree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner before split");

            Block splitBlock = Build.A.Block.WithParent(miner1Tree.FindParent(miner1Tree.Head)).WithDifficulty(miner1Tree.Head.Difficulty - 1).TestObject;
            Block splitBlockChild = Build.A.Block.WithParent(splitBlock).TestObject;

            miner1Tree.SuggestBlock(splitBlock);
            miner1Tree.MarkAsProcessed(splitBlock.Hash);
            miner1Tree.MoveToMain(splitBlock.Hash);
            miner1Tree.SuggestBlock(splitBlockChild);
            miner1Tree.MarkAsProcessed(splitBlockChild.Hash);
            miner1Tree.MoveToMain(splitBlockChild.Hash);

            Assert.AreEqual(splitBlockChild.Hash, miner1Tree.BestSuggested.Hash, "split as expected");

            resetEvent.Reset();

            _manager.AddNewBlock(splitBlockChild, miner1.NodeId);

            resetEvent.WaitOne(TimeSpan.FromSeconds(1));

            Assert.AreEqual(miner1Tree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner after split");
        }

        [Test]
        public async Task Can_sync_on_split_of_length_6()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISynchronizationPeer miner1 = new SynchronizationPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.SyncEvent += (sender, args) =>
            {
                if(args.SyncStatus == SyncStatus.Completed || args.SyncStatus == SyncStatus.Failed) resetEvent.Set();
            };
            
            _manager.Start();

            Task addMiner1Task = _manager.AddPeer(miner1);

            await Task.WhenAll(addMiner1Task);

            resetEvent.WaitOne(TimeSpan.FromSeconds(1));

            Assert.AreEqual(miner1Tree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner before split");

            miner1Tree.AddBranch(7, 0, 1);

            Assert.AreNotEqual(miner1Tree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client does not agree with miner after split");

            resetEvent.Reset();

            _manager.AddNewBlock(miner1Tree.RetrieveHeadBlock(), miner1.NodeId);

            resetEvent.WaitOne(TimeSpan.FromSeconds(1));

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
            await _manager.AddPeer(miner1);
            resetEvent.WaitOne(2000);

            Assert.AreEqual(minerTree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.MarkAsProcessed(newBlock.Hash);
            minerTree.MoveToMain(newBlock.Hash);

            ISynchronizationPeer miner2 = Substitute.For<ISynchronizationPeer>();
            miner2.GetHeadBlockNumber(Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockNumber(CancellationToken.None));
            miner2.GetHeadBlockHash(Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHash(CancellationToken.None));
            miner2.NodeId.Returns(new NodeId(TestObject.PublicKeyB));

            Assert.AreEqual(newBlock.Number, await miner2.GetHeadBlockNumber(Arg.Any<CancellationToken>()), "number as expected");
            Assert.AreEqual(newBlock.Hash, await miner2.GetHeadBlockHash(default(CancellationToken)), "hash as expected");

            await _manager.AddPeer(miner2);
            resetEvent.WaitOne(2000);

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

            Task addMiner1Task = _manager.AddPeer(miner1);
            await Task.WhenAll(addMiner1Task);
            resetEvent.WaitOne(2000);

            Assert.AreEqual(minerTree.BestSuggested.Hash, _blockTree.BestSuggested.Hash, "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.MarkAsProcessed(newBlock.Hash);
            minerTree.MoveToMain(newBlock.Hash);

            ISynchronizationPeer miner2 = Substitute.For<ISynchronizationPeer>();
            miner2.GetHeadBlockNumber(Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockNumber(CancellationToken.None));
            miner2.GetHeadBlockHash(Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHash(CancellationToken.None));
            miner2.NodeId.Returns(new NodeId(TestObject.PublicKeyB));

            Assert.AreEqual(newBlock.Number, await miner2.GetHeadBlockNumber(Arg.Any<CancellationToken>()), "number as expected");
            Assert.AreEqual(newBlock.Hash, await miner2.GetHeadBlockHash(default(CancellationToken)), "hash as expected");

            await _manager.AddPeer(miner2);
            resetEvent.WaitOne(2000);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default(CancellationToken));
        }

        [Test]
        public void Can_retrieve_node_values()
        {
            _stateDb.Set(TestObject.KeccakA, TestObject.RandomDataA);
            byte[][] values = _manager.GetNodeData(new[] {TestObject.KeccakA, TestObject.KeccakB});
            Assert.AreEqual(2, values.Length, "data.Length");
            Assert.AreEqual(TestObject.RandomDataA, values[0], "data[0]");
            Assert.AreEqual(null, values[1], "data[1]");
        }

        [Test]
        public void Can_retrieve_empty_receipts()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(2).TestObject;
            Block block0 = _blockTree.FindBlock(0);
            Block block1 = _blockTree.FindBlock(1);

            TransactionReceipt[][] receipts = _manager.GetReceipts(new[] {block0.Hash, block1.Hash, TestObject.KeccakA});

            Assert.AreEqual(3, receipts.Length, "data.Length");
            Assert.AreEqual(0, receipts[0].Length, "data[0]");
            Assert.AreEqual(0, receipts[1].Length, "data[1]");
            Assert.AreEqual(0, receipts[2].Length, "data[2]");
        }
    }
}