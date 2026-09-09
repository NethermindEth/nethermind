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

            // The loop has left PrepareRequest with the cancellation, so everything Run does from here is its drain
            // path and releasing the gate below cannot be mistaken for a normal second lap.
            await feed.PrepareRequestCancelled.Task.WaitAsync(cancellationToken);

            Assert.That(feed.HandleResponseCompleted, Is.EqualTo(0));
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
