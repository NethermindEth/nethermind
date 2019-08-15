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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Stats;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class OldStyleFullSynchronizerTests
    {
        private readonly TimeSpan _standardTimeoutUnit = TimeSpan.FromMilliseconds(1000);
        
        [SetUp]
        public void Setup()
        {
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            _stateDb = new StateDb();
            _codeDb = new StateDb();
            _receiptsDb = new MemDb();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            SyncConfig quickConfig = new SyncConfig();
            quickConfig.FastSync = false;

            ISealValidator sealValidator = Build.A.SealValidator.ThatAlwaysReturnsTrue.TestObject;
            IBlockValidator blockValidator = Build.A.BlockValidator.ThatAlwaysReturnsTrue.TestObject;
            ITxValidator txValidator = Build.A.TransactionValidator.ThatAlwaysReturnsTrue.TestObject;

            var stats = new NodeStatsManager(new StatsConfig(), LimboLogs.Instance);
            _pool = new EthSyncPeerPool(_blockTree, stats, quickConfig, 25, LimboLogs.Instance);
            _synchronizer = new Synchronizer(MainNetSpecProvider.Instance, _blockTree, NullReceiptStorage.Instance, blockValidator, sealValidator, _pool, quickConfig, Substitute.For<INodeDataDownloader>(), NullSyncReport.Instance, LimboLogs.Instance);
            _syncServer = new SyncServer(_stateDb, _codeDb, _blockTree, _receiptStorage, sealValidator, _pool, _synchronizer, quickConfig, LimboLogs.Instance);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _pool.StopAsync();
            await _synchronizer.StopAsync();
        }

        private ISnapshotableDb _stateDb;
        private ISnapshotableDb _codeDb;
        private IDb _receiptsDb;
        private IBlockTree _blockTree;
        private IBlockTree _remoteBlockTree;
        private IReceiptStorage _receiptStorage;
        private Block _genesisBlock;
        private IEthSyncPeerPool _pool;
        private ISyncServer _syncServer;
        private ISynchronizer _synchronizer;

        [Test, Ignore("travis")]
        public void Retrieves_missing_blocks_in_batches()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max* 2).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if(args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);
            
            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.AreEqual(SyncBatchSize.Max * 2 - 1, (int) _blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        public void Syncs_with_empty_peer()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);
            
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);
            
            Assert.AreEqual(0, (int) _blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        public void Syncs_when_knows_more_blocks()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max * 2).TestObject;
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _synchronizer.SyncEvent += (sender, args) => { resetEvent.Set(); };
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);
            
            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.AreEqual(SyncBatchSize.Max * 2 - 1, (int) _blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        [Ignore("TODO: review this test - failing only with other tests")]
        public void Can_resync_if_missed_a_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            SemaphoreSlim semaphore = new SemaphoreSlim(0);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if(args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) semaphore.Release(1);
            };
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);

            BlockTreeBuilder.ExtendTree(_remoteBlockTree, SyncBatchSize.Max * 2);
            _syncServer.AddNewBlock(_remoteBlockTree.RetrieveHeadBlock(), peer.Node);
            
            semaphore.Wait(_standardTimeoutUnit);
            semaphore.Wait(_standardTimeoutUnit);

            Assert.AreEqual(SyncBatchSize.Max * 2 - 1, (int) _blockTree.BestSuggestedHeader.Number);
        }

        [Test, Ignore("travis")]
        public void Can_add_new_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if(args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };
            
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);

            Block block = Build.A.Block.WithParent(_remoteBlockTree.Head).WithTotalDifficulty((_remoteBlockTree.Head.TotalDifficulty ?? 0) + 1).TestObject;
            _syncServer.AddNewBlock(block, peer.Node);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(SyncBatchSize.Max - 1, (int) _blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        public void Can_sync_on_split_of_length_1()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if(args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };
            
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner before split");

            Block splitBlock = Build.A.Block.WithParent(miner1Tree.FindParent(miner1Tree.Head, BlockTreeLookupOptions.TotalDifficultyNotNeeded)).WithDifficulty(miner1Tree.Head.Difficulty - 1).TestObject;
            Block splitBlockChild = Build.A.Block.WithParent(splitBlock).TestObject;

            miner1Tree.SuggestBlock(splitBlock);
            miner1Tree.UpdateMainChain(splitBlock);
            miner1Tree.SuggestBlock(splitBlockChild);
            miner1Tree.UpdateMainChain(splitBlockChild);

            Assert.AreEqual(splitBlockChild.Hash, miner1Tree.BestSuggestedHeader.Hash, "split as expected");

            resetEvent.Reset();

            _syncServer.AddNewBlock(splitBlockChild, miner1.Node);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner after split");
        }

        [Test]
        public void  Can_sync_on_split_of_length_6()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if(args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };
            
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner before split");

            miner1Tree.AddBranch(7, 0, 1);

            Assert.AreNotEqual(miner1Tree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client does not agree with miner after split");

            resetEvent.Reset();

            _syncServer.AddNewBlock(miner1Tree.RetrieveHeadBlock(), miner1.Node);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner after split");
        }

        [Test]
        [Ignore("Review sync manager tests")]
        public async Task Does_not_do_full_sync_when_not_needed()
        {
            BlockTree minerTree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(minerTree);

            AutoResetEvent resetEvent = new AutoResetEvent(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if(args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };
            
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(minerTree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISyncPeer miner2 = Substitute.For<ISyncPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<Keccak>(), Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(null, CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.AreEqual(newBlock.Number, await miner2.GetHeadBlockHeader(null, Arg.Any<CancellationToken>()), "number as expected");

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner2);
            resetEvent.WaitOne(_standardTimeoutUnit);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default(CancellationToken));
        }

        [Test]
        [Ignore("Review sync manager tests")]
        public async Task Does_not_do_full_sync_when_not_needed_with_split()
        {
            BlockTree minerTree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(minerTree);

            AutoResetEvent resetEvent = new AutoResetEvent(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if(args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };
            
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(minerTree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISyncPeer miner2 = Substitute.For<ISyncPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<Keccak>(), Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(null, CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.AreEqual(newBlock.Number, await miner2.GetHeadBlockHeader(null, Arg.Any<CancellationToken>()), "number as expected");

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner2);
            resetEvent.WaitOne(_standardTimeoutUnit);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default(CancellationToken));
        }

        [Test]
        public void Can_retrieve_node_values()
        {
            _stateDb.Set(TestItem.KeccakA, TestItem.RandomDataA);
            byte[][] values = _syncServer.GetNodeData(new[] {TestItem.KeccakA, TestItem.KeccakB});
            Assert.AreEqual(2, values.Length, "data.Length");
            Assert.AreEqual(TestItem.RandomDataA, values[0], "data[0]");
            Assert.AreEqual(null, values[1], "data[1]");
        }

        [Test]
        public void Can_retrieve_empty_receipts()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(2).TestObject;
            Block block0 = _blockTree.FindBlock(0, BlockTreeLookupOptions.None);
            Block block1 = _blockTree.FindBlock(1, BlockTreeLookupOptions.None);

            TxReceipt[][] txReceipts = _syncServer.GetReceipts(new[] {block0.Hash, block1.Hash, TestItem.KeccakA});

            Assert.AreEqual(3, txReceipts.Length, "data.Length");
            Assert.AreEqual(0, txReceipts[0].Length, "data[0]");
            Assert.AreEqual(0, txReceipts[1].Length, "data[1]");
            Assert.AreEqual(0, txReceipts[2].Length, "data[2]");
        }
    }
}