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
using System.Configuration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
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
            _pool = new EthSyncPeerPool(_blockTree, _stats, new SyncConfig(), 25, LimboLogs.Instance);
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

            public bool DisconnectRequested { get; set; }

            public void Disconnect(DisconnectReason reason, string details)
            {
                DisconnectRequested = true;
            }

            public Task<BlockBody[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
            {
                return Task.FromResult(new BlockBody[0]);
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

            public Task<TxReceipt[][]> GetReceipts(Keccak[] blockHash, CancellationToken token)
            {
                return Task.FromResult(new TxReceipt[0][]);
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
            SyncPeerAllocation allocation = _pool.Borrow();
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
            SyncPeerAllocation allocation = _pool.Borrow();
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
            SyncPeerAllocation allocation = _pool.Borrow();
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
            SyncPeerAllocation allocation = _pool.Borrow();
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
            SyncPeerAllocation allocation = _pool.Borrow();
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

            var allocation = _pool.Borrow();

            Assert.AreSame(peer, allocation.Current?.SyncPeer);
        }

        [Test]
        public async Task Can_borrow_return_and_borrow_again()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);

            _pool.Start();
            _pool.AddPeer(peer);
            await Task.Delay(200);

            var allocation = _pool.Borrow();
            _pool.Free(allocation);
            allocation = _pool.Borrow();
            _pool.Free(allocation);
            allocation = _pool.Borrow();

            Assert.AreSame(peer, allocation.Current?.SyncPeer);
        }

        [Test]
        public async Task Can_borrow_many()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            var peer2 = new SimpleSyncPeerMock(TestItem.PublicKeyB);

            _pool.Start();
            _pool.AddPeer(peer);
            _pool.AddPeer(peer2);
            await Task.Delay(200);

            var allocation1 = _pool.Borrow();
            var allocation2 = _pool.Borrow();
            Assert.AreNotSame(allocation1.Current, allocation2.Current, "first");
            Assert.NotNull(allocation1.Current, "first A");
            Assert.NotNull(allocation2.Current, "first B");

            _pool.Free(allocation1);
            _pool.Free(allocation2);
            Assert.Null(allocation1.Current, "null A");
            Assert.Null(allocation2.Current, "null B");

            allocation1 = _pool.Borrow();
            allocation2 = _pool.Borrow();
            Assert.AreNotSame(allocation1.Current, allocation2.Current);
            Assert.NotNull(allocation1.Current, "second A");
            Assert.NotNull(allocation2.Current, "second B");
        }

        [Test]
        public async Task Will_not_allocate_same_peer_to_two_allocations()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);

            _pool.Start();
            _pool.AddPeer(peer);
            await Task.Delay(200);

            var allocation1 = _pool.Borrow();
            var allocation2 = _pool.Borrow();

            Assert.AreSame(peer, allocation1.Current?.SyncPeer);
            Assert.Null(allocation2.Current);
        }

        [Test]
        public async Task Can_remove_borrowed_peer()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);

            _pool.Start();
            _pool.AddPeer(peer);
            await Task.Delay(200);

            var allocation = _pool.Borrow();
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


            var allocation = _pool.Borrow();

            Assert.AreEqual(null, allocation.Current);
            Assert.True(peer.DisconnectRequested);
        }

        [Test]
        public async Task Can_remove_during_init()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            peer.SetHeaderResponseTime(1000);

            _pool.Start();
            _pool.AddPeer(peer);
            await Task.Delay(200);

            var allocation = _pool.Borrow();
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

            var allocation = _pool.Borrow();
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
            var allocation = _pool.Borrow();
            _pool.Free(allocation);
        }

        [Test]
        public void Report_no_sync_progress_on_null_does_not_crash()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);

            _pool.Start();
            _pool.AddPeer(peer);

            _pool.ReportNoSyncProgress((SyncPeerAllocation) null);
            _pool.ReportNoSyncProgress((PeerInfo) null);
        }

        [Test]
        public void Does_not_fail_when_receiving_a_new_block_and_allocation_has_no_peer()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            
            _pool.Start();
            _pool.AddPeer(peer);
            var allocation = _pool.Borrow();
            allocation.Cancel();

            _blockTree.NewHeadBlock += Raise.EventWith(new object(), new BlockEventArgs(Build.A.Block.WithTotalDifficulty(1).TestObject));
        }
    }
}