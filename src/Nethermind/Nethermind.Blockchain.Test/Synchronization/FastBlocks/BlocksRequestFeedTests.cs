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
using System.Linq;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.FastBlocks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization.FastBlocks
{
    [TestFixture]
    public class BlocksRequestFeedTests
    {
        private BlockTree _validTree2048;
        private BlockTree _validTree1024;
        private BlockTree _validTree8;
        private BlockTree _badTreeAfter1024;
        private List<LatencySyncPeerMock> _syncPeers;
        private Dictionary<LatencySyncPeerMock, int> _peerMaxResponseSizes;
        private Dictionary<LatencySyncPeerMock, IBlockTree> _peerTrees;
        private Dictionary<LatencySyncPeerMock, BlockSyncBatch> _pendingResponses;
        private BlockTree _localBlockTree;
        private BlocksRequestFeed _feed;
        private long _time;

        public BlocksRequestFeedTests()
        {
            _validTree2048 = Build.A.BlockTree().OfChainLength(2048).TestObject;
            _validTree1024 = Build.A.BlockTree().OfChainLength(1024).TestObject;
            _validTree8 = Build.A.BlockTree().OfChainLength(8).TestObject;
            _badTreeAfter1024 = Build.A.BlockTree().OfChainLength(2048, 1, 1024).TestObject;
        }

        [SetUp]
        public void Setup()
        {
            _syncPeers = new List<LatencySyncPeerMock>();
            _peerTrees = new Dictionary<LatencySyncPeerMock, IBlockTree>();
            _peerMaxResponseSizes = new Dictionary<LatencySyncPeerMock, int>();
            _pendingResponses = new Dictionary<LatencySyncPeerMock, BlockSyncBatch>();

            LatencySyncPeerMock.RemoteIndex = 1;
            _time = 0;
            IEthSyncPeerPool peerPool = Substitute.For<IEthSyncPeerPool>();
            peerPool.WhenForAnyArgs(p => p.ReportNoSyncProgress(Arg.Any<SyncPeerAllocation>()))
                .Do(ci => ((LatencySyncPeerMock) ci.Arg<SyncPeerAllocation>().Current.SyncPeer).BusyUntil = _time + 5000);
            peerPool.AllPeers.Returns((ci) => _syncPeers.Select(sp => new PeerInfo(sp) {HeadNumber = sp.Tree.Head.Number}));
            _localBlockTree = new BlockTree(new MemDb(), new MemDb(), new MemDb(), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            _localBlockTree.SuggestBlock(_validTree8.FindBlock(0));
            
            _feed = new BlocksRequestFeed(_localBlockTree, peerPool);
        }

        [Test]
        public void One_peer_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }
        
        [Test]
        public void One_peer_with_short_valid_chain()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_with_valid_chain_but_varying_latencies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 100);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_one_with_invalid_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 100);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_badTreeAfter1024, 5);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }
        
        [Test]
        public void Two_peers_one_with_invalid_chain_same_latencies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_badTreeAfter1024, 5);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }
        
        [Test]
        public void Two_peers_one_slow_with_invalid_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_badTreeAfter1024, 100);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }
        
        [Test]
        public void Two_valid_peers_various_lengths()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree1024, 5);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }
        
        [Test]
        public void Two_peers_but_with_response_size_limits()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 5);

            _peerMaxResponseSizes[syncPeer1] = 7;
            _peerMaxResponseSizes[syncPeer2] = 13;
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }
        
        [Test]
        public void ManyValidPeers()
        {
            LatencySyncPeerMock[] peers = new LatencySyncPeerMock[100];
            for (int i = 0; i < peers.Length; i++)
            {
                peers[i] = new LatencySyncPeerMock(_validTree2048, 5);
            }
            
            SetupSyncPeers(peers);
            RunFeed();
            Assert.AreEqual(_validTree2048.Head.Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        private void RunFeed(int timeLimit = 5000)
        {
            while (true)
            {
                if (_time > timeLimit)
                {
                    TestContext.WriteLine($"TIMEOUT AT {_time}");
                    throw new TimeoutException();
                }
                
                if (_pendingResponses.Count < _syncPeers.Count)
                {
                    BlockSyncBatch batch = _feed.PrepareRequest();
                    if (batch == null && _pendingResponses.Count == 0)
                    {
                        break;
                    }

                    if (batch != null)
                    {
                        bool wasAssigned = false;
                        foreach (LatencySyncPeerMock syncPeer in _syncPeers)
                        {
                            if (syncPeer.BusyUntil == null  && _peerTrees[syncPeer].Head.Number >= (batch.HeadersSyncBatch.StartNumber ?? 0) + batch.HeadersSyncBatch.RequestSize - 1)
                            {
                                syncPeer.BusyUntil = _time + syncPeer.Latency;
                                _pendingResponses.Add(syncPeer, batch);
                                TestContext.WriteLine($"{_time,6} |SENDING {batch} REQUEST TO {syncPeer.Node:s}");
                                wasAssigned = true;
                                break;
                            }
                        }

                        if (!wasAssigned)
                        {
                            _feed.HandleResponse(batch);
                        }
                    }
                }

                foreach (LatencySyncPeerMock intSyncPeerMock in _syncPeers)
                {
                    if (intSyncPeerMock.BusyUntil == _time)
                    {
                        intSyncPeerMock.BusyUntil = null;
                        BlockSyncBatch responseBatch = CreateResponse(intSyncPeerMock);
                        if (responseBatch != null)
                        {
                            _feed.HandleResponse(responseBatch);
                        }
                    }
                }

                _time++;
            }
        }

        private void SetupSyncPeers(params LatencySyncPeerMock[] syncPeers)
        {
            foreach (LatencySyncPeerMock latencySyncPeerMock in syncPeers)
            {
                _syncPeers.Add(latencySyncPeerMock);
                _peerTrees[latencySyncPeerMock] = latencySyncPeerMock.Tree;
            }
        }

        private BlockSyncBatch CreateResponse(LatencySyncPeerMock syncPeer)
        {
            if (!_pendingResponses.ContainsKey(syncPeer))
            {
                TestContext.WriteLine($"{_time,6} |SYNC PEER {syncPeer.Node:s} WAKES UP");
                return null;
            }

            BlockSyncBatch responseBatch = _pendingResponses[syncPeer];
            IBlockTree tree = _peerTrees[syncPeer];
            _pendingResponses.Remove(syncPeer);
            if (responseBatch.HeadersSyncBatch != null)
            {
                var headersSyncBatch = responseBatch.HeadersSyncBatch;
                Keccak hash = headersSyncBatch.StartHash;
                if (headersSyncBatch.StartNumber != null)
                {
                    hash = tree.FindHeader(headersSyncBatch.StartNumber.Value)?.Hash;
                }

                if (hash == null)
                {
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} CANNOT FIND {headersSyncBatch.StartNumber}");
                    return responseBatch;
                }

                BlockHeader[] headers = tree.FindHeaders(hash, headersSyncBatch.RequestSize, headersSyncBatch.Skip, headersSyncBatch.Reverse);
                responseBatch.HeadersSyncBatch.Response = headers;
                if (_peerMaxResponseSizes.ContainsKey(syncPeer))
                {
                    int maxResponseSize = _peerMaxResponseSizes[syncPeer];
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (i >= maxResponseSize)
                        {
                            headers[i] = null;
                        }
                    }
                }
                
                TestContext.WriteLine($"{_time,6} |SYNC PEER {syncPeer.Node:s} RESPONDING TO [{headersSyncBatch.StartNumber},{headersSyncBatch.StartNumber + headersSyncBatch.RequestSize - 1}]");
            }

            if (responseBatch.BodiesSyncBatch != null)
            {
                for (int i = 0; i < responseBatch.BodiesSyncBatch.Request.Length; i++)
                {
                    responseBatch.BodiesSyncBatch.Response[i] = tree.FindBlock(responseBatch.BodiesSyncBatch.Request[i], false);
                }
            }

            return responseBatch;
        }
    }
}