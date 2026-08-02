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
/// dispatches is bounded by <see cref="InFlightRequestsPerProcessingThread"/> ×
/// <see cref="ISyncConfig.MaxProcessingThreads"/> and by peer availability
/// (<see cref="ISyncPeerPool.Allocate"/>); response processing
/// (<see cref="ISimpleSyncFeed{T}.HandleResponse"/>) is bounded by
/// <see cref="ISyncConfig.MaxProcessingThreads"/> plus one — a failed allocation is handled on
/// the loop thread without taking a slot. A dispatch holds its in-flight slot until its
/// response is handled and its peer allocation until a processing slot is acquired, so
/// transient response memory is capped at roughly the in-flight bound × max response size.
/// <see cref="Run"/> returns only after every
/// in-flight dispatch has completed — including when cancelled — because callers treat that
/// return as the sole barrier before resetting or finalizing feed state.
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
    /// <summary>Bound on concurrent in-flight dispatches, as a multiple of <see cref="ISyncConfig.MaxProcessingThreads"/>.</summary>
    /// <remarks>
    /// The latency-targeting request sizer aims for seconds-long round trips, so saturating every
    /// available peer inflates observed latency and shrinks ranges, raising per-byte overhead. A
    /// small multiple of processing capacity keeps ranges large while still overlapping network
    /// waits with response processing.
    /// </remarks>
    private const int InFlightRequestsPerProcessingThread = 2;

    private readonly ILogger _logger = logManager.GetClassLogger<SimpleDispatcher<T>>();
    private readonly int _allocateTimeoutMs = syncConfig.SyncDispatcherAllocateTimeoutMs;
    private readonly string _feedName = feed.GetType().Name;

    public async Task Run(CancellationToken token)
    {
        int maxThreads = syncConfig.MaxProcessingThreads == 0
            ? Environment.ProcessorCount
            : syncConfig.MaxProcessingThreads;
        int maxInFlight = InFlightRequestsPerProcessingThread * maxThreads;
        using SemaphoreSlim processingSemaphore = new(maxThreads, maxThreads);
        using SemaphoreSlim inFlightSemaphore = new(maxInFlight, maxInFlight);

        // Counts the Run loop itself plus every in-flight DoDispatch; the loop releases its
        // slot once it stops producing so the drain cannot complete while dispatches remain.
        int inFlight = 1;
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void SignalDispatchCompleted()
        {
            if (Interlocked.Decrement(ref inFlight) == 0) drained.TrySetResult();
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

                try
                {
                    await inFlightSemaphore.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    // Mirror a cancelled Allocate: hand the request back to the feed as a
                    // null-peer response instead of dropping it.
                    HandleResponse(request, null);
                    continue;
                }

                bool inFlightSlotTransferred = false;
                try
                {
                    SyncPeerAllocation allocation = await peerPool.Allocate(
                        strategyFactory.Create(request), contexts, _allocateTimeoutMs, token);
                    PeerInfo? peer = allocation.Current;

                    if (peer is null)
                    {
                        HandleResponse(request, null);
                        continue;
                    }

                    Interlocked.Increment(ref inFlight);
                    try
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await DoDispatch(request, peer, allocation, processingSemaphore, token);
                            }
                            finally
                            {
                                inFlightSemaphore.Release();
                                SignalDispatchCompleted();
                            }
                        });
                        inFlightSlotTransferred = true;
                    }
                    catch
                    {
                        // If Task.Run itself fails the dispatch never runs: undo its in-flight count
                        // (or the drain below wedges forever) and free the peer it would have freed.
                        SignalDispatchCompleted();
                        peerPool.Free(allocation);
                        throw;
                    }
                }
                finally
                {
                    // The in-flight slot follows the in-flight counter: a started dispatch returns
                    // it in its own finally; every path that never starts one returns it here.
                    if (!inFlightSlotTransferred) inFlightSemaphore.Release();
                }
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

        // When already cancelled, free the peer without queueing for a processing slot — on
        // shutdown a full peer-pool's worth of dispatches would otherwise serialise in batches
        // of maxThreads behind HandleResponse DB work just to reach Free.
        if (token.IsCancellationRequested)
        {
            peerPool.Free(allocation);
            return;
        }

        // Acquire a processing slot before freeing the peer so that unprocessed responses stay
        // bounded by peer count. Waiting without the caller token cannot deadlock — slots are
        // held only for the synchronous HandleResponse and always released — and it guarantees
        // the allocation below is freed even when the caller cancels mid-wait.
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
