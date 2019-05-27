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
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization.FastBlocks
{
    [TestFixture]
    [SingleThreaded]
    public class BlockRequestFeedTests
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
        private HashSet<LatencySyncPeerMock> _maliciousByShortAtStart;
        private HashSet<LatencySyncPeerMock> _incorrectByTooShortMessages;
        private HashSet<LatencySyncPeerMock> _incorrectByTooLongMessages;
        private HashSet<LatencySyncPeerMock> _timingOut;
        private BlockTree _localBlockTree;
        private BlockRequestFeed _feed;
        private long _time;

        public BlockRequestFeedTests()
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
            _maliciousByShortAtStart = new HashSet<LatencySyncPeerMock>();
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

            _syncPeerPool.WhenForAnyArgs(p => p.ReportNoSyncProgress(Arg.Any<PeerInfo>()))
                .Do(ci =>
                {
                    LatencySyncPeerMock mock = ((LatencySyncPeerMock) ci.Arg<PeerInfo>().SyncPeer);
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

            _syncPeerPool.WhenForAnyArgs(p => p.ReportInvalid(Arg.Any<PeerInfo>()))
                .Do(ci =>
                {
                    LatencySyncPeerMock mock = ((LatencySyncPeerMock) ci.Arg<PeerInfo>().SyncPeer);
                    mock.BusyUntil = _time + 30000;
                    mock.IsReported = true;
                });

            _syncPeerPool.AllPeers.Returns((ci) => _syncPeers.Select(sp => new PeerInfo(sp) {HeadNumber = sp.Tree.Head.Number}));
            
            SetupLocalTree();
            SetupFeed();
        }

        private void SetupLocalTree(int length = 1)
        {
            _localBlockTree = new BlockTree(new MemDb(), new MemDb(), new MemDb(), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            for (int i = 0; i < length; i++)
            {
                _localBlockTree.SuggestBlock(_validTree2048.FindBlock(i));
            }
        }

        private void SetupFeed(bool syncBodies = false)
        {
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotHash = _validTree2048.Head.Hash.ToString();
            syncConfig.PivotNumber = _validTree2048.Head.Number.ToString();
            syncConfig.PivotTotalDifficulty = _validTree2048.Head.TotalDifficulty.ToString();
            if (syncBodies)
            {
                syncConfig.DownloadBodiesInFastSync = true;
            }

            _feed = new BlockRequestFeed(_localBlockTree, _syncPeerPool, syncConfig, LimboLogs.Instance);
            _feed.StartNumber = 2047;
            _feed.StartHash = _validTree2048.Head.Hash;
        }

        [Test]
        public void One_peer_with_valid_chain_bodies()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree2048);
            SetupFeed(true);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(102, _time);

            AssertTreeSynced(_validTree2048, true);
        }
        
        [Test]
        public void One_peer_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(48, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(25, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_with_valid_chain_and_various_max_response_sizes()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _peerMaxResponseSizes[syncPeer1] = 100;
            _peerMaxResponseSizes[syncPeer2] = 75;

            SetupSyncPeers(syncPeer1, syncPeer2);

            RunFeed();
            Assert.AreEqual(90, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_malicious_by_repetition()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByRepetition.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(49, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_malicious_by_short_at_start()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByShortAtStart.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(49, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_malicious_by_shift_forward()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByShiftedOneForward.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(49, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_malicious_by_shift_back()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByShiftedOneBack.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(49, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_sending_too_short_messages()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _incorrectByTooShortMessages.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(54, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_sending_too_long_messages()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _incorrectByTooLongMessages.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(49, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_timing_out()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _timingOut.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed(20000);
            Assert.AreEqual(5007, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void One_peer_with_valid_one_with_invalid_A()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_badTreeAfter1024);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 300);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(2409, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void One_peer_with_valid_one_with_invalid_B()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 300);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_badTreeAfter1024);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(1204, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_with_valid_chain_one_shorter()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree1024);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(37, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Short_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree1024);
            SetupSyncPeers(syncPeer1, syncPeer2);

            _feed.StartNumber = _validTree8.Head.Number;
            _feed.StartHash = _validTree8.Head.Hash;
            RunFeed();
            Assert.AreEqual(6, _time);

            AssertTreeSynced(_validTree8);
        }

        [Test]
        public void Shorter_responses()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer1);

            _incorrectByTooShortMessages.Add(syncPeer1);

            RunFeed();

            Assert.AreEqual(432, _time);

            AssertTreeSynced(_validTree2048);
        }

        private void AssertTreeSynced(IBlockTree tree, bool bodiesSync = false)
        {
            Keccak nextHash = tree.Head.Hash;
            for (int i = 0; i < tree.Head.Number; i++)
            {
                BlockHeader header = _localBlockTree.FindHeader(nextHash);
                Assert.NotNull(header, $"header {tree.Head.Number - i}");
                if (bodiesSync)
                {
                    Block block = _localBlockTree.FindBlock(nextHash, false);
                    Assert.NotNull(block, $"body {tree.Head.Number - i}");
                }
                
                nextHash = header.ParentHash;
            }
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
                    BlockSyncBatch batch = _feed.PrepareRequest();
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
                                && _peerTrees[syncPeer].Head.Number >= (batch.MinNumber ?? 0)
                                && (syncPeer.TotalDifficultyOnSessionStart >= (batch.MinTotalDifficulty ?? 0)))
                            {
                                syncPeer.BusyUntil = _time + syncPeer.Latency;
                                if (_timingOut.Contains(syncPeer))
                                {
                                    syncPeer.BusyUntil = _time + _timeoutTime;
                                }

                                batch.Allocation = new SyncPeerAllocation(new PeerInfo(syncPeer), "test");
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

            TestContext.WriteLine($"{_time,6} |SYNC PEER {syncPeer.Node:s} RESPONDING TO {responseBatch}");
            var headersSyncBatch = responseBatch.HeadersSyncBatch;
            var bodiesSyncBatch = responseBatch.BodiesSyncBatch;
            if (headersSyncBatch != null)
            {
                PrepareHeadersResponse(headersSyncBatch, syncPeer, tree);
            }
            else if (bodiesSyncBatch != null)
            {
                PrepareBodiesResponse(bodiesSyncBatch, tree);
            }

            return responseBatch;
        }

        private static void PrepareBodiesResponse(BodiesSyncBatch bodiesSyncBatch, IBlockTree tree)
        {
            bodiesSyncBatch.Response = new Block[bodiesSyncBatch.Request.Length];
            for (int i = 0; i < bodiesSyncBatch.Request.Length; i++)
            {
                bodiesSyncBatch.Response[i] = tree.FindBlock(bodiesSyncBatch.Request[i], false);
            }
        }

        private void PrepareHeadersResponse(HeadersSyncBatch headersSyncBatch, LatencySyncPeerMock syncPeer, IBlockTree tree)
        {
            if (headersSyncBatch != null)
            {
                long startNumber = headersSyncBatch.StartNumber;
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

                Keccak hash = tree.FindHeader(startNumber)?.Hash;

                if (hash == null)
                {
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} CANNOT FIND {headersSyncBatch.StartNumber}");
                    return;
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

                BlockHeader[] headers = tree.FindHeaders(hash, requestSize, 0, false);
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

                if (headers.Length > 3 && _maliciousByRepetition.Contains(syncPeer))
                {
                    headers[headers.Length - 1] = headers[headers.Length - 3];
                    headers[headers.Length - 2] = headers[headers.Length - 3];
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND A MALICIOUS (REPEATED) MESSAGE");
                }

                if (_maliciousByShortAtStart.Contains(syncPeer))
                {
                    headers[0] = null;
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND A MALICIOUS (SHORT AT START) MESSAGE");
                }


                headersSyncBatch.Response = headers;
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
            }
        }
    }
}