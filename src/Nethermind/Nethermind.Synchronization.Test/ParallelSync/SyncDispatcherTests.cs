// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Test.Mocks;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

[Parallelizable(ParallelScope.All)]
public class SyncDispatcherTests
{
    private class TestSyncPeerPool(int peerCount = 1) : ISyncPeerPool
    {
        private readonly SemaphoreSlim _peerSemaphore = new(peerCount, peerCount);
        private readonly Lock _lock = new();

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
            public override UInt256? TotalDifficulty => totalDifficulty;
        }

        public void Free(SyncPeerAllocation syncPeerAllocation) =>
            _peerSemaphore.Release();

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
            CancellationToken token) =>
            Task.FromResult<int?>(null);

        public void WakeUpAll() =>
            throw new NotImplementedException();

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

        public Task StopAsync() =>
            Task.CompletedTask;

        public PeerInfo? GetPeer(Node node) =>
            null;

        public event EventHandler<PeerBlockNotificationEventArgs> NotifyPeerBlock = static delegate { };

        public ValueTask DisposeAsync()
        {
            _peerSemaphore.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private class TestBatch(int start, int length)
    {
        public int Start { get; } = start;
        public int Length { get; } = length;
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

    private sealed class BlockingSimpleDownloader(int expectedConcurrentDispatches) : ISyncDownloader<TestBatch>
    {
        private readonly TaskCompletionSource _expectedConcurrentDispatchesReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _currentDispatches;
        private int _maxConcurrentDispatches;

        public int MaxConcurrentDispatches => Volatile.Read(ref _maxConcurrentDispatches);

        public async Task Dispatch(PeerInfo peerInfo, TestBatch request, CancellationToken cancellationToken)
        {
            int currentDispatches = Interlocked.Increment(ref _currentDispatches);
            UpdateMax(currentDispatches);
            if (currentDispatches == expectedConcurrentDispatches)
            {
                _expectedConcurrentDispatchesReached.TrySetResult();
            }

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                int[] result = new int[request.Length];
                for (int i = 0; i < request.Length; i++)
                {
                    result[i] = request.Start + i;
                }

                request.Result = result;
            }
            finally
            {
                Interlocked.Decrement(ref _currentDispatches);
            }
        }

        public Task WaitForExpectedConcurrentDispatches(CancellationToken cancellationToken) =>
            _expectedConcurrentDispatchesReached.Task.WaitAsync(cancellationToken);

        public void Release() =>
            _release.TrySetResult();

        private void UpdateMax(int currentDispatches)
        {
            int currentMax;
            do
            {
                currentMax = Volatile.Read(ref _maxConcurrentDispatches);
                if (currentDispatches <= currentMax)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref _maxConcurrentDispatches, currentDispatches, currentMax) != currentMax);
        }
    }

    private class TestSyncFeed(bool isMultiFeed = true, int max = 64) : SyncFeed<TestBatch>
    {
        public int Max { get; } = max;
        public int HighestRequested { get; private set; }

        public readonly HashSet<int> _results = [];
        private readonly ConcurrentQueue<TestBatch> _returned = new();
        private readonly ManualResetEvent _responseLock = new(true);
        private readonly TaskCompletionSource _handleResponseCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _watcherLock = new();
        private int _watchedTarget = int.MaxValue;
        private TaskCompletionSource? _watchedReached;

        public void LockResponse() =>
            _responseLock.Reset();

        public void UnlockResponse() =>
            _responseLock.Set();

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

        public Task WaitForHandleResponse() =>
            _handleResponseCalled.Task;

        public override bool IsMultiFeed { get; } = isMultiFeed;
        public override AllocationContexts Contexts => AllocationContexts.All;
        public override void SyncModeSelectorOnChanged(SyncMode current)
        {
        }

        public override bool IsFinished => false;
        public override string FeedName => nameof(TestSyncFeed);

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

                lock (_watcherLock)
                {
                    if (HighestRequested >= _watchedTarget)
                    {
                        _watchedReached?.TrySetResult();
                    }
                }

                testBatch = new TestBatch(start, 8);
            }

            Interlocked.Increment(ref _pendingRequests);
            return testBatch;
        }

        public Task WaitForHighestRequested(int target)
        {
            lock (_watcherLock)
            {
                if (HighestRequested >= target) return Task.CompletedTask;
                _watchedTarget = target;
                _watchedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _watchedReached.Task;
            }
        }
    }

    private sealed class SimpleTestFeed(int maxRequests) : ISimpleSyncFeed<TestBatch>
    {
        private readonly HashSet<int> _results = [];
        private int _preparedRequests;
        private int _pendingRequests;

        public int ResultCount
        {
            get
            {
                lock (_results)
                {
                    return _results.Count;
                }
            }
        }

        public async Task<TestBatch?> PrepareRequest(CancellationToken token)
        {
            int requestNumber = Interlocked.Increment(ref _preparedRequests);
            if (requestNumber <= maxRequests)
            {
                Interlocked.Increment(ref _pendingRequests);
                return new TestBatch((requestNumber - 1) * 8, 8);
            }

            while (Volatile.Read(ref _pendingRequests) > 0)
            {
                await Task.Delay(10, token);
            }

            return null;
        }

        public SyncResponseHandlingResult HandleResponse(TestBatch response, PeerInfo? peer = null)
        {
            if (response.Result is not null)
            {
                lock (_results)
                {
                    for (int i = 0; i < response.Result.Length; i++)
                    {
                        _results.Add(response.Result[i]);
                    }
                }
            }

            Interlocked.Decrement(ref _pendingRequests);
            return SyncResponseHandlingResult.OK;
        }
    }

    [Test, NonParallelizable, MaxTime(30_000)]
    public async Task Simple_test_sync()
    {
        TestSyncFeed syncFeed = new();
        TestDownloader downloader = new();
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
            Assert.That(syncFeed._results.Contains(i), Is.True, i.ToString());
        }
    }

    [Test, NonParallelizable, CancelAfter(30_000)]
    public async Task SimpleDispatcher_uses_configured_max_concurrency(CancellationToken cancellationToken)
    {
        const int MaxConcurrency = 4;
        SimpleTestFeed syncFeed = new(maxRequests: MaxConcurrency);
        BlockingSimpleDownloader downloader = new(expectedConcurrentDispatches: MaxConcurrency);
        SimpleDispatcher<TestBatch> dispatcher = new(
            syncFeed,
            downloader,
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            AllocationContexts.All,
            new TestSyncPeerPool(peerCount: MaxConcurrency),
            new TestSyncConfig
            {
                MaxProcessingThreads = 1,
            },
            LimboLogs.Instance,
            maxConcurrency: MaxConcurrency);

        Task executorTask = dispatcher.Run(cancellationToken);

        await downloader.WaitForExpectedConcurrentDispatches(cancellationToken);
        Assert.That(downloader.MaxConcurrentDispatches, Is.EqualTo(MaxConcurrency));

        downloader.Release();
        await executorTask.WaitAsync(cancellationToken);

        Assert.That(syncFeed.ResultCount, Is.EqualTo(MaxConcurrency * 8));
    }

    [TestCase(0, 8, 8, 32)]
    [TestCase(0, 16, 8, 32)]
    [TestCase(0, 8, 64, 32)]
    [TestCase(0, 64, 8, 64)]
    [TestCase(24, 8, 8, 24)]
    public void Snap_auto_concurrency_keeps_storage_workers_available(
        int maxProcessingThreads,
        int accountRangePartitionCount,
        int processorCount,
        int expectedConcurrency)
    {
        SyncConfig syncConfig = new()
        {
            MaxProcessingThreads = maxProcessingThreads,
            SnapSyncAccountRangePartitionCount = accountRangePartitionCount,
        };

        Assert.That(SynchronizerModule.ResolveSnapConcurrency(syncConfig, processorCount), Is.EqualTo(expectedConcurrency));
    }

    // [NonParallelizable]: this test relies on the dispatcher's Task.Run reaching HandleResponse
    // promptly. Under the class-level [Parallelizable(ParallelScope.All)] it competes with the rest
    // of the suite for thread-pool capacity, and on slow CI the 1st HandleResponse can take >15s
    // to fire — exhausting the budget before we even reach the assertion window.
    [Test, NonParallelizable, CancelAfter(30_000)]
    public async Task When_ConcurrentHandleResponseIsRunning_Then_BlockDispose(CancellationToken cancellationToken)
    {
        TestSyncFeed syncFeed = new(isMultiFeed: true);
        syncFeed.LockResponse();
        TestDownloader downloader = new();
        SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig(),
            syncFeed,
            downloader,
            new TestSyncPeerPool(),
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            LimboLogs.Instance);
        Task executorTask = dispatcher.Start(cancellationToken);

        syncFeed.Activate();
        await syncFeed.WaitForHandleResponse().WaitAsync(cancellationToken);
        syncFeed.Finish();

        Task disposeTask = Task.Run(() => dispatcher.DisposeAsync().AsTask());

        // Production invariant: DisposeAsync must remain blocked while HandleResponse is in flight.
        // WaitAsync throws TimeoutException iff disposeTask is still running after the window —
        // that's the success path. Decouples this 200 ms timing window from the test's overall budget
        // (CancelAfter), so a setup overrun no longer poisons the assertion.
        Func<Task> waitForDisposeToEscape = () => disposeTask.WaitAsync(TimeSpan.FromMilliseconds(200));
        Assert.That(async () => await waitForDisposeToEscape(), Throws.TypeOf<TimeoutException>(), "DisposeAsync must wait for in-flight HandleResponse");

        syncFeed.UnlockResponse();
        await disposeTask.WaitAsync(cancellationToken);
        await executorTask.WaitAsync(cancellationToken);
    }

    [Test]
    public async Task DisposeAsync_unsubscribes_StateChanged_handler()
    {
        (TestSyncFeed syncFeed, SyncDispatcher<TestBatch> dispatcher) = CreateFeedAndDispatcher();

        await dispatcher.DisposeAsync();

        // After dispose, the dispatcher's internal semaphores and countdown events
        // are disposed. If the StateChanged handler were still subscribed, Activate()
        // would trigger UpdateState which accesses those disposed resources.
        // No throw confirms the handler was actually removed, not just that the
        // callback happens to be harmless.
        Assert.DoesNotThrow(syncFeed.Activate);
    }

    [Test]
    public async Task DisposeAsync_double_dispose_does_not_throw()
    {
        (_, SyncDispatcher<TestBatch> dispatcher) = CreateFeedAndDispatcher();

        await dispatcher.DisposeAsync();
        await dispatcher.DisposeAsync();
    }

    [Test]
    public async Task DisposeAsync_then_StateChanged_does_not_modify_dispatcher()
    {
        (TestSyncFeed syncFeed, SyncDispatcher<TestBatch> dispatcher) = CreateFeedAndDispatcher();

        await dispatcher.DisposeAsync();

        // Activate then Finish — if handler is still subscribed, this would
        // modify internal state and potentially access disposed resources
        syncFeed.Activate();
        syncFeed.Finish();

        // No exception means the handler was properly unsubscribed
    }

    private static (TestSyncFeed Feed, SyncDispatcher<TestBatch> Dispatcher) CreateFeedAndDispatcher()
    {
        TestSyncFeed syncFeed = new();
        SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig(),
            syncFeed,
            new TestDownloader(),
            new TestSyncPeerPool(),
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            LimboLogs.Instance);
        return (syncFeed, dispatcher);
    }

    [TestCase(false, 1, 1, 8)]
    [TestCase(true, 1, 1, 24)]
    [TestCase(true, 2, 1, 32)]
    [TestCase(true, 1, 2, 32)]
    public async Task Test_release_before_processing_complete(bool isMultiSync, int processingThread, int peerCount, int expectedHighestRequest)
    {
        TestSyncFeed syncFeed = new(isMultiSync, 999999);
        syncFeed.LockResponse();

        TestDownloader downloader = new();
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

        await syncFeed.WaitForHighestRequested(expectedHighestRequest).WaitAsync(TimeSpan.FromSeconds(30));

        // The dispatcher must now plateau because all peers are busy / processing slots are full
        // and HandleResponse is locked. Drain the scheduling queue and verify no further growth.
        await Task.Yield();
        Assert.That(syncFeed.HighestRequested, Is.EqualTo(expectedHighestRequest));
        syncFeed.UnlockResponse();
    }
}
