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
/// returning null. Replaces <c>SyncDispatcher</c> for snap+state sync where sequential
/// execution is required.
/// </summary>
public class SimpleDispatcher<T>(
    ISimpleSyncFeed<T> feed,
    ISyncDownloader<T> downloader,
    IPeerAllocationStrategyFactory<T> strategyFactory,
    AllocationContexts contexts,
    ISyncPeerPool peerPool,
    ISyncConfig syncConfig,
    ILogManager logManager,
    int maxConcurrency = 0) where T : class
{
    private readonly ILogger _logger = logManager.GetClassLogger<SimpleDispatcher<T>>();
    private readonly int _allocateTimeoutMs = syncConfig.SyncDispatcherAllocateTimeoutMs;
    private readonly string _feedName = feed.GetType().Name;
    private readonly int _maxConcurrency = ResolveMaxConcurrency(syncConfig, maxConcurrency);

    public async Task Run(CancellationToken token)
    {
        int maxThreads = _maxConcurrency;
        SemaphoreSlim semaphore = new(maxThreads, maxThreads);

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

            await semaphore.WaitAsync(token);
            _ = Task.Run(async () =>
            {
                try
                {
                    await DoDispatch(request, peer, allocation, token);
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }

        // Wait for in-flight tasks to complete. Drain with CancellationToken.None so that
        // peer allocations are always freed in DoDispatch even when the caller cancels.
        for (int i = 0; i < maxThreads; i++)
            await semaphore.WaitAsync(CancellationToken.None);
    }

    private async Task DoDispatch(
        T request,
        PeerInfo peer,
        SyncPeerAllocation allocation,
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

        peerPool.Free(allocation);

        if (token.IsCancellationRequested) return;

        HandleResponse(request, peer);
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

    private static int ResolveMaxConcurrency(ISyncConfig syncConfig, int maxConcurrency)
    {
        if (maxConcurrency > 0)
        {
            return maxConcurrency;
        }

        return syncConfig.MaxProcessingThreads == 0
            ? Environment.ProcessorCount
            : syncConfig.MaxProcessingThreads;
    }
}
