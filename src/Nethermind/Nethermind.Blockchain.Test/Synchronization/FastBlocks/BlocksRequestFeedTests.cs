using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.FastBlocks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization.FastBlocks
{
    [TestFixture]
    public class BlocksRequestFeedTests
    {
        private BlockTree _validTree;
        private SyncConfig _syncConfig = new SyncConfig();
        private INodeStatsManager _statsManager = new NodeStatsManager(new StatsConfig(), LimboLogs.Instance);

        public BlocksRequestFeedTests()
        {
            _validTree = Build.A.BlockTree().OfChainLength(512).TestObject;
        }

        private List<PeerInfo> _peers = new List<PeerInfo>();
        
        [Test]
        public void One_peer_with_valid_chain()
        {
            BlockTree localBlockTree = new BlockTree(new MemDb(), new MemDb(), new MemDb(), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            IEthSyncPeerPool peerPool = Substitute.For<IEthSyncPeerPool>();
            BlocksRequestFeed feed = new BlocksRequestFeed(localBlockTree, peerPool);

            ISyncPeer syncPeer = new SyncPeerMock(_validTree);
            _peers.Add(new PeerInfo(syncPeer));
            peerPool.AllPeers.Returns(_peers);
            
            while (true)
            {
                BlockSyncBatch batch = feed.PrepareRequest();
                if (batch == null)
                {
                    break;
                }
                
                FillValidBatch(batch);
                feed.HandleResponse(batch);
            }

            Assert.AreEqual(_validTree.Head.Hash, localBlockTree.BestSuggested.Hash);
        }

        private void FillValidBatch(BlockSyncBatch batch)
        {
            if (batch.HeadersSyncBatch != null)
            {
                var headersSyncBatch = batch.HeadersSyncBatch;
                Keccak hash = headersSyncBatch.StartHash;
                if (headersSyncBatch.StartNumber != null)
                {
                    hash = _validTree.FindHeader(headersSyncBatch.StartNumber.Value)?.Hash;    
                }

                if (hash == null)
                {
                    return;
                }
                
                BlockHeader[] headers = _validTree.FindHeaders(hash, headersSyncBatch.RequestSize, headersSyncBatch.Skip, headersSyncBatch.Reverse);
                batch.HeadersSyncBatch.Response = headers;
            }

            if (batch.BodiesSyncBatch != null)
            {
                for (int i = 0; i < batch.BodiesSyncBatch.Request.Length; i++)
                {
                    batch.BodiesSyncBatch.Response[i] = _validTree.FindBlock(batch.BodiesSyncBatch.Request[i], false);
                }
            }
        }
    }
}