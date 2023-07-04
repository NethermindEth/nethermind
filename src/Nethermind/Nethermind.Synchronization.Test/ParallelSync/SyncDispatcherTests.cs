// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
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
            private readonly SemaphoreSlim _peerSemaphore;

            public TestSyncPeerPool(int peerCount = 1)
            {
                _peerSemaphore = new SemaphoreSlim(peerCount, peerCount);
            }

            public void Dispose()
            {
            }

            public async Task<SyncPeerAllocation> Allocate(IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts contexts, int timeoutMilliseconds = 0)
            {
                await _peerSemaphore.WaitAsync();
                ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
                syncPeer.ClientId.Returns("Nethermind");
                syncPeer.TotalDifficulty.Returns(UInt256.One);
                SyncPeerAllocation allocation = new(new PeerInfo(syncPeer), contexts);
                allocation.AllocateBestPeer(
                    Substitute.For<IEnumerable<PeerInfo>>(),
                    Substitute.For<INodeStatsManager>(),
                    Substitute.For<IBlockTree>());
                return allocation;
            }

            public void Free(SyncPeerAllocation syncPeerAllocation)
            {
                _peerSemaphore.Release();
            }

            public void ReportNoSyncProgress(PeerInfo peerInfo, AllocationContexts contexts)
            {
            }

            public void ReportBreachOfProtocol(PeerInfo peerInfo, InitiateDisconnectReason initiateDisconnectReason, string details)
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

            public void SetPeerPriority(PublicKey id)
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
            public event EventHandler<PeerHeadRefreshedEventArgs> PeerRefreshed;
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

        private class TestDownloader : ISyncDownloader<TestBatch>
        {
            private int _failureSwitch;
            public async Task Dispatch(PeerInfo peerInfo, TestBatch request, CancellationToken cancellationToken)
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
            public TestSyncFeed(bool isMultiFeed = true, int max = 64)
            {
                IsMultiFeed = isMultiFeed;
                Max = max;
            }

            public int Max;

            public int HighestRequested;

            public HashSet<int> _results = new();

            private ConcurrentQueue<TestBatch> _returned = new();

            private ManualResetEvent _responseLock = new ManualResetEvent(true);

            public void LockResponse()
            {
                _responseLock.Reset();
            }

            public void UnlockResponse()
            {
                _responseLock.Set();
            }

            public override SyncResponseHandlingResult HandleResponse(TestBatch response, PeerInfo peer = null)
            {
                _responseLock.WaitOne();
                if (response.Result is null)
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

            public override async Task<TestBatch> PrepareRequest(CancellationToken token = default)
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

                    if (HighestRequested >= Max)
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
                        start = HighestRequested;
                        HighestRequested += 8;
                    }

                    testBatch = new TestBatch(start, 8);
                }

                Console.WriteLine($"Incrementing Pending Requests {Interlocked.Increment(ref _pendingRequests)}");
                return testBatch;
            }
        }

        [Test, Timeout(10000)]
        public async Task Simple_test_sync()
        {
            TestSyncFeed syncFeed = new();
            TestDownloader downloader = new TestDownloader();
            SyncDispatcher<TestBatch> dispatcher = new(
                0,
                syncFeed,
                downloader,
                new TestSyncPeerPool(),
                new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
                LimboLogs.Instance);
            Task executorTask = dispatcher.Start(CancellationToken.None);
            syncFeed.Activate();
            await executorTask;
            for (int i = 0; i < syncFeed.Max; i++)
            {
                syncFeed._results.Contains(i).Should().BeTrue(i.ToString());
            }
        }

        [Retry(tryCount: 5)]
        [TestCase(false, 1, 1, 8)]
        [TestCase(true, 1, 1, 24)]
        [TestCase(true, 2, 1, 32)]
        [TestCase(true, 1, 2, 32)]
        public async Task Test_release_before_processing_complete(bool isMultiSync, int processingThread, int peerCount, int expectedHighestRequest)
        {
            TestSyncFeed syncFeed = new(isMultiSync, 999999);
            syncFeed.LockResponse();

            TestDownloader downloader = new TestDownloader();
            SyncDispatcher<TestBatch> dispatcher = new(
                processingThread,
                syncFeed,
                downloader,
                new TestSyncPeerPool(peerCount),
                new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
                LimboLogs.Instance);

            Task executorTask = dispatcher.Start(CancellationToken.None);
            syncFeed.Activate();
            await Task.Delay(100);

            Assert.That(() => syncFeed.HighestRequested, Is.EqualTo(expectedHighestRequest).After(2000, 100));
            syncFeed.UnlockResponse();
        }
    }
}
