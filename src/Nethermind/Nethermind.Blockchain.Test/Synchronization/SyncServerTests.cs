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

using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
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
            _syncServer = new SyncServer(new StateDb(), new StateDb(), _blockTree, NullReceiptStorage.Instance, TestSealValidator.AlwaysValid, _peerPool, _synchronizer, new SyncConfig(), LimboLogs.Instance);
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
    }
}