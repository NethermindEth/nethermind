// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Attributes;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync;

/// <summary>
/// A lightweight dispatcher bound to a single <see cref="ISimpleSyncFeed{T}"/>. Drives
/// the feed to completion: repeatedly calls <see cref="ISimpleSyncFeed{T}.PrepareRequest"/>,
/// dispatches each batch through the supplied downloader on a peer allocated from the pool,
/// and hands the response back to the feed. Returns when the feed signals completion by
/// returning null. Replaces <c>SyncDispatcher</c> for snap+state sync where the feeds run
/// as sequential phases (snap, then state) driven by their runners.
/// </summary>
/// <remarks>
/// Concurrency model: <see cref="ISimpleSyncFeed{T}.PrepareRequest"/> is called only from the
/// single <see cref="Run"/> loop and is never concurrent with itself. The number of in-flight
/// network requests is bounded by peer availability (<see cref="ISyncPeerPool.Allocate"/>),
/// not by CPU count; only response processing (<see cref="ISimpleSyncFeed{T}.HandleResponse"/>)
/// is bounded by <see cref="ISyncConfig.MaxProcessingThreads"/>. A downloaded response holds its
/// peer allocation until a processing slot is acquired, so unprocessed responses stay bounded
/// by peer count. <see cref="Run"/> returns only after every in-flight dispatch has completed —
/// including when cancelled — because callers treat that return as the sole barrier before
/// resetting or finalizing feed state.
/// </remarks>
public class SimpleDispatcher<T>(
    ISimpleSyncFeed<T> feed,
    ISyncDownloader<T> downloader,
    IPeerAllocationStrategyFactory<T> strategyFactory,
    AllocationContexts contexts,
    ISyncPeerPool peerPool,
    ISyncConfig syncConfig,
    ILogManager logManager) where T : class
{
    private readonly ILogger _logger = logManager.GetClassLogger<SimpleDispatcher<T>>();
    private readonly int _allocateTimeoutMs = syncConfig.SyncDispatcherAllocateTimeoutMs;
    private readonly string _feedName = feed.GetType().Name;

    public async Task Run(CancellationToken token)
    {
        int maxThreads = syncConfig.MaxProcessingThreads == 0
            ? Environment.ProcessorCount
            : syncConfig.MaxProcessingThreads;
        using SemaphoreSlim processingSemaphore = new(maxThreads, maxThreads);

        // Counts the Run loop itself plus every in-flight DoDispatch; the loop releases its
        // slot once it stops producing so the drain cannot complete while dispatches remain.
        int inFlight = 1;
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void SignalDispatchCompleted()
        {
            if (Interlocked.Decrement(ref inFlight) == 0) drained.SetResult();
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                long prepareTime = Stopwatch.GetTimestamp();
                T? request = await feed.PrepareRequest(token);
                Metrics.SyncDispatcherPrepareRequestTimeMicros.Observe(
                    Stopwatch.GetElapsedTime(prepareTime).TotalMicroseconds, new StringLabel(_feedName));

                if (request is null)
                    break;

                SyncPeerAllocation allocation = await peerPool.Allocate(
                    strategyFactory.Create(request), contexts, _allocateTimeoutMs, token);
                PeerInfo? peer = allocation.Current;

                if (peer is null)
                {
                    HandleResponse(request, null);
                    continue;
                }

                Interlocked.Increment(ref inFlight);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DoDispatch(request, peer, allocation, processingSemaphore, token);
                    }
                    finally
                    {
                        SignalDispatchCompleted();
                    }
                });
            }
        }
        finally
        {
            // Wait for in-flight tasks to complete without observing the caller token so that
            // peer allocations are always freed in DoDispatch even when the caller cancels.
            SignalDispatchCompleted();
            await drained.Task;
        }
    }

    private async Task DoDispatch(
        T request,
        PeerInfo peer,
        SyncPeerAllocation allocation,
        SemaphoreSlim processingSemaphore,
        CancellationToken token)
    {
        long dispatchTime = Stopwatch.GetTimestamp();
        try
        {
            await downloader.Dispatch(peer, request, token);
        }
        catch (ConcurrencyLimitReachedException)
        {
            if (_logger.IsDebug) _logger.Debug($"{request} - concurrency limit reached. Peer: {peer}");
        }
        catch (TimeoutException)
        {
            if (_logger.IsDebug) _logger.Debug($"{request} - timed out. Peer: {peer}");
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsTrace) _logger.Trace($"{request} - cancelled");
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Failure when executing request {e}");
        }
        Metrics.SyncDispatcherDispatchTimeMicros.Observe(
            Stopwatch.GetElapsedTime(dispatchTime).TotalMicroseconds, new StringLabel(_feedName));

        // Acquire a processing slot before freeing the peer so that unprocessed responses stay
        // bounded by peer count. Waiting without the caller token cannot deadlock — slots are
        // held only for the synchronous HandleResponse and always released — and it guarantees
        // the allocation below is freed even when the caller cancels.
        await processingSemaphore.WaitAsync(CancellationToken.None);
        try
        {
            peerPool.Free(allocation);

            if (token.IsCancellationRequested) return;

            HandleResponse(request, peer);
        }
        finally
        {
            processingSemaphore.Release();
        }
    }

    private void HandleResponse(T request, PeerInfo? peer)
    {
        long handleTime = Stopwatch.GetTimestamp();
        try
        {
            SyncResponseHandlingResult result = feed.HandleResponse(request, peer);
            ReactToHandlingResult(result, peer);
        }
        catch (ObjectDisposedException)
        {
            if (_logger.IsInfo) _logger.Info("Ignoring sync response as the DB has already closed.");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error when handling response", e);
        }
        finally
        {
            Metrics.SyncDispatcherHandleTimeMicros.Observe(
                Stopwatch.GetElapsedTime(handleTime).TotalMicroseconds, new StringLabel(_feedName));
        }
    }

    private void ReactToHandlingResult(SyncResponseHandlingResult result, PeerInfo? peer)
    {
        if (peer is null) return;

        switch (result)
        {
            case SyncResponseHandlingResult.LesserQuality:
                peerPool.ReportWeakPeer(peer, contexts);
                break;
            case SyncResponseHandlingResult.NoProgress:
                peerPool.ReportNoSyncProgress(peer, contexts);
                break;
            case SyncResponseHandlingResult.InternalError:
                if (_logger.IsError) _logger.Error($"Feed has reported an internal error when handling request");
                break;
        }
    }
}
