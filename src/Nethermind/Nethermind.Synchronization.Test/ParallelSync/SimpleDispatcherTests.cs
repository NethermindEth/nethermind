// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Test.Mocks;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

[Parallelizable(ParallelScope.All)]
public class SimpleDispatcherTests
{
    private class TestBatch;

    /// <summary>
    /// Downloader whose <see cref="Dispatch"/> blocks until <see cref="ReleaseAll"/>, emulating
    /// a long network round trip. Honors the token so cancellation aborts the wait.
    /// </summary>
    private class BlockingDownloader : ISyncDownloader<TestBatch>
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public void ReleaseAll() => _gate.TrySetResult();

        public async Task Dispatch(PeerInfo peerInfo, TestBatch request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            await _gate.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForStarted(int count, CancellationToken cancellationToken)
        {
            while (Started < count)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    private class TestFeed(int totalRequests) : ISimpleSyncFeed<TestBatch>
    {
        private readonly ManualResetEventSlim _handleGate = new(true);
        private int _prepared;
        private int _handling;
        private int _maxConcurrentHandling;
        private int _handled;
        private int _failedAllocations;

        public int HandledCount => Volatile.Read(ref _handled);
        public int FailedAllocationCount => Volatile.Read(ref _failedAllocations);
        public int MaxConcurrentHandling => Volatile.Read(ref _maxConcurrentHandling);
        public int CurrentlyHandling => Volatile.Read(ref _handling);

        public void LockHandleResponse() => _handleGate.Reset();
        public void UnlockHandleResponse() => _handleGate.Set();

        public Task<TestBatch?> PrepareRequest(CancellationToken token) =>
            Task.FromResult(_prepared++ < totalRequests ? new TestBatch() : null);

        public SyncResponseHandlingResult HandleResponse(TestBatch response, PeerInfo? peer = null)
        {
            if (peer is null)
            {
                Interlocked.Increment(ref _failedAllocations);
                return SyncResponseHandlingResult.NotAssigned;
            }

            int handling = Interlocked.Increment(ref _handling);
            int max = Volatile.Read(ref _maxConcurrentHandling);
            while (handling > max && Interlocked.CompareExchange(ref _maxConcurrentHandling, handling, max) != max)
            {
                max = Volatile.Read(ref _maxConcurrentHandling);
            }

            _handleGate.Wait();
            Interlocked.Increment(ref _handled);
            Interlocked.Decrement(ref _handling);
            return SyncResponseHandlingResult.OK;
        }
    }

    private const int MaxThreads = 2;
    private const int PeerCount = 8;

    private static SimpleDispatcher<TestBatch> CreateDispatcher(
        TestFeed feed, ISyncDownloader<TestBatch> downloader, TestSyncPeerPool peerPool) =>
        new(
            feed,
            downloader,
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            AllocationContexts.Snap,
            peerPool,
            new TestSyncConfig { MaxProcessingThreads = MaxThreads },
            LimboLogs.Instance);

    [Test, CancelAfter(30_000)]
    public async Task In_flight_dispatches_can_exceed_processing_threads_and_run_drains_them_all(CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: PeerCount);
        BlockingDownloader downloader = new();
        await using TestSyncPeerPool peerPool = new(PeerCount);

        Task runTask = CreateDispatcher(feed, downloader, peerPool).Run(cancellationToken);

        // With the network wait not holding a processing slot, all peers get a request in flight.
        await downloader.WaitForStarted(PeerCount, cancellationToken);
        Assert.That(downloader.Started, Is.GreaterThan(MaxThreads));

        // The feed has returned null (loop exited), yet Run must not return while dispatches are in flight.
        Assert.That(async () => await runTask.WaitAsync(TimeSpan.FromMilliseconds(200)), Throws.TypeOf<TimeoutException>());

        downloader.ReleaseAll();
        await runTask.WaitAsync(cancellationToken);

        Assert.That(feed.HandledCount, Is.EqualTo(PeerCount));
        Assert.That(peerPool.FreedCount, Is.EqualTo(PeerCount));
    }

    [Test, CancelAfter(30_000)]
    public async Task HandleResponse_concurrency_is_bounded_by_processing_threads(CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: PeerCount);
        feed.LockHandleResponse();
        BlockingDownloader downloader = new();
        await using TestSyncPeerPool peerPool = new(PeerCount);

        Task runTask = CreateDispatcher(feed, downloader, peerPool).Run(cancellationToken);

        await downloader.WaitForStarted(PeerCount, cancellationToken);
        downloader.ReleaseAll();

        // All responses arrived at once; only MaxThreads of them may enter HandleResponse.
        while (feed.CurrentlyHandling < MaxThreads)
        {
            await Task.Delay(10, cancellationToken);
        }

        feed.UnlockHandleResponse();
        await runTask.WaitAsync(cancellationToken);

        Assert.That(feed.HandledCount, Is.EqualTo(PeerCount));
        Assert.That(feed.MaxConcurrentHandling, Is.EqualTo(MaxThreads));
    }

    [Test, CancelAfter(30_000)]
    public async Task Cancellation_mid_dispatch_frees_allocations_and_drains(CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: int.MaxValue);
        BlockingDownloader downloader = new();
        await using TestSyncPeerPool peerPool = new(PeerCount);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task runTask = CreateDispatcher(feed, downloader, peerPool).Run(cts.Token);

        // All peers busy: the Run loop is now parked in peerPool.Allocate.
        await downloader.WaitForStarted(PeerCount, cancellationToken);

        // Allocate mirrors SyncPeerPool and reports cancellation as a failed allocation rather
        // than throwing, so Run drains and returns normally.
        cts.Cancel();
        await runTask.WaitAsync(cancellationToken);

        Assert.That(peerPool.FreedCount, Is.EqualTo(peerPool.AllocatedCount), "every allocation must be freed");
        Assert.That(feed.HandledCount, Is.Zero, "cancelled dispatches must not reach HandleResponse");
    }

    [Test, CancelAfter(30_000)]
    public async Task Failed_allocation_is_handled_with_null_peer_and_not_freed(CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: 2);
        BlockingDownloader downloader = new();
        await using TestSyncPeerPool peerPool = new(peerCount: 1) { HonorAllocationTimeout = true };

        Task runTask = CreateDispatcher(feed, downloader, peerPool).Run(cancellationToken);

        // The first request holds the only peer in a long network wait, so the second request's
        // allocation times out and must be handed to the feed as a null-peer response.
        await downloader.WaitForStarted(1, cancellationToken);
        while (feed.FailedAllocationCount == 0)
        {
            await Task.Delay(10, cancellationToken);
        }

        downloader.ReleaseAll();
        await runTask.WaitAsync(cancellationToken);

        Assert.That(feed.FailedAllocationCount, Is.EqualTo(1));
        Assert.That(feed.HandledCount, Is.EqualTo(1));
        Assert.That(peerPool.FreedCount, Is.EqualTo(peerPool.AllocatedCount), "a failed allocation must not be freed");
    }
}
