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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Test.Mocks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class SyncDispatcherTests
    {
        private class TestSyncPeerPool : ISyncPeerPool
        {
            public void Dispose()
            {
            }

            public Task<SyncPeerAllocation> Allocate(IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts contexts, int timeoutMilliseconds = 0)
            {
                ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
                syncPeer.ClientId.Returns("Nethermind");
                syncPeer.TotalDifficulty.Returns(UInt256.One);
                SyncPeerAllocation allocation = new SyncPeerAllocation(new PeerInfo(syncPeer), contexts);
                allocation.AllocateBestPeer(
                    Substitute.For<IEnumerable<PeerInfo>>(),
                    Substitute.For<INodeStatsManager>(),
                    Substitute.For<IBlockTree>());
                return Task.FromResult(allocation);
            }

            public void Free(SyncPeerAllocation syncPeerAllocation)
            {
            }

            public void ReportNoSyncProgress(PeerInfo peerInfo, AllocationContexts contexts)
            {
            }

            public void ReportBreachOfProtocol(PeerInfo peerInfo, string details)
            {
            }

            public void ReportWeakPeer(PeerInfo peerInfo, AllocationContexts contexts)
            {
            }

            public void WakeUpAll()
            {
                throw new NotImplementedException();
            }

            public IEnumerable<PeerInfo> AllPeers { get; } = Array.Empty<PeerInfo>();
            public IEnumerable<PeerInfo> InitializedPeers { get; } = Array.Empty<PeerInfo>();
            public int PeerCount { get; } = 0;
            public int InitializedPeersCount { get; } = 0;
            public int PeerMaxCount { get; } = 0;

            public void AddPeer(ISyncPeer syncPeer)
            {
            }

            public void RemovePeer(ISyncPeer syncPeer)
            {
            }

            public void RefreshTotalDifficulty(ISyncPeer syncPeer, Keccak hash)
            {
            }

            public void Start()
            {
            }

            public Task StopAsync()
            {
                return Task.CompletedTask;
            }

            public PeerInfo GetPeer(Node node)
            {
                return null;
            }

            public event EventHandler<PeerBlockNotificationEventArgs> NotifyPeerBlock;
        }

        private class TestBatch
        {
            public TestBatch(int start, int length)
            {
                Start = start;
                Length = length;
            }

            public int Start { get; }
            public int Length { get; }
            public int[] Result { get; set; }
        }

        private class TestDispatcher : SyncDispatcher<TestBatch>
        {
            public TestDispatcher(ISyncFeed<TestBatch> syncFeed, ISyncPeerPool syncPeerPool, IPeerAllocationStrategyFactory<TestBatch> peerAllocationStrategy)
                : base(syncFeed, syncPeerPool, peerAllocationStrategy, LimboLogs.Instance)
            {
            }

            private int _failureSwitch;

            protected override async Task Dispatch(PeerInfo allocation, TestBatch request, CancellationToken cancellationToken)
            {
                if (++_failureSwitch % 2 == 0)
                {
                    throw new Exception();
                }

                await Task.CompletedTask;
                Console.WriteLine("Setting result");
                int[] result = new int[request.Length];
                for (int i = 0; i < request.Length; i++)
                {
                    result[i] = request.Start + i;
                }

                request.Result = result;
                Console.WriteLine("Finished Execution");
            }
        }

        private class TestSyncFeed : SyncFeed<TestBatch>
        {
            public TestSyncFeed(bool isMultiFeed = true)
            {
                IsMultiFeed = isMultiFeed;
            }

            public const int Max = 64;

            private int _highestRequested;

            public HashSet<int> _results = new HashSet<int>();

            private ConcurrentQueue<TestBatch> _returned = new ConcurrentQueue<TestBatch>();

            public override SyncResponseHandlingResult HandleResponse(TestBatch response)
            {
                if (response.Result == null)
                {
                    Console.WriteLine("Handling failed response");
                    _returned.Enqueue(response);
                }
                else
                {
                    Console.WriteLine("Handling OK response");
                    for (int i = 0; i < response.Length; i++)
                    {
                        lock (_results)
                        {
                            _results.Add(response.Result[i]);
                        }
                    }
                }

                Console.WriteLine($"Decrementing Pending Requests {Interlocked.Decrement(ref _pendingRequests)}");
                return SyncResponseHandlingResult.OK;
            }

            public override bool IsMultiFeed { get; }
            public override AllocationContexts Contexts => AllocationContexts.All;

            private int _pendingRequests;

            public override async Task<TestBatch> PrepareRequest()
            {
                TestBatch testBatch;
                if (_returned.TryDequeue(out TestBatch returned))
                {
                    Console.WriteLine("Sending previously failed batch");
                    testBatch = returned;
                }
                else
                {
                    await Task.CompletedTask;

                    int start;

                    if (_highestRequested >= Max)
                    {
                        Console.WriteLine("Pending: " + _pendingRequests);
                        if (_pendingRequests == 0)
                        {
                            Console.WriteLine("Changing to finished");
                            Finish();
                        }

                        return null;
                    }

                    lock (_results)
                    {
                        start = _highestRequested;
                        _highestRequested += 8;
                    }

                    testBatch = new TestBatch(start, 8);
                }

                Console.WriteLine($"Incrementing Pending Requests {Interlocked.Increment(ref _pendingRequests)}");
                return testBatch;
            }
        }

        [Test, Timeout(3000)]
        public async Task Simple_test_sync()
        {
            TestSyncFeed syncFeed = new TestSyncFeed();
            TestDispatcher dispatcher = new TestDispatcher(syncFeed, new TestSyncPeerPool(), new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance));
            Task executorTask = dispatcher.Start(CancellationToken.None);
            syncFeed.Activate();
            await executorTask;
            for (int i = 0; i < TestSyncFeed.Max; i++)
            {
                syncFeed._results.Contains(i).Should().BeTrue(i.ToString());
            }
        }
    }
}
