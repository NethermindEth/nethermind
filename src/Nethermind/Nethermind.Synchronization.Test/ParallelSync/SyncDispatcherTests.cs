// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.Blocks;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Test.Mocks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

[NonParallelizable]
public class SyncDispatcherTests
{
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

    private class FailFirstDispatchDownloader(Exception failure) : ISyncDownloader<TestBatch>
    {
        private int _dispatchCount;

        public Task Dispatch(PeerInfo peerInfo, TestBatch request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _dispatchCount) == 1)
            {
                throw failure;
            }

            request.Result = new int[request.Length];
            for (int i = 0; i < request.Length; i++)
            {
                request.Result[i] = request.Start + i;
            }

            return Task.CompletedTask;
        }
    }

    private class TestSyncFeed(bool isMultiFeed = true, int max = 64, Action? onPrepareRequest = null) : SyncFeed<TestBatch>
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

        public int HandleResponseCallCount => Volatile.Read(ref _handleResponseCallCount);
        private int _handleResponseCallCount;

        public override SyncResponseHandlingResult HandleResponse(TestBatch response, PeerInfo? peer = null)
        {
            Interlocked.Increment(ref _handleResponseCallCount);
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
            onPrepareRequest?.Invoke();

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

    [Test, MaxTime(30_000)]
    public async Task Simple_test_sync()
    {
        TestSyncFeed syncFeed = new();
        TestDownloader downloader = new();
        await using TestSyncPeerPool peerPool = new();
        SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig(),
            syncFeed,
            downloader,
            peerPool,
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

    [Test, CancelAfter(30_000)]
    public async Task Failed_dispatch_is_retried_and_warns_only_for_non_peer_faults(
        [Values] bool peerFault, [Values] bool maskSensitiveData, CancellationToken cancellationToken)
    {
        bool previous = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = maskSensitiveData;
        try
        {
            Exception failure = peerFault
                ? new SubprotocolException("Receipt count mismatch with block transactions count")
                : new InvalidOperationException("node fault");
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();
            logger.IsDebug.Returns(true);
            logger.IsWarn.Returns(true);
            TestSyncFeed syncFeed = new(max: 8);
            await using TestSyncPeerPool peerPool = new();
            await using SyncDispatcher<TestBatch> dispatcher = new(
                new TestSyncConfig(),
                syncFeed,
                new FailFirstDispatchDownloader(failure),
                peerPool,
                new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
                new OneLoggerLogManager(new ILogger(logger)));

            syncFeed.Activate();
            await dispatcher.Start(cancellationToken).WaitAsync(cancellationToken);

            string detail = maskSensitiveData ? "[redacted]" : failure.Message;
            Assert.That(syncFeed._results, Has.Count.EqualTo(syncFeed.Max), "the failed request must be retried");
            logger.Received(peerFault ? 1 : 0).Debug(Arg.Is<string>(s => s.Contains(detail) && !s.Contains("   at ")));
            logger.Received(peerFault ? 0 : 1).Warn(Arg.Is<string>(s => s.Contains(failure.GetType().Name) && s.Contains(detail)));
        }
        finally
        {
            SensitiveLogMasking.Enabled = previous;
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task When_ConcurrentHandleResponseIsRunning_Then_BlockDispose(CancellationToken cancellationToken)
    {
        TestSyncFeed syncFeed = new(isMultiFeed: true);
        syncFeed.LockResponse();
        TestDownloader downloader = new();
        await using TestSyncPeerPool peerPool = new();
        SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig(),
            syncFeed,
            downloader,
            peerPool,
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

    [Test, CancelAfter(30_000)]
    public async Task Cancelled_in_flight_dispatch_skips_its_response_and_disposes_its_allocation(CancellationToken cancellationToken)
    {
        // The dispatch loop cancels its token on every exit, including the routine feed-finish exit
        // on a live node. The cancelled dispatch must not handle its response into a finishing feed,
        // and it must still dispose its allocation - a leaked slot retires the peer for the lifetime
        // of the connection.
        TestSyncPeerPool pool = new(peerCount: 2);
        TestSyncFeed syncFeed = new(max: 16);
        await using SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig { MaxProcessingThreads = 1 },
            syncFeed,
            new TestDownloader(),
            pool,
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            LimboLogs.Instance);

        // The first dispatch takes the single processing permit and blocks inside HandleResponse;
        // the second finishes downloading and waits for the permit, holding its peer allocation.
        syncFeed.LockResponse();
        syncFeed.Activate();
        Task dispatcherTask = dispatcher.Start(cancellationToken);
        await syncFeed.WaitForHandleResponse().WaitAsync(cancellationToken);

        // The permit holder has already freed its own slot, so exactly one slot outstanding
        // means the second dispatch holds its allocation and the cancelled-wait path is real.
        Assert.That(() => pool.AvailablePeers, Is.EqualTo(1).After(10_000, 10),
            "guard: the second dispatch must hold an allocation before the feed finishes");

        syncFeed.Finish();
        await dispatcherTask.WaitAsync(cancellationToken);

        syncFeed.UnlockResponse();
        await dispatcher.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.AvailablePeers, Is.EqualTo(2), "every allocation must return to the pool");
            Assert.That(syncFeed.HandleResponseCallCount, Is.EqualTo(1), "the cancelled dispatch must not handle its response");
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Feed_is_finished_when_the_loop_exits_without_an_await_throwing(CancellationToken cancellationToken)
    {
        // Shutdown waits on ISyncFeed.FeedTask (Synchronizer.DisposeAsync), and the dispatch loop is the
        // feed's only consumer - a loop that returns without finishing the feed turns that wait into a
        // guaranteed timeout. Cancelling from inside PrepareRequest reproduces the exit where nothing
        // throws: the peer pool reports a failed allocation for a cancelled token, so the iteration runs
        // to completion and it is the `while` condition, not a cancelled await, that ends the loop.
        using CancellationTokenSource cts = new();
        TestSyncFeed syncFeed = new(onPrepareRequest: cts.Cancel);
        await using SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig(),
            syncFeed,
            new TestDownloader(),
            new TestSyncPeerPool(),
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            LimboLogs.Instance);

        syncFeed.Activate();
        Assert.That(syncFeed.FeedTask.IsCompleted, Is.False, "an activated feed must have a pending FeedTask");

        await dispatcher.Start(cts.Token).WaitAsync(cancellationToken);

        Assert.That(syncFeed.FeedTask.IsCompleted, Is.True, "the dispatch loop must finish the feed on every exit path");
    }

    [Test]
    public async Task DisposeAsync_unsubscribes_StateChanged_handler()
    {
        await using TestSyncPeerPool peerPool = new();
        (TestSyncFeed syncFeed, SyncDispatcher<TestBatch> dispatcher) = CreateFeedAndDispatcher(peerPool);

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
        await using TestSyncPeerPool peerPool = new();
        (_, SyncDispatcher<TestBatch> dispatcher) = CreateFeedAndDispatcher(peerPool);

        await dispatcher.DisposeAsync();
        await dispatcher.DisposeAsync();
    }

    [Test]
    public async Task DisposeAsync_then_StateChanged_does_not_modify_dispatcher()
    {
        await using TestSyncPeerPool peerPool = new();
        (TestSyncFeed syncFeed, SyncDispatcher<TestBatch> dispatcher) = CreateFeedAndDispatcher(peerPool);

        await dispatcher.DisposeAsync();

        // Activate then Finish — if handler is still subscribed, this would
        // modify internal state and potentially access disposed resources
        syncFeed.Activate();
        syncFeed.Finish();

        // No exception means the handler was properly unsubscribed
    }

    private static (TestSyncFeed Feed, SyncDispatcher<TestBatch> Dispatcher) CreateFeedAndDispatcher(TestSyncPeerPool peerPool)
    {
        TestSyncFeed syncFeed = new();
        SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig(),
            syncFeed,
            new TestDownloader(),
            peerPool,
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
        await using TestSyncPeerPool peerPool = new(peerCount);
        SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig()
            {
                MaxProcessingThreads = processingThread,
            },
            syncFeed,
            downloader,
            peerPool,
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
    [Test, CancelAfter(30000)]
    public async Task Blocks_failures_preserve_requested_constituents_after_response_disposal(
        [Range(1, 7)] int requestedParts, [Values] bool weak, CancellationToken token)
    {
        using IContainer node = SyncPeerPoolTests.CreateNode();
        IBlockTree tree = node.Resolve<IBlockTree>();
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        tree.Insert(header, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        ISyncPeer remote = Substitute.For<ISyncPeer>();
        remote.Node.Returns(new Nethermind.Stats.Model.Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        PeerInfo peer = new(remote);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                SyncPeerAllocation allocation = new(call.ArgAt<AllocationContexts>(1));
                allocation.AllocatePeer(peer);
                return Task.FromResult(allocation);
            });
        SyncPeerPool accounting = node.Resolve<SyncPeerPool>();
        pool.When(value => value.ReportWeakPeer(peer, Arg.Any<AllocationContexts>()))
            .Do(call => accounting.ReportWeakPeer(peer, call.Arg<AllocationContexts>()));
        pool.When(value => value.ReportNoSyncProgress(peer, Arg.Any<AllocationContexts>()))
            .Do(call => accounting.ReportNoSyncProgress(peer, call.Arg<AllocationContexts>()));
        BlocksRequest request = new() { DownloadTask = Task.CompletedTask };
        if ((requestedParts & 1) != 0) request.BodiesRequests = new ArrayPoolList<BlockHeader>(1) { header };
        if ((requestedParts & 2) != 0) request.ReceiptsRequests = new ArrayPoolList<BlockHeader>(1) { header };
        if ((requestedParts & 4) != 0) request.BlockAccessListsRequests = new ArrayPoolList<BlockHeader>(1) { header };
        OneBatchFeed<BlocksRequest> feed = new(request, AllocationContexts.Blocks | AllocationContexts.BlockAccessLists,
            weak ? SyncResponseHandlingResult.LesserQuality : SyncResponseHandlingResult.NoProgress, disposeResponse: true);
        ISyncDownloader<BlocksRequest> downloader = Substitute.For<ISyncDownloader<BlocksRequest>>();
        TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AllocationContexts lastContext = (requestedParts & 4) != 0 ? AllocationContexts.BlockAccessLists
            : (requestedParts & 2) != 0 ? AllocationContexts.Receipts : AllocationContexts.Bodies;
        if (weak) pool.When(value => value.ReportWeakPeer(peer, lastContext)).Do(_ => reported.TrySetResult());
        else pool.When(value => value.ReportNoSyncProgress(peer, lastContext)).Do(_ => reported.TrySetResult());
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using SyncDispatcher<BlocksRequest> dispatcher = new(new TestSyncConfig(), feed, downloader, pool,
            new StaticPeerAllocationStrategyFactory<BlocksRequest>(FirstFree.Instance), LimboLogs.Instance);
        feed.Activate();
        Task running = dispatcher.Start(shutdown.Token);
        try
        {
            await Task.WhenAny(running, reported.Task).WaitAsync(token);
            Assert.That(reported.Task.IsCompleted, Is.True);
        }
        finally
        {
            await shutdown.CancelAsync();
            await running;
        }

        using (Assert.EnterMultipleScope())
        {
            foreach ((int bit, AllocationContexts context) in new[]
            {
                (1, AllocationContexts.Bodies), (2, AllocationContexts.Receipts), (4, AllocationContexts.BlockAccessLists)
            })
            {
                if (weak) pool.Received((requestedParts & bit) != 0 ? 1 : 0).ReportWeakPeer(peer, context);
                else pool.Received((requestedParts & bit) != 0 ? 1 : 0).ReportNoSyncProgress(peer, context);
                Assert.That(peer.HasWeakness(context), Is.EqualTo((requestedParts & bit) != 0));
                Assert.That(peer.HasNoProgress(context), Is.EqualTo(!weak && (requestedParts & bit) != 0));
            }
            if (weak) pool.DidNotReceive().ReportWeakPeer(peer, AllocationContexts.Blocks);
            else pool.DidNotReceive().ReportNoSyncProgress(peer, AllocationContexts.Blocks);
        }
        if (weak)
        {
            using BlocksRequest second = new() { DownloadTask = Task.CompletedTask };
            if ((requestedParts & 1) != 0) second.BodiesRequests = new ArrayPoolList<BlockHeader>([header]);
            if ((requestedParts & 2) != 0) second.ReceiptsRequests = new ArrayPoolList<BlockHeader>([header]);
            if ((requestedParts & 4) != 0) second.BlockAccessListsRequests = new ArrayPoolList<BlockHeader>([header]);
            await DispatchOnce(second, downloader, pool, AllocationContexts.Blocks | AllocationContexts.BlockAccessLists, token,
                handleResponse: (response, _) =>
                {
                    response.Dispose();
                    return SyncResponseHandlingResult.LesserQuality;
                });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.IsAsleep(AllocationContexts.Bodies), Is.EqualTo((requestedParts & 1) != 0));
                Assert.That(peer.IsAsleep(AllocationContexts.Receipts), Is.EqualTo((requestedParts & 2) != 0));
                Assert.That(peer.IsAsleep(AllocationContexts.BlockAccessLists), Is.EqualTo((requestedParts & 4) != 0));
            }
        }
    }

    [Test, CancelAfter(30000)]
    public async Task Allocated_transport_timeouts_are_counted_once_and_caller_cancellation_is_neutral(
        [Values(AllocationContexts.Headers, AllocationContexts.Bodies, AllocationContexts.Receipts)] AllocationContexts context,
        [Values] bool canceledTransport, [Values] bool callerCancellation, CancellationToken token)
    {
        using IContainer node = SyncPeerPoolTests.CreateNode();
        IBlockTree tree = node.Resolve<IBlockTree>();
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        tree.Insert(header, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        ISyncPeer remote = NSubstitute.Substitute.For<ISyncPeer>();
        remote.Node.Returns(new Nethermind.Stats.Model.Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        remote.HeadHash = header.Hash!;
        remote.HeadNumber = header.Number;
        remote.ProtocolVersion.Returns((byte)69);
        Action beforeFailure = () => { };
        remote.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Fail<IOwnedReadOnlyList<BlockHeader>?>(beforeFailure, canceledTransport));
        remote.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Fail<OwnedBlockBodies>(beforeFailure, canceledTransport));
        remote.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Fail<IOwnedReadOnlyList<TxReceipt[]?>>(beforeFailure, canceledTransport));
        pool.Start();
        pool.AddPeer(remote);
        PeerInfo peer = pool.GetPeer(remote.Node)!;
        for (int attempt = 1; attempt <= (callerCancellation ? 1 : 3); attempt++)
        {
            using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
            beforeFailure = () => { if (callerCancellation) shutdown.Cancel(); };
            BlockInfo info = new(header.Hash!, UInt256.Zero) { BlockNumber = header.Number };
            switch (context)
            {
                case AllocationContexts.Headers:
                    await Dispatch(new HeadersSyncBatch { StartNumber = 100, RequestSize = 1 }, new HeadersSyncDownloader(LimboLogs.Instance));
                    break;
                case AllocationContexts.Bodies:
                    await Dispatch(new BodiesSyncBatch([info]), new BodiesSyncDownloader(LimboLogs.Instance));
                    break;
                case AllocationContexts.Receipts:
                    await Dispatch(new ReceiptsSyncBatch([info]), new ReceiptsSyncDispatcher(LimboLogs.Instance));
                    break;
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.HasWeakness(context), Is.EqualTo(!callerCancellation));
                Assert.That(peer.HasNoProgress(context), Is.EqualTo(!callerCancellation && attempt == 3));
                Assert.That(peer.HasNoProgress(AllocationContexts.All & ~context), Is.False);
            }

            async Task Dispatch<T>(T request, ISyncDownloader<T> downloader) where T : FastBlocksBatch
            {
                OneBatchFeed<T> feed = new(request, context);
                await using SyncDispatcher<T> dispatcher = new(new TestSyncConfig(), feed, downloader, pool,
                    new StaticPeerAllocationStrategyFactory<T>(FirstFree.Instance), LimboLogs.Instance);
                feed.Activate();
                Task running = dispatcher.Start(shutdown.Token);
                try
                {
                    Task reached = await Task.WhenAny(running, feed.Handled.Task).WaitAsync(token);
                    if (!callerCancellation) Assert.That(reached, Is.SameAs(feed.Handled.Task));
                }
                finally
                {
                    await shutdown.CancelAsync();
                    await running;
                    request.Dispose();
                }
            }
        }

        static Task<T> Fail<T>(Action before, bool canceled)
        {
            before();
            return Task.FromException<T>(canceled ? new TaskCanceledException() : new TimeoutException());
        }
    }

    internal static async Task DispatchOnce<T>(T request, ISyncDownloader<T> downloader, ISyncPeerPool pool,
        AllocationContexts context, CancellationToken token, ISyncFeed<T?>? productionFeed = null,
        Func<T, PeerInfo?, SyncResponseHandlingResult>? handleResponse = null)
    {
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
        OneBatchFeed<T> feed = new(request, context, productionFeed: productionFeed, handleResponse: handleResponse);
        await using SyncDispatcher<T> dispatcher = new(new TestSyncConfig(), feed, downloader, pool,
            new StaticPeerAllocationStrategyFactory<T>(FirstFree.Instance), LimboLogs.Instance);
        feed.Activate();
        Task running = dispatcher.Start(shutdown.Token);
        try
        {
            await Task.WhenAny(running, feed.Accounted.Task).WaitAsync(token);
            if (running.IsCompleted) await running;
            Assert.That(feed.Accounted.Task.IsCompleted, Is.True, "the dispatcher must finish response accounting before continuing");
        }
        finally
        {
            await shutdown.CancelAsync();
            await running;
        }
    }

    private sealed class OneBatchFeed<T>(T request, AllocationContexts context,
        SyncResponseHandlingResult result = SyncResponseHandlingResult.Emptish, bool disposeResponse = false,
        ISyncFeed<T?>? productionFeed = null, Func<T, PeerInfo?, SyncResponseHandlingResult>? handleResponse = null) : SyncFeed<T>
    {
        private bool _issued;
        public TaskCompletionSource Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Accounted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool IsMultiFeed => false;
        public override AllocationContexts Contexts => context;
        public override bool IsFinished => false;
        public override string FeedName => nameof(OneBatchFeed<T>);
        public override void SyncModeSelectorOnChanged(SyncMode current) { }
        public override async Task<T> PrepareRequest(CancellationToken token = default)
        {
            if (!_issued)
            {
                _issued = true;
                return request;
            }
            Accounted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return default!;
        }
        public override SyncResponseHandlingResult HandleResponse(T response, PeerInfo? peer = null)
        {
            if (disposeResponse && response is IDisposable disposable) disposable.Dispose();
            Handled.TrySetResult();
            return productionFeed?.HandleResponse(response, peer) ?? handleResponse?.Invoke(response, peer) ?? result;
        }
    }

}
