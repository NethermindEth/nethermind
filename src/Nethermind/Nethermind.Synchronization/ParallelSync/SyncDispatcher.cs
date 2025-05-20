// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public class SyncDispatcher<T> : IAsyncDisposable
    {
        private readonly Lock _feedStateManipulation = new();
        private SyncFeedState _currentFeedState = SyncFeedState.Dormant;
        private static readonly TimeSpan ActiveTaskDisposeTimeout = TimeSpan.FromSeconds(10);

        private IPeerAllocationStrategyFactory<T> PeerAllocationStrategyFactory { get; }
        private ILogger Logger { get; }
        private ISyncFeed<T> Feed { get; }
        private ISyncDownloader<T> Downloader { get; }
        private ISyncPeerPool SyncPeerPool { get; }

        private readonly CountdownEvent _activeTasks = new CountdownEvent(1);
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly SemaphoreSlim _concurrentProcessingSemaphore;
        private readonly TimeSpan _emptyRequestDelay;
        private readonly int _allocateTimeoutMs;

        private bool _disposed = false;

        public SyncDispatcher(
            ISyncConfig syncConfig,
            ISyncFeed<T>? syncFeed,
            ISyncDownloader<T>? downloader,
            ISyncPeerPool? syncPeerPool,
            IPeerAllocationStrategyFactory<T>? peerAllocationStrategy,
            ILogManager? logManager)
        {
            Logger = logManager?.GetClassLogger<SyncDispatcher<T>>() ?? throw new ArgumentNullException(nameof(logManager));
            Feed = syncFeed ?? throw new ArgumentNullException(nameof(syncFeed));
            Downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            SyncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            PeerAllocationStrategyFactory = peerAllocationStrategy ?? throw new ArgumentNullException(nameof(peerAllocationStrategy));

            int maxNumberOfProcessingThread = syncConfig.MaxProcessingThreads;
            if (maxNumberOfProcessingThread == 0)
            {
                _concurrentProcessingSemaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
            }
            else
            {
                _concurrentProcessingSemaphore = new SemaphoreSlim(maxNumberOfProcessingThread, maxNumberOfProcessingThread);
            }

            _emptyRequestDelay = TimeSpan.FromMilliseconds(syncConfig.SyncDispatcherEmptyRequestDelayMs);
            _allocateTimeoutMs = syncConfig.SyncDispatcherAllocateTimeoutMs;

            syncFeed.StateChanged += SyncFeedOnStateChanged;
        }

        private TaskCompletionSource<object?>? _dormantStateTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Start(CancellationToken cancellationToken)
        {
            UpdateState(Feed.CurrentState);

            try
            {
                _activeTasks.AddCount(1);
                using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
                await DispatchLoop(linkedSource.Token);
            }
            finally
            {
                _activeTasks.Signal();
            }
        }

        private async Task DispatchLoop(CancellationToken cancellationToken)
        {
            bool wasCancelTriggered = false;
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
                        if (Logger.IsDebug) Logger.Debug($"{GetType().NameWithGenerics()} is going to sleep.");
                        if (dormantTaskLocal is null)
                        {
                            if (Logger.IsWarn) Logger.Warn("Dormant task is NULL when trying to await it");
                        }

                        await (dormantTaskLocal?.Task ?? Task.CompletedTask).WaitAsync(cancellationToken);
                        if (Logger.IsDebug) Logger.Debug($"{GetType().NameWithGenerics()} got activated.");
                    }
                    else if (currentStateLocal == SyncFeedState.Active)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        T request = await (Feed.PrepareRequest(cancellationToken) ?? Task.FromResult<T>(default!)); // just to avoid null refs
                        if (request is null)
                        {
                            if (!Feed.IsMultiFeed)
                            {
                                if (Logger.IsTrace) Logger.Trace($"{Feed.GetType().NameWithGenerics()} enqueued a null request.");
                            }

                            await Task.Delay(_emptyRequestDelay, cancellationToken);
                            continue;
                        }

                        SyncPeerAllocation allocation = await Allocate(request, cancellationToken);
                        PeerInfo? allocatedPeer = allocation.Current;
                        if (Logger.IsTrace) Logger.Trace($"Allocated peer: {allocatedPeer}");
                        if (allocatedPeer is not null)
                        {
                            if (Logger.IsTrace) Logger.Trace($"SyncDispatcher request: {request}, AllocatedPeer {allocation.Current}");

                            // Use Task.Run to make sure it queues it instead of running part of it synchronously.
                            _activeTasks.AddCount();

                            Task task = Task.Run(
                                () =>
                                {
                                    try
                                    {
                                        return DoDispatch(cancellationToken, allocatedPeer, request, allocation);
                                    }
                                    finally
                                    {
                                        _activeTasks.Signal();
                                    }
                                });

                            if (!Feed.IsMultiFeed)
                            {
                                if (Logger.IsDebug) Logger.Debug($"Awaiting single dispatch from {Feed.GetType().NameWithGenerics()} with allocated {allocatedPeer}");
                                await task;
                                if (Logger.IsDebug) Logger.Debug($"Single dispatch from {Feed.GetType().NameWithGenerics()} with allocated {allocatedPeer} has been processed");
                            }
                        }
                        else
                        {
                            Logger.Debug($"DISPATCHER - {GetType().NameWithGenerics()}: peer NOT allocated");
                            DoHandleResponse(request);
                        }
                    }
                    else if (currentStateLocal == SyncFeedState.Finished)
                    {
                        if (Logger.IsInfo) Logger.Info($"{GetType().NameWithGenerics()} has finished work.");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (wasCancelTriggered)
                        throw new InvalidOperationException($"{Feed} did not switch to finished after `Feed.Finish` on cancel");
                    wasCancelTriggered = true;
                    Feed.Finish();
                }
            }
        }

        private async Task DoDispatch(CancellationToken cancellationToken, PeerInfo? allocatedPeer, T request,
            SyncPeerAllocation allocation)
        {
            try
            {
                await Downloader.Dispatch(allocatedPeer, request, cancellationToken);
            }
            catch (ConcurrencyLimitReachedException)
            {
                if (Logger.IsDebug) Logger.Debug($"{request} - concurrency limit reached. Peer: {allocatedPeer}");
            }
            catch (TimeoutException)
            {
                if (Logger.IsDebug) Logger.Debug($"{request} - timed out. Peer: {allocatedPeer}");
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

        private void Free(SyncPeerAllocation allocation)
        {
            SyncPeerPool.Free(allocation);
        }

        protected async Task<SyncPeerAllocation> Allocate(T request, CancellationToken cancellationToken)
        {
            return await SyncPeerPool.Allocate(PeerAllocationStrategyFactory.Create(request), Feed.Contexts, _allocateTimeoutMs, cancellationToken);
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

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, true, false))
            {
                return;
            }

            await _cancellationTokenSource.CancelAsync();
            _activeTasks.Signal();
            if (!_activeTasks.Wait(ActiveTaskDisposeTimeout))
            {
                if (Logger.IsWarn) Logger.Warn($"Timeout on waiting for active tasks for feed {Feed.GetType().Name} {_activeTasks.CurrentCount}");
            }
            _cancellationTokenSource.Dispose();
            _concurrentProcessingSemaphore.Dispose();
        }
    }
}
