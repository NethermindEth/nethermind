//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
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
        private IBlockTree _blockTree;
        private EthSyncPeerPool _pool;

        [SetUp]
        public void SetUp()
        {
            _blockTree = Substitute.For<IBlockTree>();
            _stats = Substitute.For<INodeStatsManager>();
            _pool = new EthSyncPeerPool(_blockTree, _stats, new SyncConfig(), 25, 50, LimboLogs.Instance);
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

            public Task<BlockBody[]> GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
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
                    throw new Exception("Failed");
                }

                if (_shouldTimeout)
                {
                    throw new TimeoutException("Timed out");
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

            public Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
            {
                return Task.FromResult(new TxReceipt[0][]);
            }

            public Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
            {
                return Task.FromResult(new byte[0][]);
            }

            private int? _headerResponseTime;

            private bool _shouldFail;

            private bool _shouldTimeout;

            public void SetHeaderResponseTime(int responseTime)
            {
                if (responseTime > 5000)
                {
                    _shouldTimeout = true;
                }

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
        public async Task Will_disconnect_one_when_at_max()
        {
            var peers = await SetupPeers(25);
            await WaitForPeersInitialization();
            _pool.DropUselessPeers(true);
            Assert.True(peers.Any(p => p.DisconnectRequested));
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
        public void Ensure_best_does_not_throw_on_no_allocations()
        {
            _pool.EnsureBest();
            _pool.Start();
            _pool.EnsureBest();
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

        [Test, Retry(3)]
        public async Task Can_refresh()
        {
            _pool.Start();
            var syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            _pool.AddPeer(syncPeer);
            _pool.Refresh(TestItem.PublicKeyA);
            await Task.Delay(100);

            await syncPeer.Received(2).GetHeadBlockHeader(Arg.Any<Keccak>(), Arg.Any<CancellationToken>());
        }

        private void SetupSpeedStats(PublicKey publicKey, int milliseconds)
        {
            Node node = new Node(publicKey, "127.0.0.1", 30303);
            NodeStatsLight stats = new NodeStatsLight(node, new StatsConfig());
            stats.AddTransferSpeedCaptureEvent(milliseconds);

            _stats.GetOrAdd(Arg.Is<Node>(n => n.Id == publicKey)).Returns(stats);
        }

        [Test]
        public async Task Can_replace_peer_with_better()
        {
            SetupSpeedStats(TestItem.PublicKeyA, 50);
            SetupSpeedStats(TestItem.PublicKeyB, 100);

            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA, "A"));
            await WaitForPeersInitialization();
            SyncPeerAllocation allocation = await _pool.BorrowAsync();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB, "B"));

            await WaitFor(() => replaced, "peer to get replaced");
            Assert.True(replaced);
        }

        [Test]
        public async Task Does_not_replace_with_a_worse_peer()
        {
            SetupSpeedStats(TestItem.PublicKeyA, 200);
            SetupSpeedStats(TestItem.PublicKeyB, 100);

            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await WaitForPeersInitialization();
            SyncPeerAllocation allocation = await _pool.BorrowAsync();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await WaitForPeersInitialization();

            Assert.False(replaced);
        }

        [Test]
        public async Task Does_not_replace_if_too_small_percentage_change()
        {
            SetupSpeedStats(TestItem.PublicKeyA, 91);
            SetupSpeedStats(TestItem.PublicKeyB, 100);

            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await WaitForPeersInitialization();
            SyncPeerAllocation allocation = await _pool.BorrowAsync();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await WaitForPeersInitialization();

            Assert.False(replaced);
        }

        [Test, Retry(3)]
        public async Task Does_not_replace_on_small_difference_in_low_numbers()
        {
            SetupSpeedStats(TestItem.PublicKeyA, 5);
            SetupSpeedStats(TestItem.PublicKeyB, 4);

            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await WaitForPeersInitialization();
            SyncPeerAllocation allocation = await _pool.BorrowAsync();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await WaitForPeersInitialization();

            Assert.False(replaced);
        }

        [Test]
        public async Task Can_stay_when_current_is_best()
        {
            SetupSpeedStats(TestItem.PublicKeyA, 100);
            SetupSpeedStats(TestItem.PublicKeyB, 100);

            _pool.Start();
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await WaitForPeersInitialization();
            SyncPeerAllocation allocation = await _pool.BorrowAsync();
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            _pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await WaitForPeersInitialization();
            Assert.False(replaced);
        }

        [Test]
        public async Task Can_list_all_peers()
        {
            var peers = await SetupPeers(3);

            Assert.AreEqual(3, _pool.AllPeers.Count());
        }

        [Test]
        public async Task Can_borrow_peer()
        {
            var peers = await SetupPeers(1);

            var allocation = await _pool.BorrowAsync();

            Assert.AreSame(peers[0], allocation.Current?.SyncPeer);
        }

        [Test]
        public async Task Can_borrow_return_and_borrow_again()
        {
            var peers = await SetupPeers(1);

            var allocation = await _pool.BorrowAsync();
            _pool.Free(allocation);
            allocation = await _pool.BorrowAsync();
            _pool.Free(allocation);
            allocation = await _pool.BorrowAsync();

            Assert.AreSame(peers[0], allocation.Current?.SyncPeer);
        }

        [Test]
        public async Task Can_borrow_many()
        {
            await SetupPeers(2);

            SyncPeerAllocation allocation1 = await _pool.BorrowAsync();
            SyncPeerAllocation allocation2 = await _pool.BorrowAsync();
            Assert.AreNotSame(allocation1.Current, allocation2.Current, "first");
            Assert.NotNull(allocation1.Current, "first A");
            Assert.NotNull(allocation2.Current, "first B");

            _pool.Free(allocation1);
            _pool.Free(allocation2);
            Assert.Null(allocation1.Current, "null A");
            Assert.Null(allocation2.Current, "null B");

            allocation1 = await _pool.BorrowAsync();
            allocation2 = await _pool.BorrowAsync();
            Assert.AreNotSame(allocation1.Current, allocation2.Current);
            Assert.NotNull(allocation1.Current, "second A");
            Assert.NotNull(allocation2.Current, "second B");
        }
        
        [Test]
        public async Task Does_not_allocate_sleeping_peers()
        {
            var peers = await SetupPeers(3);
            _pool.ReportNoSyncProgress(_pool.AllPeers.First());

            SyncPeerAllocation allocation1 = await _pool.BorrowAsync();
            SyncPeerAllocation allocation2 = await _pool.BorrowAsync();
            SyncPeerAllocation allocation3 = await _pool.BorrowAsync();
            
            Assert.True(allocation1.HasPeer);
            Assert.True(allocation2.HasPeer);
            Assert.False(allocation3.HasPeer);
        }
        
        [Test]
        public async Task Can_wake_up_all_sleeping_peers()
        {
            var peers = await SetupPeers(3);
            _pool.ReportNoSyncProgress(_pool.AllPeers.First());
            _pool.ReportNoSyncProgress(_pool.AllPeers.Last());

            _pool.WakeUpAll();
            
            SyncPeerAllocation allocation1 = await _pool.BorrowAsync();
            SyncPeerAllocation allocation2 = await _pool.BorrowAsync();
            SyncPeerAllocation allocation3 = await _pool.BorrowAsync();

            Assert.True(allocation1.HasPeer);
            Assert.True(allocation2.HasPeer);
            Assert.True(allocation3.HasPeer);
        }
        
        [Test]
        public async Task Useful_peers_does_not_return_sleeping_peers()
        {
            var peers = await SetupPeers(3);
            _pool.ReportNoSyncProgress(_pool.AllPeers.First());
            _pool.ReportNoSyncProgress(_pool.AllPeers.Last());

            Assert.AreEqual(1, _pool.UsefulPeers.Count());
        }
        
        [Test]
        public async Task Report_invalid_invokes_disconnection()
        {
            var peers = await SetupPeers(3);
            _pool.ReportInvalid(_pool.AllPeers.First(), "issue details");

            Assert.True(peers[0].DisconnectRequested);
        }
        
        [Test]
        public async Task Report_invalid_via_allocation_invokes_disconnection()
        {
            var peers = await SetupPeers(3);
            var allocation = await _pool.BorrowAsync(BorrowOptions.DoNotReplace);
            _pool.ReportInvalid(allocation, "issue details");

            Assert.True(peers[0].DisconnectRequested);
        }
        
        [Test]
        public async Task Report_bad_peer_only_disconnects_after_11_times()
        {
            var peers = await SetupPeers(1);
            var allocation = await _pool.BorrowAsync(BorrowOptions.DoNotReplace);
            
            for (int i = 0; i < 10; i++)
            {
                _pool.ReportBadPeer(allocation);
                Assert.False(peers[0].DisconnectRequested);
            }
            
            _pool.ReportBadPeer(allocation);
            Assert.True(peers[0].DisconnectRequested);
        }

        [Test]
        public async Task Will_not_allocate_same_peer_to_two_allocations()
        {
            var peers = await SetupPeers(1);

            var allocation1 = await _pool.BorrowAsync();
            var allocation2 = await _pool.BorrowAsync();

            Assert.AreSame(peers[0], allocation1.Current?.SyncPeer);
            Assert.Null(allocation2.Current);
        }

        [Test]
        public async Task Can_remove_borrowed_peer()
        {
            var peers = await SetupPeers(1);

            var allocation = await _pool.BorrowAsync();
            _pool.RemovePeer(peers[0]);

            Assert.Null(allocation.Current);
        }

        [Test]
        public async Task Will_remove_peer_if_times_out_on_init()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            peer.SetHeaderResponseTime(int.MaxValue);
            _pool.Start();
            _pool.AddPeer(peer);

            await WaitFor(() => peer.DisconnectRequested);
            Assert.True(peer.DisconnectRequested);
        }

        [Test]
        public async Task Can_remove_during_init()
        {
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            peer.SetHeaderResponseTime(500);
            _pool.Start();
            _pool.AddPeer(peer);

            var allocation = await _pool.BorrowAsync();
            _pool.RemovePeer(peer);

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
            await WaitForPeersInitialization();

            var allocation = await _pool.BorrowAsync();
            _pool.RemovePeer(peer);

            Assert.AreEqual(null, allocation.Current);
            Assert.AreEqual(0, _pool.PeerCount);
        }

        [Test]
        public async Task Can_return()
        {
            await SetupPeers(1);

            var allocation = await _pool.BorrowAsync();
            _pool.Free(allocation);
        }

        [Test]
        public async Task Report_no_sync_progress_on_null_does_not_crash()
        {
            await SetupPeers(1);

            _pool.ReportNoSyncProgress((SyncPeerAllocation) null);
            _pool.ReportNoSyncProgress((PeerInfo) null);
        }

        [Test]
        public async Task Does_not_fail_when_receiving_a_new_block_and_allocation_has_no_peer()
        {
            await SetupPeers(1);

            var allocation = await _pool.BorrowAsync();
            allocation.Cancel();

            _blockTree.NewHeadBlock += Raise.EventWith(new object(), new BlockEventArgs(Build.A.Block.WithTotalDifficulty(1).TestObject));
        }

        [Test]
        public async Task Can_borrow_async_many()
        {
            await SetupPeers(2);

            var allocationTasks = new Task<SyncPeerAllocation>[3];
            for (int i = 0; i < allocationTasks.Length; i++)
            {
                allocationTasks[i] = _pool.BorrowAsync(BorrowOptions.None, string.Empty, null, 50);
            }

            await Task.WhenAll(allocationTasks);

            var allocations = allocationTasks.Select(t => t.Result).ToArray();
            var successfulAllocations = allocations.Where(r => r.Current != null).ToArray();

            // we had only two peers and 3 borrow calls so only two are successful
            Assert.AreEqual(2, successfulAllocations.Length);

            foreach (var allocation in successfulAllocations)
            {
                // free allocated peers
                _pool.Free(allocation);
            }

            foreach (SyncPeerAllocation allocation in allocations)
            {
                // no peer assigned any more after calling free
                Assert.Null(allocation.Current, "null A");
            }
        }

        int _pendingRequests = 0;

        private Random _workRandomDelay = new Random(42);

        private async Task DoWork(string desc, SyncPeerAllocation allocation)
        {
            if (allocation.HasPeer)
            {
                int workTime = _workRandomDelay.Next(1000);
                Console.WriteLine($"{desc} will work for {workTime} ms");
                await Task.Delay(workTime);
                Console.WriteLine($"{desc} finished work after {workTime} ms");
            }

            _pool.Free(allocation);
            Console.WriteLine($"{desc} freed allocation");
        }

        [Test, Retry(3)]
        public async Task Try_to_break_multithreaded()
        {
            await SetupPeers(25);

            int failures = 0;
            int iterations = 100;
            do
            {
                if (iterations > 0)
                {
                    SyncPeerAllocation allocation = await _pool.BorrowAsync(BorrowOptions.None, string.Empty, null, 10);
                    if (!allocation.HasPeer)
                    {
                        failures++;
                    }

                    Interlocked.Increment(ref _pendingRequests);
                    int iterationsLocal = iterations;


                    Task task = DoWork(iterationsLocal.ToString(), allocation);
#pragma warning disable 4014
                    task.ContinueWith(t =>
#pragma warning restore 4014
                    {
                        Console.WriteLine($"{iterationsLocal} Decrement on {t.IsCompleted}");
                        Interlocked.Decrement(ref _pendingRequests);
                    });
                }

                Console.WriteLine(iterations + " " + failures + " " + _pool.Allocations.Count() + " " + _pendingRequests);
                await Task.Delay(10);
            } while (iterations-- > 0 || _pendingRequests > 0);

            Assert.AreEqual(0, _pool.Allocations.Count(), "allocations");
            Assert.AreEqual(0, _pendingRequests, "pending requests");
            Assert.GreaterOrEqual(failures, 0, "pending requests");
        }

        private async Task<SimpleSyncPeerMock[]> SetupPeers(int count)
        {
            var peers = new SimpleSyncPeerMock[count];
            for (int i = 0; i < count; i++)
            {
                peers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
            }

            _pool.Start();

            for (int i = 0; i < count; i++)
            {
                _pool.AddPeer(peers[i]);
            }

            await WaitForPeersInitialization();
            return peers;
        }

        private async Task WaitForPeersInitialization()
        {
            await WaitFor(() => _pool.AllPeers.All(p => p.IsInitialized), "peers to initialize");
        }

        private async Task WaitFor(Func<bool> isConditionMet, string description = "condition to be met")
        {
            const int waitInterval = 10;
            for (int i = 0; i < 10; i++)
            {
                if (isConditionMet())
                {
                    return;
                }

                TestContext.WriteLine($"({i}) Waiting {waitInterval} for {description}");
                await Task.Delay(waitInterval);
            }
        }
    }
}