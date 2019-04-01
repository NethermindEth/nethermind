using System;
using System.Configuration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class EthSyncPeerPoolTests
    {
        private INodeStatsManager _stats;
        private IEthSyncPeerPool _pool;
        private IBlockTree _blockTree;

        [SetUp]
        public void SetUp()
        {
            _blockTree = Substitute.For<IBlockTree>();
            _stats = new NodeStatsManager(new StatsConfig(), LimboLogs.Instance);
            _pool = new EthSyncPeerPool(_blockTree, _stats, new SyncConfig(), LimboLogs.Instance);
        }

        private class SimpleSyncPeerMock : ISyncPeer
        {
            public SimpleSyncPeerMock(PublicKey publicKey)
            {
                Node = new Node(publicKey, "127.0.0.1", 30303, false);
            }

            public Guid SessionId { get; } = Guid.NewGuid();

            public bool IsFastSyncSupported => true;
            public Node Node { get; }
            public string ClientId => "simple mock";
            public UInt256 TotalDifficultyOnSessionStart => 1;

            public Task<Block[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
            {
                return Task.FromResult(new Block[0]);
            }

            public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
            {
                return Task.FromResult(new BlockHeader[0]);
            }

            public Task<BlockHeader[]> GetBlockHeaders(UInt256 number, int maxBlocks, int skip, CancellationToken token)
            {
                return Task.FromResult(new BlockHeader[0]);
            }

            public Task<BlockHeader> GetHeadBlockHeader(Keccak hash, CancellationToken token)
            {
                return Task.FromResult(Build.A.BlockHeader.TestObject);
            }

            public void SendNewBlock(Block block)
            {
            }

            public void SendNewTransaction(Transaction transaction)
            {
            }

            public Task<TransactionReceipt[][]> GetReceipts(Keccak[] blockHash, CancellationToken token)
            {
                return Task.FromResult(new TransactionReceipt[0][]);
            }

            public Task<byte[][]> GetNodeData(Keccak[] hashes, CancellationToken token)
            {
                return Task.FromResult(new byte[0][]);
            }
        }
        
        [Test]
        public void Cannot_add_when_not_started()
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(0, _pool.PeerCount);
                _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeys[i]));
            }
        }
        
        [Test]
        public async Task Cannot_remove_when_stopped()
        {
            _pool.Start();
            ISyncPeer[] syncPeers = new ISyncPeer[3];
            for (int i = 0; i < 3; i++)
            {
                syncPeers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
                _pool.AddPeer(syncPeers[i]);
            }

            await _pool.StopAsync();
            
            for (int i = 3; i > 0; i--)
            {
                Assert.AreEqual(3, _pool.PeerCount, $"Remove {i}");
                _pool.RemovePeer(syncPeers[i - 1]);
            }
        }

        [Test]
        public void Peer_count_is_valid_when_adding()
        {
            _pool.Start();
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(i, _pool.PeerCount);
                _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeys[i]));
            }
        }

        [Test]
        public void Peer_count_is_valid_when_removing()
        {
            _pool.Start();
            ISyncPeer[] syncPeers = new ISyncPeer[3];
            for (int i = 0; i < 3; i++)
            {
                syncPeers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
                _pool.AddPeer(syncPeers[i]);
            }

            for (int i = 3; i > 0; i--)
            {
                Assert.AreEqual(i, _pool.PeerCount, $"Remove {i}");
                _pool.RemovePeer(syncPeers[i - 1]);
            }
        }

        [Test]
        public void Can_find_sync_peers()
        {
            _pool.Start();
            ISyncPeer[] syncPeers = new ISyncPeer[3];
            for (int i = 0; i < 3; i++)
            {
                syncPeers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
                _pool.AddPeer(syncPeers[i]);
            }

            for (int i = 3; i > 0; i--)
            {
                Assert.True(_pool.TryFind(syncPeers[i - 1].Node.Id, out PeerInfo peerInfo));
                Assert.NotNull(peerInfo);
            }
        }

        [Test]
        public void Can_start()
        {
            _pool.Start();
        }

        [Test]
        public async Task Can_start_and_stop()
        {
            _pool.Start();
            await _pool.StopAsync();
        }

        [Test]
        public void Can_refresh()
        {
            _pool.Start();
            var syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            _pool.AddPeer(syncPeer);
            _pool.Refresh(TestItem.PublicKeyA);

            syncPeer.Received(2).GetHeadBlockHeader(Arg.Any<Keccak>(), Arg.Any<CancellationToken>());
        }

        [Test]
        public void Can_list_all_peers()
        {
            _pool.Start();
            for (int i = 0; i < 3; i++)
            {
                _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeys[i]));
            }

            Assert.AreEqual(3, _pool.AllPeers.Count());
        }
        
        [Test]
        public void Can_get_best()
        {
            throw new NotImplementedException();
        }
        
        [Test]
        public void Can_return()
        {
            throw new NotImplementedException();
        }
    }
}