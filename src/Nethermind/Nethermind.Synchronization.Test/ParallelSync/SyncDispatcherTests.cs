// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Test.Mocks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

[Parallelizable(ParallelScope.All)]
public class SyncDispatcherTests
{
    private class TestSyncPeerPool : ISyncPeerPool
    {
        private readonly SemaphoreSlim _peerSemaphore;
        private readonly Lock _lock = new Lock();

        public TestSyncPeerPool(int peerCount = 1)
        {
            _peerSemaphore = new SemaphoreSlim(peerCount, peerCount);
        }

        public async Task<SyncPeerAllocation> Allocate(
            IPeerAllocationStrategy peerAllocationStrategy,
            AllocationContexts contexts,
            int timeoutMilliseconds = 0,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await _peerSemaphore.WaitAsync(cancellationToken);
            ISyncPeer syncPeer = new MockSyncPeer("Nethermind", UInt256.One);
            SyncPeerAllocation allocation = new(new PeerInfo(syncPeer), contexts, _lock);
            return allocation;
        }

        private class MockSyncPeer(string clientId, UInt256 totalDifficulty) : BaseSyncPeerMock
        {
            public override string ClientId => clientId;
            public override UInt256 TotalDifficulty => totalDifficulty;
        }

        public void Free(SyncPeerAllocation syncPeerAllocation)
        {
            _peerSemaphore.Release();
        }

        public void ReportNoSyncProgress(PeerInfo peerInfo, AllocationContexts contexts)
        {
        }

        public void ReportBreachOfProtocol(PeerInfo peerInfo, DisconnectReason disconnectReason, string details)
        {
        }

        public void ReportWeakPeer(PeerInfo peerInfo, AllocationContexts contexts)
        {
        }

        public Task<int?> EstimateRequestLimit(RequestType bodies, IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts blocks,
            CancellationToken token)
        {
            return Task.FromResult<int?>(null);
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

        public void RefreshTotalDifficulty(ISyncPeer syncPeer, Hash256 hash)
        {
        }

        public void Start()
        {
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }

        public PeerInfo? GetPeer(Node node)
        {
            return null;
        }

        public event EventHandler<PeerBlockNotificationEventArgs> NotifyPeerBlock = static delegate { };

        public ValueTask DisposeAsync()
        {
            _peerSemaphore.Dispose();
            return ValueTask.CompletedTask;
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
        public int[]? Result { get; set; }
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
            int[] result = new int[request.Length];
            for (int i = 0; i < request.Length; i++)
            {
                result[i] = request.Start + i;
            }

            request.Result = result;
        }
    }

    private class TestSyncFeed : SyncFeed<TestBatch>
    {
        public TestSyncFeed(bool isMultiFeed = true, int max = 64)
        {
            IsMultiFeed = isMultiFeed;
            Max = max;
        }

        public int Max { get; }
        public int HighestRequested { get; private set; }

        public readonly HashSet<int> _results = new();
        private readonly ConcurrentQueue<TestBatch> _returned = new();
        private readonly ManualResetEvent _responseLock = new ManualResetEvent(true);
        private TaskCompletionSource _handleResponseCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void LockResponse()
        {
            _responseLock.Reset();
        }

        public void UnlockResponse()
        {
            _responseLock.Set();
        }

        public override SyncResponseHandlingResult HandleResponse(TestBatch response, PeerInfo? peer = null)
        {
            _handleResponseCalled.TrySetResult();
            _responseLock.WaitOne();
            if (response.Result is null)
            {
                _returned.Enqueue(response);
            }
            else
            {
                for (int i = 0; i < response.Length; i++)
                {
                    lock (_results)
                    {
                        _results.Add(response.Result[i]);
                    }
                }
            }

            Interlocked.Decrement(ref _pendingRequests);
            return SyncResponseHandlingResult.OK;
        }

        public Task WaitForHandleResponse()
        {
            return _handleResponseCalled.Task;
        }

        public override bool IsMultiFeed { get; }
        public override AllocationContexts Contexts => AllocationContexts.All;
        public override void SyncModeSelectorOnChanged(SyncMode current)
        {
        }

        public override bool IsFinished => false;

        private int _pendingRequests;

        public override async Task<TestBatch> PrepareRequest(CancellationToken token = default)
        {
            TestBatch testBatch;
            if (_returned.TryDequeue(out TestBatch? returned))
            {
                testBatch = returned;
            }
            else
            {
                await Task.CompletedTask;

                int start;

                if (HighestRequested >= Max)
                {
                    if (_pendingRequests == 0)
                    {
                        Finish();
                    }

                    return null!;
                }

                lock (_results)
                {
                    start = HighestRequested;
                    HighestRequested += 8;
                }

                testBatch = new TestBatch(start, 8);
            }

            Interlocked.Increment(ref _pendingRequests);
            return testBatch;
        }
    }

    [Test, MaxTime(10000)]
    public async Task Simple_test_sync()
    {
        TestSyncFeed syncFeed = new();
        TestDownloader downloader = new TestDownloader();
        SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig(),
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

    [Test]
    public async Task When_ConcurrentHandleResponseIsRunning_Then_BlockDispose()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        TestSyncFeed syncFeed = new(isMultiFeed: true);
        syncFeed.LockResponse();
        TestDownloader downloader = new TestDownloader();
        SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig(),
            syncFeed,
            downloader,
            new TestSyncPeerPool(),
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            LimboLogs.Instance);
        Task executorTask = dispatcher.Start(cts.Token);

        // Load some requests
        syncFeed.Activate();
        await syncFeed.WaitForHandleResponse();
        syncFeed.Finish();

        // Dispose
        Task disposeTask = Task.Run(async () =>
        {
            await dispatcher.DisposeAsync();
        });
        await Task.Delay(100, cts.Token);

        disposeTask.IsCompletedSuccessfully.Should().BeFalse();

        syncFeed.UnlockResponse();
        await disposeTask;
        await executorTask;
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
            new TestSyncConfig()
            {
                MaxProcessingThreads = processingThread,
            },
            syncFeed,
            downloader,
            new TestSyncPeerPool(peerCount),
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            LimboLogs.Instance);

        Task _ = dispatcher.Start(CancellationToken.None);
        syncFeed.Activate();

        await Task.Delay(100);

        Assert.That(() => syncFeed.HighestRequested, Is.EqualTo(expectedHighestRequest).After(4000, 100));
        syncFeed.UnlockResponse();
    }
}
