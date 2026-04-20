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
/// A lightweight dispatcher that runs feeds sequentially via <see cref="RunFeed{T}"/>,
/// with the ability to wait for specific sync modes from <see cref="ISyncModeSelector"/>.
/// Replaces SyncDispatcher for snap+state sync to guarantee sequential execution.
/// </summary>
public class SimpleDispatcher(
    ISyncPeerPool peerPool,
    ISyncConfig syncConfig,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<SimpleDispatcher>();
    private readonly TimeSpan _emptyRequestDelay = TimeSpan.FromMilliseconds(syncConfig.SyncDispatcherEmptyRequestDelayMs);
    private readonly int _allocateTimeoutMs = syncConfig.SyncDispatcherAllocateTimeoutMs;

    public async Task RunFeed<T>(
        ISimpleSyncFeed<T> feed,
        ISyncDownloader<T> downloader,
        IPeerAllocationStrategyFactory<T> strategyFactory,
        AllocationContexts contexts,
        CancellationToken token)
    {
        int maxThreads = syncConfig.MaxProcessingThreads == 0
            ? Environment.ProcessorCount
            : syncConfig.MaxProcessingThreads;
        SemaphoreSlim semaphore = new(maxThreads, maxThreads);
        string feedName = feed.GetType().Name;

        while (!token.IsCancellationRequested)
        {
            long prepareTime = Stopwatch.GetTimestamp();
            T? request = await feed.PrepareRequest(token);
            Metrics.SyncDispatcherPrepareRequestTimeMicros.Observe(
                Stopwatch.GetElapsedTime(prepareTime).TotalMicroseconds, new StringLabel(feedName));

            if (request is null)
                break;

            SyncPeerAllocation allocation = await peerPool.Allocate(
                strategyFactory.Create(request), contexts, _allocateTimeoutMs, token);
            PeerInfo? peer = allocation.Current;

            if (peer is null)
            {
                HandleResponse(feed, request, null, feedName);
                continue;
            }

            await semaphore.WaitAsync(token);
            _ = Task.Run(async () =>
            {
                try
                {
                    await DoDispatch(feed, downloader, request, peer, allocation, contexts, feedName, token);
                }
                finally
                {
                    semaphore.Release();
                }
            }, token);
        }

        // Wait for in-flight tasks to complete. Drain with CancellationToken.None so that
        // peer allocations are always freed in DoDispatch even when the caller cancels.
        for (int i = 0; i < maxThreads; i++)
            await semaphore.WaitAsync(CancellationToken.None);
    }

    private async Task DoDispatch<T>(
        ISimpleSyncFeed<T> feed,
        ISyncDownloader<T> downloader,
        T request,
        PeerInfo peer,
        SyncPeerAllocation allocation,
        AllocationContexts contexts,
        string feedName,
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
            Stopwatch.GetElapsedTime(dispatchTime).TotalMicroseconds, new StringLabel(feedName));

        peerPool.Free(allocation);

        if (token.IsCancellationRequested) return;

        HandleResponse(feed, request, peer, feedName, contexts);
    }

    private void HandleResponse<T>(
        ISimpleSyncFeed<T> feed,
        T request,
        PeerInfo? peer,
        string feedName,
        AllocationContexts contexts = default)
    {
        long handleTime = Stopwatch.GetTimestamp();
        try
        {
            SyncResponseHandlingResult result = feed.HandleResponse(request, peer);
            ReactToHandlingResult(result, peer, contexts);
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
                Stopwatch.GetElapsedTime(handleTime).TotalMicroseconds, new StringLabel(feedName));
        }
    }

    private void ReactToHandlingResult(SyncResponseHandlingResult result, PeerInfo? peer, AllocationContexts contexts)
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
