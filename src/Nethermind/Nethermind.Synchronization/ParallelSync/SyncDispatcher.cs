// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public abstract class SyncDispatcher<T>
    {
        private readonly object _feedStateManipulation = new();
        private SyncFeedState _currentFeedState = SyncFeedState.Dormant;

        private IPeerAllocationStrategyFactory<T> PeerAllocationStrategyFactory { get; }

        protected ILogger Logger { get; }
        protected ISyncFeed<T> Feed { get; }
        protected ISyncPeerPool SyncPeerPool { get; }

        private readonly SemaphoreSlim _concurrentProcessingSemaphore;

        protected SyncDispatcher(
            int maxNumberOfProcessingThread,
            ISyncFeed<T>? syncFeed,
            ISyncPeerPool? syncPeerPool,
            IPeerAllocationStrategyFactory<T>? peerAllocationStrategy,
            ILogManager? logManager)
        {
            Logger = logManager?.GetClassLogger<SyncDispatcher<T>>() ?? throw new ArgumentNullException(nameof(logManager));
            Feed = syncFeed ?? throw new ArgumentNullException(nameof(syncFeed));
            SyncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            PeerAllocationStrategyFactory = peerAllocationStrategy ?? throw new ArgumentNullException(nameof(peerAllocationStrategy));

            if (maxNumberOfProcessingThread == 0)
            {
                _concurrentProcessingSemaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
            }
            else
            {
                _concurrentProcessingSemaphore = new SemaphoreSlim(maxNumberOfProcessingThread, maxNumberOfProcessingThread);
            }

            syncFeed.StateChanged += SyncFeedOnStateChanged;
        }

        private TaskCompletionSource<object?>? _dormantStateTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected abstract Task Dispatch(PeerInfo peerInfo, T request, CancellationToken cancellationToken);

        public async Task Start(CancellationToken cancellationToken)
        {
            UpdateState(Feed.CurrentState);
            while (true)
            {
                try
                {
                    SyncFeedState currentStateLocal;
                    TaskCompletionSource<object?>? dormantTaskLocal;
                    lock (_feedStateManipulation)
                    {
                        currentStateLocal = _currentFeedState;
                        dormantTaskLocal = _dormantStateTask;
                    }

                    if (currentStateLocal == SyncFeedState.Dormant)
                    {
                        if (Logger.IsDebug) Logger.Debug($"{GetType().Name} is going to sleep.");
                        if (dormantTaskLocal is null)
                        {
                            if (Logger.IsWarn) Logger.Warn("Dormant task is NULL when trying to await it");
                        }

                        await (dormantTaskLocal?.Task ?? Task.CompletedTask).WaitAsync(cancellationToken);
                        if (Logger.IsDebug) Logger.Debug($"{GetType().Name} got activated.");
                    }
                    else if (currentStateLocal == SyncFeedState.Active)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        T request = await (Feed.PrepareRequest(cancellationToken) ?? Task.FromResult<T>(default!)); // just to avoid null refs
                        if (request is null)
                        {
                            if (!Feed.IsMultiFeed)
                            {
                                if (Logger.IsTrace) Logger.Trace($"{Feed.GetType().Name} enqueued a null request.");
                            }

                            await Task.Delay(10, cancellationToken);
                            continue;
                        }

                        SyncPeerAllocation allocation = await Allocate(request);
                        PeerInfo? allocatedPeer = allocation.Current;
                        if (Logger.IsTrace) Logger.Trace($"Allocated peer: {allocatedPeer}");
                        if (allocatedPeer is not null)
                        {
                            if (Logger.IsTrace) Logger.Trace($"SyncDispatcher request: {request}, AllocatedPeer {allocation.Current}");

                            // Use Task.Run to make sure it queues it instead of running part of it synchronously.
                            Task task = Task.Run(() => DoDispatch(cancellationToken, allocatedPeer, request,
                                allocation), cancellationToken);

                            if (!Feed.IsMultiFeed)
                            {
                                if (Logger.IsDebug) Logger.Debug($"Awaiting single dispatch from {Feed.GetType().Name} with allocated {allocatedPeer}");
                                await task;
                                if (Logger.IsDebug) Logger.Debug($"Single dispatch from {Feed.GetType().Name} with allocated {allocatedPeer} has been processed");
                            }
                        }
                        else
                        {
                            Logger.Debug($"DISPATCHER - {this.GetType().Name}: peer NOT allocated");
                            DoHandleResponse(request);
                        }
                    }
                    else if (currentStateLocal == SyncFeedState.Finished)
                    {
                        if (Logger.IsInfo) Logger.Info($"{GetType().Name} has finished work.");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    Feed.Finish();
                }
            }
        }

        private async Task DoDispatch(CancellationToken cancellationToken, PeerInfo? allocatedPeer, T request,
            SyncPeerAllocation allocation)
        {
            try
            {
                await Dispatch(allocatedPeer, request, cancellationToken);
            }
            catch (ConcurrencyLimitReachedException)
            {
                if (Logger.IsDebug) Logger.Debug($"{request} - concurrency limit reached. Peer: {allocatedPeer}");
            }
            catch (OperationCanceledException)
            {
                if (Logger.IsTrace) Logger.Debug($"{request} - Operation was canceled");
            }
            catch (Exception e)
            {
                if (Logger.IsWarn) Logger.Warn($"Failure when executing request {e}");
            }

            if (Feed.IsMultiFeed)
            {
                // Limit multithreaded feed concurrency. Note, this also blocks freeing the allocation, which is deliberate.
                // otherwise, we will keep spawning requests without processing it fast enough, which consume memory.
                await _concurrentProcessingSemaphore.WaitAsync(cancellationToken);
            }

            Free(allocation);

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (Logger.IsDebug) Logger.Debug("Ignoring sync response as shutdown is requested.");
                    return;
                }

                DoHandleResponse(request, allocatedPeer);
            }
            finally
            {
                if (Feed.IsMultiFeed)
                {
                    _concurrentProcessingSemaphore.Release();
                }
            }
        }

        private void DoHandleResponse(T request, PeerInfo? allocatedPeer = null)
        {
            try
            {
                SyncResponseHandlingResult result = Feed.HandleResponse(request, allocatedPeer);
                ReactToHandlingResult(request, result, allocatedPeer);
            }
            catch (ObjectDisposedException)
            {
                if (Logger.IsInfo) Logger.Info("Ignoring sync response as the DB has already closed.");
            }
            catch (Exception e)
            {
                // possibly clear the response and handle empty response batch here (to avoid missing parts)
                // this practically corrupts sync
                if (Logger.IsError) Logger.Error("Error when handling response", e);
            }
        }

        protected virtual void Free(SyncPeerAllocation allocation)
        {
            SyncPeerPool.Free(allocation);
        }

        protected virtual async Task<SyncPeerAllocation> Allocate(T request)
        {
            SyncPeerAllocation allocation = await SyncPeerPool.Allocate(PeerAllocationStrategyFactory.Create(request), Feed.Contexts, 1000);
            return allocation;
        }

        private void ReactToHandlingResult(T request, SyncResponseHandlingResult result, PeerInfo? peer)
        {
            if (peer is not null)
            {
                switch (result)
                {
                    case SyncResponseHandlingResult.Emptish:
                        break;
                    case SyncResponseHandlingResult.Ignored:
                        break;
                    case SyncResponseHandlingResult.LesserQuality:
                        SyncPeerPool.ReportWeakPeer(peer, Feed.Contexts);
                        break;
                    case SyncResponseHandlingResult.NoProgress:
                        SyncPeerPool.ReportNoSyncProgress(peer, Feed.Contexts);
                        break;
                    case SyncResponseHandlingResult.NotAssigned:
                        break;
                    case SyncResponseHandlingResult.InternalError:
                        Logger.Error($"Feed {Feed} has reported an internal error when handling {request}");
                        break;
                    case SyncResponseHandlingResult.OK:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(result), result, null);
                }
            }
        }

        private void SyncFeedOnStateChanged(object? sender, SyncFeedStateEventArgs e)
        {
            SyncFeedState state = e.NewState;
            UpdateState(state);
        }

        private void UpdateState(SyncFeedState state)
        {
            lock (_feedStateManipulation)
            {
                if (_currentFeedState != state)
                {
                    if (Logger.IsDebug) Logger.Debug($"{Feed.GetType().Name} state changed to {state}");

                    _currentFeedState = state;
                    TaskCompletionSource<object?>? newDormantStateTask = null;
                    if (state == SyncFeedState.Dormant)
                    {
                        newDormantStateTask = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    var previous = Interlocked.Exchange(ref _dormantStateTask, newDormantStateTask);
                    previous?.TrySetResult(null);
                }
            }
        }
    }
}
