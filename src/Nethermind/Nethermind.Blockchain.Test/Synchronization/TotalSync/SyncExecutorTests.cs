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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Test.Synchronization.Mocks;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.TotalSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization.TotalSync
{
    [TestFixture]
    public class SyncExecutorTests
    {
        private class TestSyncPeerPool : ISyncPeerPool
        {
            public void Dispose()
            {
            }

            public bool TryFind(PublicKey nodeId, out PeerInfo peerInfo)
            {
                throw new NotImplementedException();
            }

            public Task<SyncPeerAllocation> Borrow(IPeerSelectionStrategy peerSelectionStrategy, string description = "", int timeoutMilliseconds = 0)
            {
                ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
                syncPeer.ClientId.Returns("Nethermind");
                syncPeer.TotalDifficultyOnSessionStart.Returns(UInt256.One);
                SyncPeerAllocation allocation = new SyncPeerAllocation(new PeerInfo(syncPeer));
                allocation.AllocateBestPeer(null, null, null);
                return Task.FromResult(allocation);
            }

            public void Free(SyncPeerAllocation syncPeerAllocation)
            {
            }

            public void ReportNoSyncProgress(PeerInfo peerInfo, bool isSevere = true)
            {
            }

            public void ReportInvalid(PeerInfo peerInfo, string details)
            {
            }

            public void ReportWeakPeer(PeerInfo peerInfo)
            {
            }

            public void ReportWeakPeer(SyncPeerAllocation allocation)
            {
            }

            public void WakeUpAll()
            {
                throw new NotImplementedException();
            }

            public IEnumerable<PeerInfo> AllPeers { get; }
            public IEnumerable<PeerInfo> UsefulPeers { get; }
            public int PeerCount { get; }
            public int UsefulPeerCount { get; }
            public int PeerMaxCount { get; }

            public void AddPeer(ISyncPeer syncPeer)
            {
            }

            public void RemovePeer(ISyncPeer syncPeer)
            {
            }

            public void RefreshTotalDifficulty(PeerInfo peerInfo, Keccak hash)
            {
            }

            public void Start()
            {
            }

            public Task StopAsync()
            {
                return Task.CompletedTask;
            }

            public event EventHandler PeerAdded
            {
                add { }
                remove { }
            }
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

        private class TestExecutor : SyncExecutor<TestBatch>
        {
            public TestExecutor(ISyncFeed<TestBatch> syncFeed, ISyncPeerPool syncPeerPool, IPeerSelectionStrategyFactory<TestBatch> peerSelectionStrategy)
                : base(syncFeed, syncPeerPool, peerSelectionStrategy, LimboLogs.Instance)
            {
            }

            private int _failureSwitch;

            protected override async Task Execute(PeerInfo allocation, TestBatch request, CancellationToken cancellationToken)
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

            public override SyncBatchResponseHandlingResult HandleResponse(TestBatch response)
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

                Console.WriteLine("Decrementing");
                Interlocked.Decrement(ref _pendingRequests);
                return SyncBatchResponseHandlingResult.OK;
            }

            public override bool IsMultiFeed { get; }

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

                Console.WriteLine("Incrementing");
                Interlocked.Increment(ref _pendingRequests);
                return testBatch;
            }
        }

        [Test, Timeout(3000)]
        public async Task Simple_test_sync()
        {
            TestSyncFeed syncFeed = new TestSyncFeed();
            TestExecutor executor = new TestExecutor(syncFeed, new TestSyncPeerPool(), new StaticPeerSelectionStrategyFactory<TestBatch>(new FirstFree()));
            Task executorTask = executor.Start(CancellationToken.None);
            syncFeed.Activate();
            await executorTask;
            for (int i = 0; i < TestSyncFeed.Max; i++)
            {
                syncFeed._results.Contains(i).Should().BeTrue(i.ToString());
            }
        }
    }
}