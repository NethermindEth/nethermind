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
        public ManualResetEventSlim ReleaseHandleResponse { get; } = new(false);
        public int HandleResponseCompleted => Volatile.Read(ref _completed);
        private int _completed;

        public async Task<TestRequest?> PrepareRequest(CancellationToken token)
        {
            if (Interlocked.Increment(ref _prepared) == 1) return new TestRequest();

            await Task.Delay(Timeout.Infinite, token);
            return null;
        }

        public SyncResponseHandlingResult HandleResponse(TestRequest response, PeerInfo? peer = null)
        {
            HandleResponseEntered.TrySetResult();
            ReleaseHandleResponse.Wait();
            Interlocked.Increment(ref _completed);
            return SyncResponseHandlingResult.OK;
        }
    }

    private static SimpleDispatcher<TestRequest> CreateDispatcher(GatedFeed feed)
    {
        ISyncPeerPool peerPool = Substitute.For<ISyncPeerPool>();
        peerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new SyncPeerAllocation(new PeerInfo(Substitute.For<ISyncPeer>()), AllocationContexts.State)));

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
        await feed.HandleResponseEntered.Task.WaitAsync(cancellationToken);

        // Shutdown: the loop is cancelled while a dispatched worker is still inside HandleResponse
        // (in production: writing state-sync nodes into RocksDB).
        cts.Cancel();

        // Production invariant: Run must not return while a worker it spawned is still running -
        // the caller (Synchronizer -> Autofac) disposes the databases right after it returns.
        Func<Task> waitForRunToEscape = () => runTask.WaitAsync(TimeSpan.FromMilliseconds(200));
        Assert.That(async () => await waitForRunToEscape(), Throws.TypeOf<TimeoutException>(), "Run must wait for in-flight HandleResponse");
        Assert.That(feed.HandleResponseCompleted, Is.EqualTo(0));

        feed.ReleaseHandleResponse.Set();
        Assert.ThrowsAsync<TaskCanceledException>(() => runTask.WaitAsync(cancellationToken));
        Assert.That(feed.HandleResponseCompleted, Is.EqualTo(1));
    }
}
