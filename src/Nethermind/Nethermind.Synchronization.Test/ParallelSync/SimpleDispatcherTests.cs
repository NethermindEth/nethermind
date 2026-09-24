// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Test.Mocks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

[Parallelizable(ParallelScope.All)]
public class SimpleDispatcherTests
{
    public class TestBatch;

    /// <summary>
    /// Downloader whose <see cref="Dispatch"/> blocks until <see cref="ReleaseAll"/>, emulating
    /// a long network round trip. Honors the token so cancellation aborts the wait.
    /// The first <paramref name="completeImmediately"/> dispatches return without blocking.
    /// </summary>
    private class BlockingDownloader(int completeImmediately = 0) : ISyncDownloader<TestBatch>
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public void ReleaseAll() => _gate.TrySetResult();

        public async Task Dispatch(PeerInfo peerInfo, TestBatch request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _started) <= completeImmediately) return;
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

        try
        {
            await downloader.WaitForStarted(InFlightCap, cancellationToken);
            downloader.ReleaseAll();

            // A full cap's worth of responses arrived at once; only MaxThreads may enter HandleResponse.
            while (feed.CurrentlyHandling < MaxThreads)
            {
                await Task.Delay(10, cancellationToken);
            }

        }
        finally
        {
            feed.UnlockHandleResponse();
        }
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

    // Blocked response workers must not compete with other fixtures for thread-pool capacity.
    [Test, NonParallelizable, CancelAfter(30_000)]
    public async Task Failed_allocation_is_handled_with_null_peer_without_a_processing_slot_and_not_freed(CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: 4);
        feed.LockHandleResponse();
        BlockingDownloader downloader = new(completeImmediately: MaxThreads);
        await using TestSyncPeerPool peerPool = new(peerCount: 1) { HonorAllocationTimeout = true };

        // A generous allocate timeout so only the deliberately starved allocation below times out.
        Task runTask = CreateDispatcher(feed, downloader, peerPool, allocateTimeoutMs: 1000).Run(cancellationToken);

        try
        {
            // Requests 1-2 download instantly, fill both processing slots and block in HandleResponse;
            // request 3 then holds the only peer in a blocked download, so request 4's allocation times
            // out. Its null-peer response must reach the feed even though every processing slot is taken.
            while (feed.FailedAllocationCount == 0)
            {
                await Task.Delay(10, cancellationToken);
            }
            Assert.That(feed.CurrentlyHandling, Is.EqualTo(MaxThreads), "the null-peer response must not wait for a processing slot");

        }
        finally
        {
            downloader.ReleaseAll();
            feed.UnlockHandleResponse();
        }
        await runTask.WaitAsync(cancellationToken);

        Assert.That(feed.FailedAllocationCount, Is.EqualTo(1));
        Assert.That(feed.HandledCount, Is.EqualTo(3));
        Assert.That(peerPool.FreedCount, Is.EqualTo(peerPool.AllocatedCount), "a failed allocation must not be freed");
    }

    [Test, CancelAfter(30_000)]
    public async Task Peers_are_freed_while_responses_wait_for_a_processing_slot(CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: InFlightCap);
        feed.LockHandleResponse();
        BlockingDownloader downloader = new();
        downloader.ReleaseAll();
        await using TestSyncPeerPool peerPool = new(peerCount: 1);

        Task runTask = CreateDispatcher(feed, downloader, peerPool).Run(cancellationToken);

        try
        {
            // Processing is stalled, yet the single peer must still serve every in-flight request.
            await downloader.WaitForStarted(InFlightCap, cancellationToken);
            while (peerPool.AvailablePeers == 0)
            {
                await Task.Delay(10, cancellationToken);
            }
            Assert.That(feed.CurrentlyHandling, Is.EqualTo(MaxThreads));
        }
        finally
        {
            feed.UnlockHandleResponse();
        }
        await runTask.WaitAsync(cancellationToken);

        Assert.That(feed.HandledCount, Is.EqualTo(InFlightCap));
        Assert.That(peerPool.FreedCount, Is.EqualTo(InFlightCap));
    }

    [Test, CancelAfter(30_000)]
    public async Task Cancellation_with_responses_waiting_for_processing_drains_and_frees_peers(CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: InFlightCap);
        feed.LockHandleResponse();
        BlockingDownloader downloader = new();
        downloader.ReleaseAll();
        await using TestSyncPeerPool peerPool = new(PeerCount);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task runTask = CreateDispatcher(feed, downloader, peerPool).Run(cts.Token);

        try
        {
            await downloader.WaitForStarted(InFlightCap, cancellationToken);
            while (feed.CurrentlyHandling < MaxThreads)
            {
                await Task.Delay(10, cancellationToken);
            }
            cts.Cancel();
            Assert.That(runTask.IsCompleted, Is.False, "processing must finish before Run returns");
        }
        finally
        {
            cts.Cancel();
            feed.UnlockHandleResponse();
            await runTask.WaitAsync(cancellationToken);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(feed.HandledCount, Is.EqualTo(MaxThreads), "queued responses must be discarded after cancellation");
            Assert.That(peerPool.FreedCount, Is.EqualTo(InFlightCap));
            Assert.That(peerPool.AvailablePeers, Is.EqualTo(PeerCount));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(30_000)]
    public async Task Downloader_failure_returns_allocations_and_does_not_stall_later_requests(bool timeout, CancellationToken cancellationToken)
    {
        TestFeed feed = new(totalRequests: PeerCount);
        ISyncDownloader<TestBatch> downloader = Substitute.For<ISyncDownloader<TestBatch>>();
        downloader.Dispatch(Arg.Any<PeerInfo>(), Arg.Any<TestBatch>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(timeout ? new TimeoutException() : new InvalidOperationException()));
        await using TestSyncPeerPool peerPool = new(PeerCount);

        await CreateDispatcher(feed, downloader, peerPool).Run(cancellationToken).WaitAsync(cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(feed.HandledCount, Is.EqualTo(PeerCount));
            Assert.That(peerPool.FreedCount, Is.EqualTo(PeerCount));
            Assert.That(peerPool.AvailablePeers, Is.EqualTo(PeerCount));
        }
    }

    public class TestRequest;

    /// <summary>
    /// Hands out one request, then idles on the token like a real feed waiting for more work.
    /// HandleResponse blocks on a gate to model a worker that is inside a DB write.
    /// </summary>
    private class GatedFeed : ISimpleSyncFeed<TestRequest>
    {
        private int _prepared;

        public TaskCompletionSource HandleResponseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Released to let the blocked worker finish. A TCS so it needs no disposal while a worker is inside it.</summary>
        public TaskCompletionSource ReleaseHandleResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set once the loop has observed cancellation and left <see cref="PrepareRequest"/>.</summary>
        public TaskCompletionSource PrepareRequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int HandleResponseCompleted => Volatile.Read(ref _completed);
        private int _completed;

        public async Task<TestRequest?> PrepareRequest(CancellationToken token)
        {
            if (Interlocked.Increment(ref _prepared) == 1) return new TestRequest();

            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                PrepareRequestCancelled.TrySetResult();
                throw;
            }

            return null;
        }

        public SyncResponseHandlingResult HandleResponse(TestRequest response, PeerInfo? peer = null)
        {
            HandleResponseEntered.TrySetResult();
            ReleaseHandleResponse.Task.GetAwaiter().GetResult();
            Interlocked.Increment(ref _completed);
            return SyncResponseHandlingResult.OK;
        }
    }

    private static SimpleDispatcher<TestRequest> CreateDispatcher(GatedFeed feed)
    {
        ISyncPeerPool peerPool = Substitute.For<ISyncPeerPool>();
        peerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                SyncPeerAllocation allocation = new(AllocationContexts.State);
                allocation.AllocatePeer(new PeerInfo(Substitute.For<ISyncPeer>()));
                return Task.FromResult(allocation);
            });

        return new SimpleDispatcher<TestRequest>(
            feed,
            Substitute.For<ISyncDownloader<TestRequest>>(),
            Substitute.For<IPeerAllocationStrategyFactory<TestRequest>>(),
            AllocationContexts.State,
            peerPool,
            new TestSyncConfig(),
            LimboLogs.Instance);
    }

    /// <summary>
    /// How long the gated worker is held after the cancellation, i.e. how long Run is given to prove it does not
    /// return. Paid only when the assertion fails.
    /// </summary>
    private static readonly TimeSpan WorkerHold = TimeSpan.FromMilliseconds(500);

    [Test, CancelAfter(30_000)]
    public async Task Cancelled_run_waits_for_in_flight_handle_response(CancellationToken cancellationToken)
    {
        GatedFeed feed = new();
        SimpleDispatcher<TestRequest> dispatcher = CreateDispatcher(feed);
        using CancellationTokenSource cts = new();

        Task runTask = dispatcher.Run(cts.Token);

        // Production invariant: the caller (Synchronizer -> Autofac) disposes the databases the moment Run returns,
        // so no worker may still be inside HandleResponse at that instant. Sampling the counter in a synchronous
        // continuation states that as an ordering fact rather than as a deadline the runner has to beat.
        Task<int> completedWhenRunReturned = runTask.ContinueWith(
            _ => feed.HandleResponseCompleted,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await feed.HandleResponseEntered.Task.WaitAsync(cancellationToken);

            // Shutdown: the loop is cancelled while a dispatched worker is still inside HandleResponse
            // (in production: writing state-sync nodes into RocksDB).
            cts.Cancel();

            // PrepareRequest has thrown the cancellation, so everything Run does from here is its exit path.
            await feed.PrepareRequestCancelled.Task.WaitAsync(cancellationToken);

            // The worker holds the permit Run's drain has to reclaim, so Run cannot get back to its caller: the
            // whole point of #13154. Hold the gate over the window instead of opening it here - if the drain sits
            // after the loop rather than in the finally, the OperationCanceledException carries straight out of
            // Run and this is what catches it. Only the failing path pays the wait, and the unwind it is racing
            // is a handful of continuations with no IO, so the margin is four orders of magnitude.
            Assert.That(await Task.WhenAny(runTask, Task.Delay(WorkerHold, cancellationToken)), Is.Not.SameAs(runTask),
                "Run returned while a worker was still inside HandleResponse");

            Assert.That(feed.HandleResponseCompleted, Is.EqualTo(0), "the gate is still closed");
        }
        finally
        {
            // Unconditional: a failed assertion above must not leave the worker blocked on a gate nobody will open.
            feed.ReleaseHandleResponse.TrySetResult();
        }

        Assert.ThrowsAsync<TaskCanceledException>(() => runTask.WaitAsync(CancellationToken.None));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await completedWhenRunReturned, Is.EqualTo(1), "Run must wait for in-flight HandleResponse");
            Assert.That(feed.HandleResponseCompleted, Is.EqualTo(1));
        }
    }
}
