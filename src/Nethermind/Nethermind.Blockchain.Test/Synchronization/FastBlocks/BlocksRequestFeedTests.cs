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
        private IEthSyncPeerPool _peerPool;
        private List<IntSyncPeerMock> _syncPeers = new List<IntSyncPeerMock>();
        private Dictionary<IntSyncPeerMock, BlockTree> _peerTrees = new Dictionary<IntSyncPeerMock, BlockTree>();
        private Dictionary<IntSyncPeerMock, BlockSyncBatch> _pendingResponses = new Dictionary<IntSyncPeerMock, BlockSyncBatch>();

        public BlocksRequestFeedTests()
        {
            _validTree = Build.A.BlockTree().OfChainLength(512).TestObject;
            _peerPool = Substitute.For<IEthSyncPeerPool>();
            _peerPool.AllPeers.Returns((ci) => _syncPeers.Select(sp => new PeerInfo(sp)));
        }

        [Test]
        public void One_peer_with_valid_chain()
        {
            BlockTree localBlockTree = new BlockTree(new MemDb(), new MemDb(), new MemDb(), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            
            BlocksRequestFeed feed = new BlocksRequestFeed(localBlockTree, _peerPool);

            IntSyncPeerMock syncPeer = new IntSyncPeerMock(_validTree);
            _syncPeers.Add(syncPeer);
            _peerTrees[syncPeer] = _validTree;
            
            int time = 0;
            while (true)
            {
                if (_pendingResponses.Count < _syncPeers.Count)
                {
                    BlockSyncBatch batch = feed.PrepareRequest();
                    if (batch == null && _pendingResponses.Count == 0)
                    {
                        break;
                    }

                    if (batch != null)
                    {
                        foreach (IntSyncPeerMock intSyncPeerMock in _syncPeers)
                        {
                            if (intSyncPeerMock.BusyUntil == null)
                            {
                                intSyncPeerMock.BusyUntil = time + intSyncPeerMock.Latency;
                                _pendingResponses.Add(intSyncPeerMock, batch);
                            }
                        }
                    }
                }
                
                foreach (IntSyncPeerMock intSyncPeerMock in _syncPeers)
                {
                    if (intSyncPeerMock.BusyUntil == time)
                    {
                        intSyncPeerMock.BusyUntil = null;
                        BlockSyncBatch responseBatch = CreateResponse(intSyncPeerMock);
                        feed.HandleResponse(responseBatch);
                    }
                }
                
                time++;
            }

            Assert.AreEqual(_validTree.Head.Hash, localBlockTree.BestSuggested.Hash);
        }

        private BlockSyncBatch CreateResponse(IntSyncPeerMock syncPeer)
        {
            BlockTree tree = _peerTrees[syncPeer];
            BlockSyncBatch responseBatch = _pendingResponses[syncPeer];
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