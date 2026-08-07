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
    // Mirrors SimpleDispatcher<T>.InFlightRequestsPerProcessingThread × MaxThreads.
    private const int InFlightCap = 2 * MaxThreads;
    private const int PeerCount = 8;

    private static SimpleDispatcher<TestBatch> CreateDispatcher(
        TestFeed feed, ISyncDownloader<TestBatch> downloader, TestSyncPeerPool peerPool, int allocateTimeoutMs = 1) =>
        new(
            feed,
            downloader,
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            AllocationContexts.Snap,
            peerPool,
            new TestSyncConfig { MaxProcessingThreads = MaxThreads, SyncDispatcherAllocateTimeoutMs = allocateTimeoutMs },
            LimboLogs.Instance);

    [Test, CancelAfter(30_000)]
    public async Task In_flight_dispatches_exceed_processing_threads_up_to_the_cap_and_run_drains_them_all(CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: PeerCount);
        BlockingDownloader downloader = new();
        await using TestSyncPeerPool peerPool = new(PeerCount);

        Task runTask = CreateDispatcher(feed, downloader, peerPool).Run(cancellationToken);

        // With the network wait not holding a processing slot, dispatches overlap beyond
        // MaxProcessingThreads up to the in-flight cap.
        await downloader.WaitForStarted(InFlightCap, cancellationToken);
        Assert.That(downloader.Started, Is.GreaterThan(MaxThreads));

        // Run must not return while dispatches are in flight, and despite free peers no dispatch
        // beyond the cap may start.
        Assert.That(async () => await runTask.WaitAsync(TimeSpan.FromMilliseconds(200)), Throws.TypeOf<TimeoutException>());
        Assert.That(downloader.Started, Is.EqualTo(InFlightCap));

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

        await downloader.WaitForStarted(InFlightCap, cancellationToken);
        downloader.ReleaseAll();

        // A full cap's worth of responses arrived at once; only MaxThreads may enter HandleResponse.
        while (feed.CurrentlyHandling < MaxThreads)
        {
            await Task.Delay(10, cancellationToken);
        }

        feed.UnlockHandleResponse();
        await runTask.WaitAsync(cancellationToken);

        Assert.That(feed.HandledCount, Is.EqualTo(PeerCount));
        Assert.That(feed.MaxConcurrentHandling, Is.EqualTo(MaxThreads));
    }

    // With more peers than the cap the loop parks in the in-flight cap wait; with fewer it parks
    // in peerPool.Allocate. Cancellation must drain cleanly from either park point.
    [TestCase(PeerCount)]
    [TestCase(MaxThreads)]
    [CancelAfter(30_000)]
    public async Task Cancellation_mid_dispatch_frees_allocations_and_drains(int poolPeerCount, CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: int.MaxValue);
        BlockingDownloader downloader = new();
        await using TestSyncPeerPool peerPool = new(poolPeerCount);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task runTask = CreateDispatcher(feed, downloader, peerPool).Run(cts.Token);

        await downloader.WaitForStarted(Math.Min(poolPeerCount, InFlightCap), cancellationToken);

        // Both park points report cancellation as a failed allocation rather than throwing,
        // so Run drains and returns normally.
        cts.Cancel();
        await runTask.WaitAsync(cancellationToken);

        Assert.That(peerPool.FreedCount, Is.EqualTo(peerPool.AllocatedCount), "every allocation must be freed");
        Assert.That(feed.HandledCount, Is.Zero, "cancelled dispatches must not reach HandleResponse");
    }

    [Test, CancelAfter(30_000)]
    public async Task Failed_allocation_is_handled_with_null_peer_without_a_processing_slot_and_not_freed(CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: 4);
        feed.LockHandleResponse();
        BlockingDownloader downloader = new();
        downloader.ReleaseAll(); // The network returns instantly; this test stalls the processing side.
        await using TestSyncPeerPool peerPool = new(peerCount: 1) { HonorAllocationTimeout = true };

        // A generous allocate timeout so only the deliberately starved allocation below times out.
        Task runTask = CreateDispatcher(feed, downloader, peerPool, allocateTimeoutMs: 1000).Run(cancellationToken);

        // Requests 1-2 fill both processing slots and block in HandleResponse; request 3 then
        // holds the only peer while waiting for a slot, so request 4's allocation times out. Its
        // null-peer response must reach the feed even though every processing slot is taken.
        while (feed.FailedAllocationCount == 0)
        {
            await Task.Delay(10, cancellationToken);
        }
        Assert.That(feed.CurrentlyHandling, Is.EqualTo(MaxThreads), "the null-peer response must not wait for a processing slot");

        feed.UnlockHandleResponse();
        await runTask.WaitAsync(cancellationToken);

        Assert.That(feed.FailedAllocationCount, Is.EqualTo(1));
        Assert.That(feed.HandledCount, Is.EqualTo(3));
        Assert.That(peerPool.FreedCount, Is.EqualTo(peerPool.AllocatedCount), "a failed allocation must not be freed");
    }
}
