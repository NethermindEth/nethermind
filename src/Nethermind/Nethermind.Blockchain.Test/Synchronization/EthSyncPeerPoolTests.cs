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
            _stats = Substitute.For<INodeStatsManager>();
            _pool = new EthSyncPeerPool(_blockTree, _stats, new SyncConfig(), LimboLogs.Instance);
        }
        
        [TearDown]
        public async Task TearDown()
        {
            await _pool.StopAsync();
        }

        private class SimpleSyncPeerMock : ISyncPeer
        {
            public SimpleSyncPeerMock(PublicKey publicKey, string description = "simple mock")
            {
                Node = new Node(publicKey, "127.0.0.1", 30303, false);
                ClientId = description;
            }

            public Guid SessionId { get; } = Guid.NewGuid();

            public bool IsFastSyncSupported => true;
            public Node Node { get; }
            public string ClientId { get; }
        
            public UInt256 TotalDifficultyOnSessionStart => 1;

            public Task<Block[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
            {
                return Task.FromResult(new Block[0]);
            }

            public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
            {
                return Task.FromResult(new BlockHeader[0]);
            }

            public Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
            {
                return Task.FromResult(new BlockHeader[0]);
            }

            public async Task<BlockHeader> GetHeadBlockHeader(Keccak hash, CancellationToken token)
            {
                if (_shouldFail)
                {
                    throw new Exception("Should fails");
                }
                
                if (_headerResponseTime.HasValue)
                {
                    await Task.Delay(_headerResponseTime.Value);
                }
                
                return await Task.FromResult(Build.A.BlockHeader.TestObject);
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

            private int? _headerResponseTime;
            
            private bool _shouldFail;

            public void SetHeaderResponseTime(int responseTime)
            {
                _headerResponseTime = responseTime;
            }
            
            public void SetHeaderFailure(bool shouldFail)
            {
                _shouldFail = shouldFail;
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
        public void Does_not_crash_when_adding_twice_same_peer()
        {
            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            
            Assert.AreEqual(1, _pool.PeerCount);
        }
        
        [Test]
        public void Does_not_crash_when_removing_non_existing_peer()
        {
            _pool.Start();
            _pool.RemovePeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            Assert.AreEqual(0, _pool.PeerCount);
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
        public async Task Can_refresh()
        {
            _pool.Start();
            var syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            _pool.AddPeer(syncPeer);
            _pool.Refresh(TestItem.PublicKeyA);
            await Task.Delay(200);

            await syncPeer.Received(2).GetHeadBlockHeader(Arg.Any<Keccak>(), Arg.Any<CancellationToken>());
        }

        private void SetupLatencyStats(PublicKey publicKey, int milliseconds)
        {
            Node node = new Node(publicKey, "127.0.0.1", 30303);
            NodeStatsLight stats = new NodeStatsLight(node, new StatsConfig());
            stats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, milliseconds);
            stats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockBodies, milliseconds);
            stats.AddLatencyCaptureEvent(NodeLatencyStatType.P2PPingPong, milliseconds);
            
            _stats.GetOrAdd(Arg.Is<Node>(n => n.Id == publicKey)).Returns(stats);
        }
        
        [Test]
        public async Task Can_replace_peer_with_better()
        {
            SetupLatencyStats(TestItem.PublicKeyA, 100);
            SetupLatencyStats(TestItem.PublicKeyB, 50);
            
            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA, "A"));
            await Task.Delay(200);
            SyncPeerAllocation allocation = _pool.Allocate();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB, "B"));
            await Task.Delay(1200);
            
            Assert.True(replaced);
        }
        
        [Test]
        public async Task Does_not_replace_with_a_worse_peer()
        {
            SetupLatencyStats(TestItem.PublicKeyA, 100);
            SetupLatencyStats(TestItem.PublicKeyB, 200);
            
            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await Task.Delay(200);
            SyncPeerAllocation allocation = _pool.Allocate();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await Task.Delay(1200);
            
            Assert.False(replaced);
        }
        
        [Test]
        public async Task Does_not_replace_if_too_small_percentage_change()
        {
            SetupLatencyStats(TestItem.PublicKeyA, 100);
            SetupLatencyStats(TestItem.PublicKeyB, 91);
            
            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await Task.Delay(200);
            SyncPeerAllocation allocation = _pool.Allocate();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await Task.Delay(2200);
            
            Assert.False(replaced);
        }
        
        [Test]
        public async Task Does_not_replace_on_small_difference_in_low_numbers()
        {
            SetupLatencyStats(TestItem.PublicKeyA, 5);
            SetupLatencyStats(TestItem.PublicKeyB, 4);
            
            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await Task.Delay(200);
            SyncPeerAllocation allocation = _pool.Allocate();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await Task.Delay(2200);
            
            Assert.False(replaced);
        }
        
        [Test]
        public async Task Can_stay_when_current_is_best()
        {
            SetupLatencyStats(TestItem.PublicKeyA, 100);
            SetupLatencyStats(TestItem.PublicKeyB, 100);
            
            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await Task.Delay(200);
            SyncPeerAllocation allocation = _pool.Allocate();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await Task.Delay(1200);
            
            Assert.False(replaced);
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
        public async Task Can_borrow_peer()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            
            _pool.Start();
            _pool.AddPeer(peer);
            await Task.Delay(200);
            
            var allocation = _pool.Allocate();
            
            Assert.AreSame(peer, allocation.Current?.SyncPeer);
        }
        
        [Test]
        public async Task Can_remove_borrowed_peer()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            
            _pool.Start();
            _pool.AddPeer(peer);
            await Task.Delay(200);
            
            var allocation = _pool.Allocate();
            _pool.RemovePeer(peer);
            
            Assert.Null(allocation.Current);
        }
        
        [Test]
        public async Task Will_remove_peer_if_times_out_on_init()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            peer.SetHeaderResponseTime(20000);
            
            _pool.Start();
            _pool.AddPeer(peer);
            await Task.Delay(12000);
            
            
            var allocation = _pool.Allocate();
            
            Assert.AreEqual(null, allocation.Current);
            Assert.AreEqual(0, _pool.PeerCount);
        }
        
        [Test]
        public async Task Can_remove_during_init()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            peer.SetHeaderResponseTime(1000);
            
            _pool.Start();
            _pool.AddPeer(peer);
            await Task.Delay(200);
            
            var allocation = _pool.Allocate();
            _pool.RemovePeer(peer);
            await Task.Delay(1000);
            
            Assert.AreEqual(null, allocation.Current);
            Assert.AreEqual(0, _pool.PeerCount);
        }
        
        [Test]
        public async Task It_is_fine_to_fail_init()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            peer.SetHeaderFailure(true);
            
            _pool.Start();
            _pool.AddPeer(peer);
            await Task.Delay(200);
            
            var allocation = _pool.Allocate();
            _pool.RemovePeer(peer);
            await Task.Delay(1000);
            
            Assert.AreEqual(null, allocation.Current);
            Assert.AreEqual(0, _pool.PeerCount);
        }
        
        [Test]
        public void Can_return()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            
            _pool.Start();
            _pool.AddPeer(peer);
            var allocation = _pool.Allocate();
            _pool.Free(allocation);
        }
    }
}