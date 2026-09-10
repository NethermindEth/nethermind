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
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

[Parallelizable(ParallelScope.All)]
public class SimpleDispatcherTests
{
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
            .Returns(_ => Task.FromResult(CreateAllocation()));

        return CreateDispatcher(feed, Substitute.For<ISyncDownloader<TestRequest>>(), peerPool, new TestSyncConfig());
    }

    private static SyncPeerAllocation CreateAllocation()
    {
        SyncPeerAllocation allocation = new(AllocationContexts.State);
        allocation.AllocatePeer(new PeerInfo(Substitute.For<ISyncPeer>()));
        return allocation;
    }

    private static SimpleDispatcher<TestRequest> CreateDispatcher(
        ISimpleSyncFeed<TestRequest> feed,
        ISyncDownloader<TestRequest> downloader,
        ISyncPeerPool peerPool,
        TestSyncConfig syncConfig) =>
        new(
            feed,
            downloader,
            Substitute.For<IPeerAllocationStrategyFactory<TestRequest>>(),
            AllocationContexts.State,
            peerPool,
            syncConfig,
            LimboLogs.Instance);

    /// <summary>
    /// How long the gated worker is held after the cancellation, i.e. how long Run is given to prove it does not
    /// return. Paid only when the assertion fails.
    /// </summary>
    private static readonly TimeSpan WorkerHold = TimeSpan.FromMilliseconds(500);

    [Test, CancelAfter(30_000)]
    public async Task Cancellation_while_waiting_for_a_permit_frees_the_undispatched_allocation(CancellationToken cancellationToken)
    {
        TestRequest firstRequest = new();
        TestRequest secondRequest = new();
        ISimpleSyncFeed<TestRequest> feed = Substitute.For<ISimpleSyncFeed<TestRequest>>();
        feed.PrepareRequest(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<TestRequest?>(firstRequest),
            Task.FromResult<TestRequest?>(secondRequest),
            Task.FromResult<TestRequest?>(null));

        SyncPeerAllocation firstAllocation = CreateAllocation();
        SyncPeerAllocation secondAllocation = CreateAllocation();
        TaskCompletionSource secondAllocated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondFreed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISyncPeerPool peerPool = Substitute.For<ISyncPeerPool>();
        peerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(firstAllocation),
                _ =>
                {
                    secondAllocated.TrySetResult();
                    return Task.FromResult(secondAllocation);
                });
        peerPool.When(pool => pool.Free(secondAllocation)).Do(_ => secondFreed.TrySetResult());

        TaskCompletionSource firstDispatchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstDispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISyncDownloader<TestRequest> downloader = Substitute.For<ISyncDownloader<TestRequest>>();
        downloader.Dispatch(Arg.Any<PeerInfo>(), firstRequest, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            firstDispatchStarted.TrySetResult();
            return releaseFirstDispatch.Task;
        });
        SimpleDispatcher<TestRequest> dispatcher = CreateDispatcher(feed, downloader, peerPool, new TestSyncConfig { MaxProcessingThreads = 1 });
        using CancellationTokenSource cts = new();

        Task runTask = dispatcher.Run(cts.Token);
        try
        {
            await firstDispatchStarted.Task.WaitAsync(cancellationToken);
            await secondAllocated.Task.WaitAsync(cancellationToken);

            cts.Cancel();
            await secondFreed.Task.WaitAsync(cancellationToken);

            Assert.That(runTask.IsCompleted, Is.False, "the first worker still holds the only permit");
        }
        finally
        {
            cts.Cancel();
            releaseFirstDispatch.TrySetResult();
        }

        Assert.That(async () => await runTask.WaitAsync(cancellationToken), Throws.InstanceOf<OperationCanceledException>());
        peerPool.Received(1).Free(secondAllocation);
        peerPool.Received(1).Free(firstAllocation);
        _ = downloader.DidNotReceive().Dispatch(Arg.Any<PeerInfo>(), secondRequest, Arg.Any<CancellationToken>());
    }

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
