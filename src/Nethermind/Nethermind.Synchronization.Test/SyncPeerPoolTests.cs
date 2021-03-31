//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class SyncPeerPoolTests
    {
        private class Context : IAsyncDisposable
        {
            public INodeStatsManager Stats;
            public IBlockTree BlockTree;
            public SyncPeerPool Pool;

            public Context()
            {
                BlockTree = Substitute.For<IBlockTree>();
                Stats = Substitute.For<INodeStatsManager>();
                Pool = new SyncPeerPool(BlockTree, Stats, 25, 50, LimboLogs.Instance);
            }

            public async ValueTask DisposeAsync()
            {
                await Pool.StopAsync();
            }
        }

        private class SimpleSyncPeerMock : ISyncPeer
        {
            public SimpleSyncPeerMock(PublicKey publicKey, string description = "simple mock")
            {
                Node = new Node(publicKey, "127.0.0.1", 30303);
                ClientId = description;
            }

            public Node Node { get; }
            public string ClientId { get; }
            public Keccak HeadHash { get; set; }
            public long HeadNumber { get; set; }
            public UInt256 TotalDifficulty { get; set; } = 1;
            public bool IsInitialized { get; set; }

            public bool DisconnectRequested { get; set; }

            public void Disconnect(DisconnectReason reason, string details)
            {
                DisconnectRequested = true;
            }

            public Task<BlockBody[]> GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
            {
                return Task.FromResult(Array.Empty<BlockBody>());
            }

            public Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
            {
                return Task.FromResult(Array.Empty<BlockHeader>());
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

                IsInitialized = true;
                return await Task.FromResult(Build.A.BlockHeader.TestObject);
            }

            public void NotifyOfNewBlock(Block block, SendBlockPriority priority)
            {
            }

            public PublicKey Id => Node.Id;

            public bool SendNewTransaction(Transaction transaction, bool isPriority) => true;

            public Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
            {
                return Task.FromResult(Array.Empty<TxReceipt[]>());
            }

            public Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
            {
                return Task.FromResult(Array.Empty<byte[]>());
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

            public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
            {
                throw new NotImplementedException();
            }

            public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
            {
                throw new NotImplementedException();
            }
        }

        [Test]
        public async Task Cannot_add_when_not_started()
        {
            await using Context ctx = new Context(); 
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(0, ctx.Pool.PeerCount);
                ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeys[i]));
            }
        }

        [Test]
        public async Task Will_disconnect_one_when_at_max()
        {
            await using Context ctx = new Context();
            var peers = await SetupPeers(ctx, 25);
            await WaitForPeersInitialization(ctx);
            ctx.Pool.DropUselessPeers(true);
            Assert.True(peers.Any(p => p.DisconnectRequested));
        }

        [Test]
        public async Task Cannot_remove_when_stopped()
        {
            await using Context ctx = new Context();
            ctx.Pool.Start();
            ISyncPeer[] syncPeers = new ISyncPeer[3];
            for (int i = 0; i < 3; i++)
            {
                syncPeers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
                ctx.Pool.AddPeer(syncPeers[i]);
            }

            await ctx.Pool.StopAsync();

            for (int i = 3; i > 0; i--)
            {
                Assert.AreEqual(3, ctx.Pool.PeerCount, $"Remove {i}");
                ctx.Pool.RemovePeer(syncPeers[i - 1]);
            }
        }

        [Test]
        public async Task Peer_count_is_valid_when_adding()
        {
            await using Context ctx = new Context();
            ctx.Pool.Start();
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(i, ctx.Pool.PeerCount);
                ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeys[i]));
            }
        }

        [Test]
        public async Task Does_not_crash_when_adding_twice_same_peer()
        {
            await using Context ctx = new Context();
            ctx.Pool.Start();
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));

            Assert.AreEqual(1, ctx.Pool.PeerCount);
        }

        [Test]
        public async Task Does_not_crash_when_removing_non_existing_peer()
        {
            await using Context ctx = new Context();
            ctx.Pool.Start();
            ctx.Pool.RemovePeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            Assert.AreEqual(0, ctx.Pool.PeerCount);
        }

        [Test]
        public async Task Peer_count_is_valid_when_removing()
        {
            await using Context ctx = new Context();
            ctx.Pool.Start();
            ISyncPeer[] syncPeers = new ISyncPeer[3];
            for (int i = 0; i < 3; i++)
            {
                syncPeers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
                ctx.Pool.AddPeer(syncPeers[i]);
            }

            for (int i = 3; i > 0; i--)
            {
                Assert.AreEqual(i, ctx.Pool.PeerCount, $"Remove {i}");
                ctx.Pool.RemovePeer(syncPeers[i - 1]);
            }
        }

        [Test]
        public async Task Can_start()
        {
            await using Context ctx = new Context();
            ctx.Pool.Start();
        }

        [Test]
        public async Task Can_start_and_stop()
        {
            await using Context ctx = new Context();
            ctx.Pool.Start();
            await ctx.Pool.StopAsync();
        }

        [Test, Retry(3)]
        public async Task Can_refresh()
        {
            await using Context ctx = new Context();
            ctx.Pool.Start();
            var syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            ctx.Pool.AddPeer(syncPeer);
            ctx.Pool.RefreshTotalDifficulty(syncPeer, null);
            await Task.Delay(100);

            await syncPeer.Received(2).GetHeadBlockHeader(Arg.Any<Keccak>(), Arg.Any<CancellationToken>());
        }

        private void SetupSpeedStats(Context ctx, PublicKey publicKey, int transferSpeed)
        {
            
            Node node = new Node(publicKey, "127.0.0.1", 30303);
            NodeStatsLight stats = new NodeStatsLight(node);
            stats.AddTransferSpeedCaptureEvent(TransferSpeedType.Headers, transferSpeed);

            ctx.Stats.GetOrAdd(Arg.Is<Node>(n => n.Id == publicKey)).Returns(stats);
        }

        [Test]
        public async Task Can_replace_peer_with_better()
        {
            await using Context ctx = new Context();
            SetupSpeedStats(ctx, TestItem.PublicKeyA, 50);
            SetupSpeedStats(ctx, TestItem.PublicKeyB, 100);

            ctx.Pool.Start();
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA, "A"));
            await WaitForPeersInitialization(ctx);
            var allocation = await ctx.Pool.Allocate(new BlocksSyncPeerAllocationStrategy(null));
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB, "B"));

            await WaitFor(() => replaced, "peer to get replaced");
            Assert.True(replaced);
        }

        [Test]
        public async Task Does_not_replace_with_a_worse_peer()
        {
            await using Context ctx = new Context();
            SetupSpeedStats(ctx, TestItem.PublicKeyA, 200);
            SetupSpeedStats(ctx, TestItem.PublicKeyB, 100);

            ctx.Pool.Start();
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await WaitForPeersInitialization(ctx);
            SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await WaitForPeersInitialization(ctx);

            Assert.False(replaced);
        }

        [Test]
        public async Task Does_not_replace_if_too_small_percentage_change()
        {
            await using Context ctx = new Context();
            SetupSpeedStats(ctx, TestItem.PublicKeyA, 91);
            SetupSpeedStats(ctx, TestItem.PublicKeyB, 100);

            ctx.Pool.Start();
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await WaitForPeersInitialization(ctx);
            SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await WaitForPeersInitialization(ctx);

            Assert.False(replaced);
        }

        [Test, Retry(3)]
        public async Task Does_not_replace_on_small_difference_in_low_numbers()
        {
            await using Context ctx = new Context();
            SetupSpeedStats(ctx, TestItem.PublicKeyA, 5);
            SetupSpeedStats(ctx, TestItem.PublicKeyB, 4);

            ctx.Pool.Start();
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await WaitForPeersInitialization(ctx);
            SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await WaitForPeersInitialization(ctx);

            Assert.False(replaced);
        }

        [Test]
        public async Task Can_stay_when_current_is_best()
        {
            await using Context ctx = new Context();
            SetupSpeedStats(ctx, TestItem.PublicKeyA, 100);
            SetupSpeedStats(ctx, TestItem.PublicKeyB, 100);

            ctx.Pool.Start();
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
            await WaitForPeersInitialization(ctx);
            SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            bool replaced = false;
            allocation.Replaced += (sender, args) => replaced = true;
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyB));
            await WaitForPeersInitialization(ctx);
            Assert.False(replaced);
        }

        [Test]
        public async Task Can_list_all_peers()
        {
            await using Context ctx = new Context();
            _ = await SetupPeers(ctx, 3);
            Assert.AreEqual(3, ctx.Pool.AllPeers.Count());
        }

        [Test]
        public async Task Can_borrow_peer()
        {
            await using Context ctx = new Context();
            var peers = await SetupPeers(ctx, 1);

            var allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));

            Assert.AreSame(peers[0], allocation.Current?.SyncPeer);
        }

        [Test]
        public async Task Can_borrow_return_and_borrow_again()
        {
            await using Context ctx = new Context();
            var peers = await SetupPeers(ctx, 1);

            var allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            ctx.Pool.Free(allocation);
            allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            ctx.Pool.Free(allocation);
            allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));

            Assert.AreSame(peers[0], allocation.Current?.SyncPeer);
        }

        [Test]
        public async Task Can_borrow_many()
        {
            await using Context ctx = new Context();
            await SetupPeers(ctx, 2);

            SyncPeerAllocation allocation1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            SyncPeerAllocation allocation2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            Assert.AreNotSame(allocation1.Current, allocation2.Current, "first");
            Assert.NotNull(allocation1.Current, "first A");
            Assert.NotNull(allocation2.Current, "first B");

            ctx.Pool.Free(allocation1);
            ctx.Pool.Free(allocation2);
            Assert.Null(allocation1.Current, "null A");
            Assert.Null(allocation2.Current, "null B");

            allocation1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            allocation2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            Assert.AreNotSame(allocation1.Current, allocation2.Current);
            Assert.NotNull(allocation1.Current, "second A");
            Assert.NotNull(allocation2.Current, "second B");
        }

        [Test]
        public async Task Does_not_allocate_sleeping_peers()
        {
            await using Context ctx = new Context();
            _ = await SetupPeers(ctx, 3);
            for (int i = 0; i < PeerInfo.SleepThreshold + 1; i++)
            {
                ctx.Pool.ReportNoSyncProgress(ctx.Pool.InitializedPeers.First(), AllocationContexts.All);
            }

            SyncPeerAllocation allocation1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            SyncPeerAllocation allocation2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            SyncPeerAllocation allocation3 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));

            Assert.True(allocation1.HasPeer);
            Assert.True(allocation2.HasPeer);
            Assert.False(allocation3.HasPeer);
        }

        [Test]
        public async Task Can_wake_up_all_sleeping_peers()
        {
            await using Context ctx = new Context();
            _ = await SetupPeers(ctx, 3);
            ctx.Pool.ReportNoSyncProgress(ctx.Pool.InitializedPeers.First(), AllocationContexts.All);
            ctx.Pool.ReportNoSyncProgress(ctx.Pool.InitializedPeers.Last(), AllocationContexts.All);

            ctx.Pool.WakeUpAll();

            SyncPeerAllocation allocation1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            SyncPeerAllocation allocation2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            SyncPeerAllocation allocation3 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));

            Assert.True(allocation1.HasPeer);
            Assert.True(allocation2.HasPeer);
            Assert.True(allocation3.HasPeer);
        }

        [Test]
        public async Task Initialized_peers()
        {
            await using Context ctx = new Context();
            _ = await SetupPeers(ctx, 3);
            Assert.AreEqual(3, ctx.Pool.InitializedPeers.Count());
        }

        [Test]
        public async Task Report_invalid_invokes_disconnection()
        {
            await using Context ctx = new Context();
            var peers = await SetupPeers(ctx, 3);
            var peerInfo = ctx.Pool.InitializedPeers.First();
            ctx.Pool.ReportBreachOfProtocol(peerInfo, "issue details");

            Assert.True(((SimpleSyncPeerMock)peerInfo.SyncPeer).DisconnectRequested);
        }

        [Test]
        public async Task Will_not_allocate_same_peer_to_two_allocations()
        {
            await using Context ctx = new Context();
            var peers = await SetupPeers(ctx, 1);

            var allocation1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            var allocation2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));

            Assert.AreSame(peers[0], allocation1.Current?.SyncPeer);
            Assert.Null(allocation2.Current);
        }

        [Test]
        public async Task Can_remove_borrowed_peer()
        {
            await using Context ctx = new Context();
            var peers = await SetupPeers(ctx, 1);

            var allocation = await ctx.Pool.Allocate(new BlocksSyncPeerAllocationStrategy(null));
            ctx.Pool.RemovePeer(peers[0]);

            Assert.Null(allocation.Current);
        }

        [Test]
        public async Task Will_remove_peer_if_times_out_on_init()
        {
            await using Context ctx = new Context();
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            peer.SetHeaderResponseTime(int.MaxValue);
            ctx.Pool.Start();
            ctx.Pool.AddPeer(peer);

            await WaitFor(() => peer.DisconnectRequested);
            Assert.True(peer.DisconnectRequested);
        }

        [Test]
        public async Task Can_remove_during_init()
        {
            await using Context ctx = new Context();
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            peer.SetHeaderResponseTime(500);
            ctx.Pool.Start();
            ctx.Pool.AddPeer(peer);

            var allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            ctx.Pool.RemovePeer(peer);

            Assert.AreEqual(null, allocation.Current);
            Assert.AreEqual(0, ctx.Pool.PeerCount);
        }

        [Test]
        public async Task It_is_fine_to_fail_init()
        {
            await using Context ctx = new Context();
            var peer = new SimpleSyncPeerMock(TestItem.PublicKeyA);
            peer.SetHeaderFailure(true);
            ctx.Pool.Start();
            ctx.Pool.AddPeer(peer);
            await WaitForPeersInitialization(ctx);

            var allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            ctx.Pool.RemovePeer(peer);

            Assert.AreEqual(null, allocation.Current);
            Assert.AreEqual(0, ctx.Pool.PeerCount);
        }

        [Test]
        public async Task Can_return()
        {
            await using Context ctx = new Context();
            await SetupPeers(ctx, 1);

            var allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            ctx.Pool.Free(allocation);
        }
        
        [Test]
        public async Task Does_not_fail_when_receiving_a_new_block_and_allocation_has_no_peer()
        {
            await using Context ctx = new Context();
            await SetupPeers(ctx, 1);

            var allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
            allocation.Cancel();

            ctx.BlockTree.NewHeadBlock += Raise.EventWith(new object(), new BlockEventArgs(Build.A.Block.WithTotalDifficulty(1L).TestObject));
        }

        [Test]
        public async Task Can_borrow_async_many()
        {
            await using Context ctx = new Context();
            await SetupPeers(ctx, 2);

            var allocationTasks = new Task<SyncPeerAllocation>[3];
            for (int i = 0; i < allocationTasks.Length; i++)
            {
                allocationTasks[i] = ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.All, 50);
            }

            await Task.WhenAll(allocationTasks);

            var allocations = allocationTasks.Select(t => t.Result).ToArray();
            var successfulAllocations = allocations.Where(r => r.Current != null).ToArray();

            // we had only two peers and 3 borrow calls so only two are successful
            Assert.AreEqual(2, successfulAllocations.Length);

            foreach (var allocation in successfulAllocations)
            {
                // free allocated peers
                ctx.Pool.Free(allocation);
            }

            foreach (SyncPeerAllocation allocation in allocations)
            {
                // no peer assigned any more after calling free
                Assert.Null(allocation.Current, "null A");
            }
        }

        private int _pendingRequests;

        private Random _workRandomDelay = new Random(42);

        private async Task DoWork(string desc, SyncPeerAllocation allocation)
        {
            await using Context ctx = new Context();
            if (allocation.HasPeer)
            {
                int workTime = _workRandomDelay.Next(1000);
                Console.WriteLine($"{desc} will work for {workTime} ms");
                await Task.Delay(workTime);
                Console.WriteLine($"{desc} finished work after {workTime} ms");
            }

            ctx.Pool.Free(allocation);
            Console.WriteLine($"{desc} freed allocation");
        }

        [Test, Retry(3)]
        public async Task Try_to_break_multithreaded()
        {
            await using Context ctx = new Context();
            await SetupPeers(ctx, 25);

            int failures = 0;
            int iterations = 100;
            do
            {
                if (iterations > 0)
                {
                    SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.All, 10);
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

                Console.WriteLine(iterations + " " + failures + " " + ctx.Pool.ReplaceableAllocations.Count() + " " + _pendingRequests);
                await Task.Delay(10);
            } while (iterations-- > 0 || _pendingRequests > 0);

            Assert.AreEqual(0, ctx.Pool.ReplaceableAllocations.Count(), "allocations");
            Assert.AreEqual(0, _pendingRequests, "pending requests");
            Assert.GreaterOrEqual(failures, 0, "pending requests");
        }

        private async Task<SimpleSyncPeerMock[]> SetupPeers(Context ctx, int count)
        {
            var peers = new SimpleSyncPeerMock[count];
            for (int i = 0; i < count; i++)
            {
                peers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
            }

            ctx.Pool.Start();

            for (int i = 0; i < count; i++)
            {
                ctx.Pool.AddPeer(peers[i]);
            }

            await WaitForPeersInitialization(ctx);
            return peers;
        }

        private async Task WaitForPeersInitialization(Context ctx)
        {
            await WaitFor(() => ctx.Pool.AllPeers.All(p => p.IsInitialized), "peers to initialize");
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
