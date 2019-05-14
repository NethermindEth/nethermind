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
        private BlockTree _validTree;
        private List<LatencySyncPeerMock> _syncPeers = new List<LatencySyncPeerMock>();
        private Dictionary<LatencySyncPeerMock, IBlockTree> _peerTrees = new Dictionary<LatencySyncPeerMock, IBlockTree>();
        private Dictionary<LatencySyncPeerMock, BlockSyncBatch> _pendingResponses = new Dictionary<LatencySyncPeerMock, BlockSyncBatch>();
        private BlockTree _localBlockTree;
        private BlocksRequestFeed _feed;
        private long _time;
        
        public BlocksRequestFeedTests()
        {
            _validTree = Build.A.BlockTree().OfChainLength(512).TestObject;
        }
        
        [SetUp]
        public void Setup()
        {
            _time = 0;
            IEthSyncPeerPool peerPool = Substitute.For<IEthSyncPeerPool>();
            peerPool.WhenForAnyArgs(p => p.ReportNoSyncProgress(Arg.Any<SyncPeerAllocation>()))
                .Do(ci => ((LatencySyncPeerMock)ci.Arg<SyncPeerAllocation>().Current.SyncPeer).BusyUntil = _time + 5000);
            peerPool.AllPeers.Returns((ci) => _syncPeers.Select(sp => new PeerInfo(sp) {HeadNumber = sp.Tree.Head.Number}));
            _localBlockTree = new BlockTree(new MemDb(), new MemDb(), new MemDb(), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            _feed = new BlocksRequestFeed(_localBlockTree, peerPool);
        }

        [Test]
        public void One_peer_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(_validTree.Head.Hash, _localBlockTree.BestSuggested.Hash);
        }
        
        [Test]
        public void Two_peers_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree.Head.Hash, _localBlockTree.BestSuggested.Hash);
        }
        
        [Test]
        public void Two_peers_with_valid_chain_but_varying_latencies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree, 5);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree, 100);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(_validTree.Head.Hash, _localBlockTree.BestSuggested.Hash);
        }

        private void RunFeed()
        {
            while (true)
            {
                if (_pendingResponses.Count < _syncPeers.Count)
                {
                    BlockSyncBatch batch = _feed.PrepareRequest();
                    if (batch == null && _pendingResponses.Count == 0)
                    {
                        break;
                    }

                    if (batch != null)
                    {
                        foreach (LatencySyncPeerMock intSyncPeerMock in _syncPeers)
                        {
                            if (intSyncPeerMock.BusyUntil == null)
                            {
                                intSyncPeerMock.BusyUntil = _time + intSyncPeerMock.Latency;
                                _pendingResponses.Add(intSyncPeerMock, batch);
                                break;
                            }
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
                    return responseBatch;
                }

                BlockHeader[] headers = tree.FindHeaders(hash, headersSyncBatch.RequestSize, headersSyncBatch.Skip, headersSyncBatch.Reverse);
                responseBatch.HeadersSyncBatch.Response = headers;
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