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
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
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
            
            IHeaderValidator headerValidator = Build.A.HeaderValidator.ThatAlwaysReturnsTrue.TestObject;
            IBlockValidator blockValidator = Build.A.BlockValidator.ThatAlwaysReturnsTrue.TestObject;
            ITransactionValidator transactionValidator = Build.A.TransactionValidator.ThatAlwaysReturnsTrue.TestObject;
            
            _manager = new SynchronizationManager(_blockTree, blockValidator, headerValidator, new TransactionStore(), transactionValidator, NullLogger.Instance);
        }

        private IBlockTree _blockTree;
        private IBlockTree _remoteBlockTree;
        private Block _genesisBlock;
        private SynchronizationManager _manager;

        [Test]
        public async Task Retrieves_missing_blocks_in_batches()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SynchronizationManager.BatchSize * 2).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);
            
            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.Synced += (sender, args) => { resetEvent.Set(); };
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);
            _manager.Start();
            resetEvent.WaitOne(TimeSpan.FromMilliseconds(2000));
            Assert.AreEqual(SynchronizationManager.BatchSize * 2 - 1, (int)_blockTree.BestSuggestedBlock.Number);
        }
        
        [Test]
        public async Task Syncs_with_empty_peer()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(0).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);
            
            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.Synced += (sender, args) => { resetEvent.Set(); };
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);
            _manager.Start();
            resetEvent.WaitOne(TimeSpan.FromMilliseconds(2000));
            Assert.AreEqual(0, (int)_blockTree.BestSuggestedBlock.Number);
        }
        
        [Test]
        public async Task Syncs_when_knows_more_blocks()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SynchronizationManager.BatchSize * 2).TestObject;
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(0).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);
            
            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.Synced += (sender, args) => { resetEvent.Set(); };
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);
            _manager.Start();
            resetEvent.WaitOne(TimeSpan.FromMilliseconds(2000));
            Assert.AreEqual(SynchronizationManager.BatchSize * 2 - 1, (int)_blockTree.BestSuggestedBlock.Number);
        }
        
        [Test]
        public async Task Can_resync_if_missed_a_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SynchronizationManager.BatchSize).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);
            
            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.Synced += (sender, args) => { resetEvent.Set(); };
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);
            _manager.Start();
            resetEvent.WaitOne(TimeSpan.FromMilliseconds(2000));

            BlockTreeBuilder.ExtendTree(_remoteBlockTree, SynchronizationManager.BatchSize * 2);
            _manager.AddNewBlock(_remoteBlockTree.HeadBlock, peer.NodeId);
            
            Assert.AreEqual(SynchronizationManager.BatchSize * 2 - 1, (int)_blockTree.BestSuggestedBlock.Number);
        }
        
        [Test]
        public async Task Can_add_new_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SynchronizationManager.BatchSize).TestObject;
            ISynchronizationPeer peer = new SynchronizationPeerMock(_remoteBlockTree);
            
            ManualResetEvent resetEvent = new ManualResetEvent(false);
            _manager.Synced += (sender, args) => { resetEvent.Set(); };
            Task addPeerTask = _manager.AddPeer(peer);
            Task firstToComplete = await Task.WhenAny(addPeerTask, Task.Delay(2000));
            Assert.AreSame(addPeerTask, firstToComplete);
            _manager.Start();
            resetEvent.WaitOne(TimeSpan.FromMilliseconds(2000));

            Block block = Build.A.Block.WithParent(_remoteBlockTree.HeadBlock).TestObject;
            _manager.AddNewBlock(block, peer.NodeId);
            
            Assert.AreEqual(SynchronizationManager.BatchSize, (int)_blockTree.BestSuggestedBlock.Number);
        }
    }
}