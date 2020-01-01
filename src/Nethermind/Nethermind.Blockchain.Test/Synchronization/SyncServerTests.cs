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

using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Stats.Model;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class SyncServerTests
    {
        private IBlockTree _blockTree;
        private IEthSyncPeerPool _peerPool;
        private ISynchronizer _synchronizer;
        private SyncServer _syncServer;

        [SetUp]
        public void Setup()
        {
            _blockTree = Substitute.For<IBlockTree>();
            _peerPool = Substitute.For<IEthSyncPeerPool>();
            _synchronizer = Substitute.For<ISynchronizer>();
            _syncServer = new SyncServer(new StateDb(), new StateDb(), _blockTree, NullReceiptStorage.Instance, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, _peerPool, _synchronizer, new SyncConfig(), LimboLogs.Instance);
        }

        [Test]
        public void _When_finding_hash_it_does_not_load_headers()
        {
            _blockTree.FindHash(123).Returns(TestItem.KeccakA);
            Keccak result = _syncServer.FindHash(123);

            _blockTree.DidNotReceive().FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>());
            _blockTree.DidNotReceive().FindHeader(Arg.Any<Keccak>(), Arg.Any<BlockTreeLookupOptions>());
            _blockTree.DidNotReceive().FindBlock(Arg.Any<Keccak>(), Arg.Any<BlockTreeLookupOptions>());
            Assert.AreEqual(TestItem.KeccakA, result);
        }

        [Test]
        public void When_finding_by_hash_block_info_is_not_loaded()
        {
            _syncServer.Find(TestItem.KeccakA);
            _blockTree.Received().FindBlock(Arg.Any<Keccak>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        [TestCase(true, true, true)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        public void Can_accept_new_valid_blocks(bool sealOk, bool validationOk, bool accepted)
        {
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;

            ISealValidator sealValidator = sealOk ? TestSealValidator.AlwaysValid : TestSealValidator.NeverValid;
            IBlockValidator blockValidator = validationOk ? TestBlockValidator.AlwaysValid : TestBlockValidator.NeverValid;
            _syncServer = new SyncServer(new StateDb(), new StateDb(), localBlockTree, NullReceiptStorage.Instance, blockValidator, sealValidator, _peerPool, _synchronizer, new SyncConfig(), LimboLogs.Instance);

            Node knownNode = new Node(TestItem.PublicKeyA, "127.0.0.1", 30303);
            _peerPool.TryFind(knownNode.Id, out PeerInfo peerInfo).Returns(x =>
            {
                x[1] = new PeerInfo(Substitute.For<ISyncPeer>());
                return true;
            });
            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            _synchronizer.SyncMode.Returns(SyncMode.Full);

            if (!accepted)
            {
                Assert.Throws<EthSynchronizationException>(() => _syncServer.AddNewBlock(block, knownNode));
            }
            else
            {
                _syncServer.AddNewBlock(block, knownNode);
            }
            
            if (accepted)
            {
                Assert.AreEqual(localBlockTree.BestSuggestedHeader, block.Header);
            }
            else
            {
                Assert.AreNotEqual(localBlockTree.BestSuggestedHeader, block.Header);
            }
        }
        
        [Test]
        public void Rejects_new_blocks_with_failing_seal()
        {
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;

            _syncServer = new SyncServer(new StateDb(), new StateDb(), localBlockTree, NullReceiptStorage.Instance, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, _peerPool, _synchronizer, new SyncConfig(), LimboLogs.Instance);

            Node knownNode = new Node(TestItem.PublicKeyA, "127.0.0.1", 30303);
            _peerPool.TryFind(knownNode.Id, out PeerInfo peerInfo).Returns(x =>
            {
                x[1] = new PeerInfo(Substitute.For<ISyncPeer>());
                return true;
            });
            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            _synchronizer.SyncMode.Returns(SyncMode.Full);
            _syncServer.AddNewBlock(block, knownNode);

            Assert.AreEqual(localBlockTree.BestSuggestedHeader, block.Header);
        }
        
        [Test]
        public void Rejects_new_old_blocks()
        {
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(600).TestObject;

            ISealValidator sealValidator = Substitute.For<ISealValidator>();
            IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
            _syncServer = new SyncServer(new StateDb(), new StateDb(), localBlockTree, NullReceiptStorage.Instance, blockValidator, sealValidator, _peerPool, _synchronizer, new SyncConfig(), LimboLogs.Instance);

            Node knownNode = new Node(TestItem.PublicKeyA, "127.0.0.1", 30303);
            _peerPool.TryFind(knownNode.Id, out PeerInfo peerInfo).Returns(x =>
            {
                x[1] = new PeerInfo(Substitute.For<ISyncPeer>());
                return true;
            });
            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            _synchronizer.SyncMode.Returns(SyncMode.Full);
            _syncServer.AddNewBlock(block, knownNode);

            sealValidator.DidNotReceive().ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>());
        }
    }
}