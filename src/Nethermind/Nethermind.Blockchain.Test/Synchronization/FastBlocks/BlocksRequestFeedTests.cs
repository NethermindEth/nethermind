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
    [SingleThreaded]
    public class BlocksRequestFeedTests
    {
        private BlockTree _validTree2048;
        private BlockTree _validTree1024;
        private BlockTree _validTree8;
        private BlockTree _badTreeAfter1024;
        private List<LatencySyncPeerMock> _syncPeers;
        private Dictionary<LatencySyncPeerMock, int> _peerMaxResponseSizes;
        private Dictionary<LatencySyncPeerMock, HashSet<long>> _invalidBlocks;
        private Dictionary<LatencySyncPeerMock, IBlockTree> _peerTrees;
        private Dictionary<LatencySyncPeerMock, BlockSyncBatch> _pendingResponses;
        private HashSet<LatencySyncPeerMock> _maliciousByRepetition;
        private HashSet<LatencySyncPeerMock> _maliciousByShiftedOneForward;
        private HashSet<LatencySyncPeerMock> _maliciousByShiftedOneBack;
        private HashSet<LatencySyncPeerMock> _incorrectByTooShortMessages;
        private HashSet<LatencySyncPeerMock> _incorrectByTooLongMessages;
        private HashSet<LatencySyncPeerMock> _timingOut;
        private BlockTree _localBlockTree;
        private BlocksRequestFeed _feed;
        private long _time;

        public BlocksRequestFeedTests()
        {
            // make trees lazy
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
            _invalidBlocks = new Dictionary<LatencySyncPeerMock, HashSet<long>>();
            _maliciousByRepetition = new HashSet<LatencySyncPeerMock>();
            _maliciousByShiftedOneForward = new HashSet<LatencySyncPeerMock>();
            _maliciousByShiftedOneBack = new HashSet<LatencySyncPeerMock>();
            _incorrectByTooShortMessages = new HashSet<LatencySyncPeerMock>();
            _incorrectByTooLongMessages = new HashSet<LatencySyncPeerMock>();
            _timingOut = new HashSet<LatencySyncPeerMock>();

            LatencySyncPeerMock.RemoteIndex = 1;
            _time = 0;
            _syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            _syncPeerPool.WhenForAnyArgs(p => p.ReportNoSyncProgress(Arg.Any<SyncPeerAllocation>()))
                .Do(ci =>
                {
                    LatencySyncPeerMock mock = ((LatencySyncPeerMock) ci.Arg<SyncPeerAllocation>().Current.SyncPeer);
                    mock.BusyUntil = _time + 5000;
                    mock.IsReported = true;
                });

            _syncPeerPool.WhenForAnyArgs(p => p.ReportInvalid(Arg.Any<SyncPeerAllocation>()))
                .Do(ci =>
                {
                    LatencySyncPeerMock mock = ((LatencySyncPeerMock) ci.Arg<SyncPeerAllocation>().Current.SyncPeer);
                    mock.BusyUntil = _time + 30000;
                    mock.IsReported = true;
                });

            _syncPeerPool.AllPeers.Returns((ci) => _syncPeers.Select(sp => new PeerInfo(sp) {HeadNumber = sp.Tree.Head.Number}));
            SetupLocalTree();
        }

        private void SetupLocalTree(int length = 1)
        {
            _localBlockTree = new BlockTree(new MemDb(), new MemDb(), new MemDb(), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            for (int i = 0; i < length; i++)
            {
                _localBlockTree.SuggestBlock(_validTree2048.FindBlock(i));    
            }

            _feed = new BlocksRequestFeed(_localBlockTree, _syncPeerPool, LimboLogs.Instance);
        }

        [Test]
        public void One_peer_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }
        
        [Test]
        public void One_peer_with_valid_chain_when_already_partially_synced()
        {
            SetupLocalTree(512);
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void One_peer_with_short_valid_chain()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree8);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(0).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_with_valid_chain_but_varying_latencies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 100);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_one_with_invalid_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 40);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_badTreeAfter1024, 5);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_one_with_invalid_chain_same_latencies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_badTreeAfter1024, 5);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_one_timing_out()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 5);
            _timingOut.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed(20000);
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_one_slow_with_invalid_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_badTreeAfter1024, 100);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_valid_peers_various_lengths()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree1024, 5);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
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
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void Two_peers_one_with_invalid_block()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 5);

            _invalidBlocks[syncPeer1] = new HashSet<long> {1720};
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void One_malicious_by_repetition_peer()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            _maliciousByRepetition.Add(syncPeer1);

            SetupSyncPeers(syncPeer1);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(253).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void One_malicious_by_repetition_peer_other_fine_but_slow()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 100);
            _maliciousByRepetition.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void One_malicious_by_shift_forward_other_fine_but_slow()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 100);
            _maliciousByShiftedOneForward.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void One_malicious_by_shift_back_other_fine_but_slow()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 100);
            _maliciousByShiftedOneBack.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void One_peer_sending_too_short_messages()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 200);
            _incorrectByTooShortMessages.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }

        [Test]
        public void One_peer_sending_too_long_messages()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 5);
            _incorrectByTooLongMessages.Add(syncPeer1);

            SetupSyncPeers(syncPeer1);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
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
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }
        
        [Test]
        public void ManyInvalidPeers()
        {
            LatencySyncPeerMock[] peers = new LatencySyncPeerMock[100];
            for (int i = 0; i < peers.Length; i++)
            {
                if (i == 50)
                {
                    peers[i] = new LatencySyncPeerMock(_validTree2048, 5);    
                }
                else
                {
                    peers[i] = new LatencySyncPeerMock(_badTreeAfter1024, 5);    
                }
            }

            SetupSyncPeers(peers);
            RunFeed();
            Assert.AreEqual(_validTree2048.FindBlock(2015).Hash, _localBlockTree.BestSuggested.Hash, _localBlockTree.BestSuggested.ToString());
        }


        private int _timeoutTime = 5000;
        private IEthSyncPeerPool _syncPeerPool;

        private void RunFeed(int timeLimit = 5000)
        {
            while (true)
            {
                if (_time > timeLimit)
                {
                    TestContext.WriteLine($"TIMEOUT AT {_time}");
                    break;
                }

                if (_pendingResponses.Count < _syncPeers.Count(p => !p.IsReported))
                {
                    BlockSyncBatch batch = _feed.PrepareRequest(SyncModeSelector.FullSyncThreshold);
                    if (batch == null && _pendingResponses.Count == 0)
                    {
                        TestContext.WriteLine($"STOP - NULL BATCH AND NO PENDING");
                        break;
                    }

                    if (batch != null)
                    {
                        bool wasAssigned = false;
                        foreach (LatencySyncPeerMock syncPeer in _syncPeers)
                        {
                            if (syncPeer.BusyUntil == null
                                && _peerTrees[syncPeer].Head.Number >= (batch.HeadersSyncBatch.StartNumber ?? 0) + batch.HeadersSyncBatch.RequestSize - 1
                                && (syncPeer.TotalDifficultyOnSessionStart >= (batch.MinTotalDifficulty ?? 0)))
                            {
                                syncPeer.BusyUntil = _time + syncPeer.Latency;
                                if (_timingOut.Contains(syncPeer))
                                {
                                    syncPeer.BusyUntil = _time + _timeoutTime;
                                }

                                batch.AssignedPeer = new SyncPeerAllocation(new PeerInfo(syncPeer), "test");
                                _pendingResponses.Add(syncPeer, batch);
                                TestContext.WriteLine($"{_time,6} |SENDING {batch} REQUEST TO {syncPeer.Node:s}");
                                wasAssigned = true;
                                break;
                            }
                        }

                        if (!wasAssigned)
                        {
//                            TestContext.WriteLine($"{_time,6} | {batch} WAS NOT ASSIGNED");
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
                syncPeer.IsReported = false;
                return null;
            }

            BlockSyncBatch responseBatch = _pendingResponses[syncPeer];
            IBlockTree tree = _peerTrees[syncPeer];
            _pendingResponses.Remove(syncPeer);

            if (_timingOut.Contains(syncPeer))
            {
                TestContext.WriteLine($"{_time,6} |SYNC PEER {syncPeer.Node:s} TIMED OUT");
                // timeout punishment
                syncPeer.BusyUntil = _time + 5000;
                syncPeer.IsReported = true;
                return responseBatch;
            }

            if (responseBatch.HeadersSyncBatch != null)
            {
                var headersSyncBatch = responseBatch.HeadersSyncBatch;
                Keccak hash = headersSyncBatch.StartHash;
                if (headersSyncBatch.StartNumber != null)
                {
                    long startNumber = headersSyncBatch.StartNumber.Value;
                    if (_maliciousByShiftedOneBack.Contains(syncPeer))
                    {
                        startNumber++;
                        TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND SHIFTED MESSAGES ({startNumber} INSTEAD OF {headersSyncBatch.StartNumber})");
                    }
                    else if (_maliciousByShiftedOneForward.Contains(syncPeer))
                    {
                        startNumber = Math.Max(0, startNumber - 1);
                        TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND SHIFTED MESSAGES ({startNumber} INSTEAD OF {headersSyncBatch.StartNumber})");
                    }

                    hash = tree.FindHeader(startNumber)?.Hash;
                }

                if (hash == null)
                {
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} CANNOT FIND {headersSyncBatch.StartNumber}");
                    return responseBatch;
                }

                int requestSize = headersSyncBatch.RequestSize;
                if (_incorrectByTooLongMessages.Contains(syncPeer))
                {
                    requestSize *= 2;
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND TOO LONG MESSAGE ({requestSize} INSTEAD OF {headersSyncBatch.RequestSize})");
                }
                else if (_incorrectByTooShortMessages.Contains(syncPeer))
                {
                    requestSize = Math.Max(1, requestSize / 2);
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND TOO SHORT MESSAGE ({requestSize} INSTEAD OF {headersSyncBatch.RequestSize})");
                }

                BlockHeader[] headers = tree.FindHeaders(hash, requestSize, headersSyncBatch.Skip, headersSyncBatch.Reverse);
                if (_invalidBlocks.ContainsKey(syncPeer))
                {
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (_invalidBlocks[syncPeer].Contains(headers[i].Number))
                        {
                            TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND AN INVALID BLOCK AT {headers[i].Number}");
                            headers[i] = Build.A.Block.WithDifficulty(1).TestObject.Header;
                        }
                    }
                }

                if (_maliciousByRepetition.Contains(syncPeer))
                {
                    headers[headers.Length - 1] = headers[headers.Length - 3];
                    headers[headers.Length - 2] = headers[headers.Length - 3];
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND A MALICIOUS (REPEATED) MESSAGE");
                }

                responseBatch.HeadersSyncBatch.Response = headers;
                if (_peerMaxResponseSizes.ContainsKey(syncPeer))
                {
                    int maxResponseSize = _peerMaxResponseSizes[syncPeer];
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND NULLS AFTER INDEX {maxResponseSize}");
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (i >= maxResponseSize)
                        {
                            headers[i] = null;
                        }
                    }
                }

                TestContext.WriteLine($"{_time,6} |SYNC PEER {syncPeer.Node:s} RESPONDING TO [{headersSyncBatch.StartNumber},{headersSyncBatch.StartNumber + requestSize - 1}]");
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