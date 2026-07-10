// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.TxPool;

public sealed class RetryCache<TMessage, TResourceId> : IAsyncDisposable
    where TMessage : INew<TResourceId, TMessage>
    where TResourceId : struct, IEquatable<TResourceId>, IHash64bit<TResourceId>
{
    private const int DefaultTimeoutMs = 3500;
    private const int DefaultRequestingCacheSize = 1024;
    private const int DefaultExpiringQueueLimit = 50000;
    private const int DefaultMaxRetryRequests = 4;
    private const int DefaultMaxPendingResourcesPerHandler = 4096;
    private const int MaxRetryResourcesPerTick = 256;
    private const int MaxQueueEntriesPerTick = 4096;

    private readonly int _timeoutMs;
    private readonly CancellationToken _token;
    private readonly int _checkMs;
    private readonly int _expiringQueueLimit;
    private readonly int _maxRetryRequests;
    private readonly int _maxPendingResourcesPerHandler;
    private readonly int _maxPreferredRetryResourcesPerHandlerPerTick;
    private readonly TimeProvider _timeProvider;
    private readonly Task _mainLoopTask;
    private static readonly ObjectPool<HandlerBag<TMessage>> _handlerBagsPool = new DefaultObjectPool<HandlerBag<TMessage>>(new HandlerBagPolicy<TMessage>(), maximumRetained: 512);
    private readonly ConcurrentDictionary<TResourceId, RetryRequestEntry> _retryRequests = new();
    private readonly ConcurrentDictionary<IMessageHandler<TMessage>, int> _pendingResourcesByHandler = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentQueue<(TResourceId ResourceId, RetryRequestEntry Entry, DateTimeOffset ExpiresAfter)> _expiringQueue = new();
    private int _expiringQueueCounter = 0;
    private readonly AssociativeKeyCache<TResourceId> _requestingResources;
    private readonly ILogger _logger;

    internal int ResourcesInRetryQueue => Volatile.Read(ref _expiringQueueCounter);

    private readonly record struct RetryRequestEntry(
        HandlerBag<TMessage> Handlers,
        long Generation,
        long ActivatedAtTimestamp,
        IMessageHandler<TMessage> SourceHandler);

    public RetryCache(
        ILogManager logManager,
        int timeoutMs = DefaultTimeoutMs,
        int requestingCacheSize = DefaultRequestingCacheSize,
        int expiringQueueLimit = DefaultExpiringQueueLimit,
        int maxRetryRequests = DefaultMaxRetryRequests,
        CancellationToken token = default,
        int maxPendingResourcesPerHandler = DefaultMaxPendingResourcesPerHandler)
        : this(
            logManager,
            TimeProvider.System,
            timeoutMs,
            requestingCacheSize,
            expiringQueueLimit,
            maxRetryRequests,
            token,
            maxPendingResourcesPerHandler)
    {
    }

    internal RetryCache(
        ILogManager logManager,
        TimeProvider timeProvider,
        int timeoutMs = DefaultTimeoutMs,
        int requestingCacheSize = DefaultRequestingCacheSize,
        int expiringQueueLimit = DefaultExpiringQueueLimit,
        int maxRetryRequests = DefaultMaxRetryRequests,
        CancellationToken token = default,
        int maxPendingResourcesPerHandler = DefaultMaxPendingResourcesPerHandler)
    {
        _logger = logManager.GetClassLogger(typeof(RetryCache<,>));

        _timeoutMs = timeoutMs;
        _token = token;
        _checkMs = Math.Max(1, _timeoutMs / 5);
        _requestingResources = new(requestingCacheSize);
        _expiringQueueLimit = expiringQueueLimit;
        _maxRetryRequests = maxRetryRequests;
        _maxPendingResourcesPerHandler = maxPendingResourcesPerHandler;
        _maxPreferredRetryResourcesPerHandlerPerTick = Math.Max(
            1,
            MaxRetryResourcesPerTick / Math.Max(1, maxRetryRequests));
        _timeProvider = timeProvider;
        _mainLoopTask = Task.Run(async () =>
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_checkMs), _timeProvider);

            while (await timer.WaitForNextTickAsync(token))
            {
                Dictionary<IBatchMessageHandler<TMessage, TResourceId>, ArrayPoolList<TResourceId>>? batchedRetryRequests = null;
                try
                {
                    int retryResourcesProcessed = 0;
                    int queueEntriesProcessed = 0;
                    int queueEntriesToProcess = Math.Min(ResourcesInRetryQueue, MaxQueueEntriesPerTick);
                    while (!token.IsCancellationRequested
                        && queueEntriesProcessed < queueEntriesToProcess
                        && _expiringQueue.TryPeek(out (TResourceId ResourceId, RetryRequestEntry Entry, DateTimeOffset ExpiresAfter) item)
                        && item.ExpiresAfter <= _timeProvider.GetUtcNow())
                    {
                        if (_expiringQueue.TryDequeue(out item))
                        {
                            queueEntriesProcessed++;
                            bool queueSlotTransferred = false;

                            try
                            {
                                if (_retryRequests.TryGetValue(item.ResourceId, out RetryRequestEntry currentEntry)
                                    && currentEntry == item.Entry)
                                {
                                    if (retryResourcesProcessed >= MaxRetryResourcesPerTick)
                                    {
                                        Enqueue(item.ResourceId, currentEntry);
                                        queueSlotTransferred = true;
                                        continue;
                                    }

                                    BatchedHandlerPreference handlerPreference = new(
                                        batchedRetryRequests,
                                        _maxPreferredRetryResourcesPerHandlerPerTick);
                                    if (currentEntry.Handlers.TryTake(
                                        currentEntry.Generation,
                                        ref handlerPreference,
                                        out IMessageHandler<TMessage>? retryHandler,
                                        out _))
                                    {
                                        retryResourcesProcessed++;
                                        Metrics.AddPendingTransactionRetryHandlersCalledOnTimeout(1);
                                        Metrics.AddPendingTransactionRetryResourcesTimedOutWithHandlers(1);
                                        Metrics.AddPendingTransactionRetryResourcesTimedOutWithHandlersAgeMilliseconds(
                                            GetElapsedMilliseconds(currentEntry.ActivatedAtTimestamp));
                                        ReleaseHandlerSlot(retryHandler!);
                                        Enqueue(item.ResourceId, currentEntry);
                                        queueSlotTransferred = true;
                                        CollectRetryRequest(item.ResourceId, retryHandler!, ref batchedRetryRequests);
                                    }
                                    else
                                    {
                                        _requestingResources.Set(item.ResourceId);
                                        if (!TryRemove(item.ResourceId, currentEntry))
                                        {
                                            _requestingResources.Delete(item.ResourceId);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                if (!queueSlotTransferred)
                                {
                                    Interlocked.Decrement(ref _expiringQueueCounter);
                                }
                            }
                        }
                    }

                    SendBatchedRetryRequests(batchedRetryRequests);
                    batchedRetryRequests = null;
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Unexpected error in {typeof(TResourceId)} retry cache loop", ex);
                    Clear();
                }
                finally
                {
                    DisposeBatchedRetryRequests(batchedRetryRequests);
                }
            }

            if (_logger.IsDebug) _logger.Debug($"{typeof(TResourceId)} retry cache stopped");
        }, token);
    }

    public AnnounceResult Announced(in TResourceId resourceId, IMessageHandler<TMessage> handler)
    {
        if (_token.IsCancellationRequested)
        {
            return AnnounceResult.RequestRequired;
        }

        while (!_requestingResources.Contains(resourceId))
        {
            if (_retryRequests.TryGetValue(resourceId, out RetryRequestEntry existingEntry))
            {
                if (ReferenceEquals(existingEntry.SourceHandler, handler))
                {
                    return AnnounceResult.Delayed;
                }

                bool candidateSlotReserved = TryReserveHandlerSlot(handler);
                if (!candidateSlotReserved)
                {
                    Metrics.AddPendingTransactionRetryHandlersRejectedByLimit(1);
                    _logger.TraceWarn($"{handler} has reached the pending {typeof(TResourceId)} limit, suppressing request");
                    return AnnounceResult.Delayed;
                }

                HandlerBagAddResult addResult;
                try
                {
                    addResult = existingEntry.Handlers.Add(handler, _maxRetryRequests, existingEntry.Generation);
                    if (addResult is HandlerBagAddResult.Added)
                    {
                        candidateSlotReserved = false;
                    }
                }
                finally
                {
                    if (candidateSlotReserved)
                    {
                        ReleaseHandlerSlot(handler);
                    }
                }

                if (addResult is HandlerBagAddResult.Full)
                {
                    Metrics.AddPendingTransactionRetryHandlersRejectedByLimit(1);
                }

                if (addResult is not HandlerBagAddResult.Inactive)
                {
                    return AnnounceResult.Delayed;
                }

                if (_retryRequests.TryGetValue(resourceId, out RetryRequestEntry currentEntry)
                    && currentEntry == existingEntry)
                {
                    return AnnounceResult.RequestRequired;
                }

                continue;
            }

            HandlerBag<TMessage>? newBag = null;
            bool published = false;
            bool queueSlotReserved = false;
            bool handlerSlotReserved = TryReserveHandlerSlot(handler);
            if (!handlerSlotReserved)
            {
                Metrics.AddPendingTransactionRetryHandlersRejectedByLimit(1);
                _logger.TraceWarn($"{handler} has reached the pending {typeof(TResourceId)} limit, suppressing request");
                return AnnounceResult.Delayed;
            }

            try
            {
                queueSlotReserved = TryReserveQueueSlot();
                if (!queueSlotReserved)
                {
                    Metrics.AddPendingTransactionRetryQueueFull(1);
                    _logger.TraceWarn($"{typeof(TResourceId)} retry queue is full, bypassing retry tracking");
                    return AnnounceResult.RequestRequired;
                }

                newBag = _handlerBagsPool.Get();
                RetryRequestEntry newEntry = new(newBag, newBag.Activate(), _timeProvider.GetTimestamp(), handler);
                published = _retryRequests.TryAdd(resourceId, newEntry);

                if (published)
                {
                    // First announcer: not added to the retry bag because the caller receives
                    // RequestRequired and will immediately request the resource itself.
                    // Only subsequent announcers (via TryAdd) are registered for retry.
                    Enqueue(resourceId, newEntry);
                    queueSlotReserved = false;
                    handlerSlotReserved = false;
                    return AnnounceResult.RequestRequired;
                }
            }
            finally
            {
                if (queueSlotReserved)
                {
                    Interlocked.Decrement(ref _expiringQueueCounter);
                }

                if (handlerSlotReserved)
                {
                    ReleaseHandlerSlot(handler);
                }

                if (!published && newBag is not null)
                {
                    _handlerBagsPool.Return(newBag);
                }
            }
        }

        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {handler}, but a retry is in progress already, immediately firing");

        return AnnounceResult.RequestRequired;
    }

    private void Enqueue(TResourceId resourceId, RetryRequestEntry entry) =>
        _expiringQueue.Enqueue((resourceId, entry, _timeProvider.GetUtcNow().AddMilliseconds(_timeoutMs)));

    public void Received(in TResourceId resourceId)
    {
        if (_logger.IsTrace) _logger.Trace($"Received {resourceId}");

        if (_retryRequests.TryRemove(resourceId, out RetryRequestEntry entry))
        {
            int skippedHandlers = Return(entry);
            Metrics.AddPendingTransactionRetryHandlersSkippedOnReceived(skippedHandlers);
            if (skippedHandlers > 0)
            {
                Metrics.AddPendingTransactionRetryResourcesSkippedOnReceived(1);
                Metrics.AddPendingTransactionRetryResourcesSkippedOnReceivedAgeMilliseconds(
                    GetElapsedMilliseconds(entry.ActivatedAtTimestamp));
            }
        }

        _requestingResources.Delete(resourceId);
    }

    private void Clear()
    {
        while (_expiringQueue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _expiringQueueCounter);
        }

        _requestingResources.Clear();

        foreach (KeyValuePair<TResourceId, RetryRequestEntry> kvp in _retryRequests)
        {
            if (_retryRequests.TryRemove(kvp))
            {
                Return(kvp.Value);
            }
        }

    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _mainLoopTask;
        }
        catch (OperationCanceledException) { }

        Clear();
    }

    private void SendBatchedRetryRequests(Dictionary<IBatchMessageHandler<TMessage, TResourceId>, ArrayPoolList<TResourceId>>? batchedRetryRequests)
    {
        if (batchedRetryRequests is null)
        {
            return;
        }

        foreach (KeyValuePair<IBatchMessageHandler<TMessage, TResourceId>, ArrayPoolList<TResourceId>> kvp in batchedRetryRequests)
        {
            IBatchMessageHandler<TMessage, TResourceId> retryHandler = kvp.Key;
            ArrayPoolList<TResourceId> resourceIds = kvp.Value;

            try
            {
                Metrics.AddPendingTransactionRetryBatchHandlersCalledOnTimeout(1);
                Metrics.AddPendingTransactionRetryBatchResourcesCalledOnTimeout(resourceIds.Count);
                retryHandler.HandleMessages(resourceIds.AsSpan());
            }
            catch (Exception ex)
            {
                _logger.TraceError($"Failed to send batched retry requests to {retryHandler} for {resourceIds.Count} {typeof(TResourceId)} resources", ex);
            }
            finally
            {
                resourceIds.Dispose();
            }
        }

        batchedRetryRequests.Clear();
    }

    private void CollectRetryRequest(
        TResourceId resourceId,
        IMessageHandler<TMessage> retryHandler,
        ref Dictionary<IBatchMessageHandler<TMessage, TResourceId>, ArrayPoolList<TResourceId>>? batchedRetryRequests)
    {
        if (_logger.IsTrace) _logger.Trace($"Sending a retry request for {resourceId} after timeout");

        if (retryHandler is IBatchMessageHandler<TMessage, TResourceId> batchRetryHandler)
        {
            batchedRetryRequests ??= new(ReferenceEqualityComparer.Instance);

            if (!batchedRetryRequests.TryGetValue(batchRetryHandler, out ArrayPoolList<TResourceId>? resourceIds))
            {
                resourceIds = new ArrayPoolList<TResourceId>(1);
                batchedRetryRequests.Add(batchRetryHandler, resourceIds);
            }

            resourceIds.Add(resourceId);
            return;
        }

        try
        {
            Metrics.AddPendingTransactionRetryFallbackHandlersCalledOnTimeout(1);
            retryHandler.HandleMessage(TMessage.New(resourceId));
        }
        catch (Exception ex)
        {
            _logger.TraceError($"Failed to send retry request to {retryHandler} for {resourceId}", ex);
        }
    }

    private bool TryReserveQueueSlot()
    {
        int count = Volatile.Read(ref _expiringQueueCounter);
        while (count < _expiringQueueLimit)
        {
            int observed = Interlocked.CompareExchange(ref _expiringQueueCounter, count + 1, count);
            if (observed == count)
            {
                return true;
            }

            count = observed;
        }

        return false;
    }

    private bool TryReserveHandlerSlot(IMessageHandler<TMessage> handler)
    {
        if (_maxPendingResourcesPerHandler <= 0)
        {
            return false;
        }

        while (true)
        {
            if (_pendingResourcesByHandler.TryGetValue(handler, out int count))
            {
                if (count >= _maxPendingResourcesPerHandler)
                {
                    return false;
                }

                if (_pendingResourcesByHandler.TryUpdate(handler, count + 1, count))
                {
                    return true;
                }
            }
            else if (_pendingResourcesByHandler.TryAdd(handler, 1))
            {
                return true;
            }
        }
    }

    private void ReleaseHandlerSlot(IMessageHandler<TMessage> handler)
    {
        while (_pendingResourcesByHandler.TryGetValue(handler, out int count))
        {
            if (count == 1)
            {
                if (_pendingResourcesByHandler.TryRemove(new KeyValuePair<IMessageHandler<TMessage>, int>(handler, count)))
                {
                    return;
                }
            }
            else if (_pendingResourcesByHandler.TryUpdate(handler, count - 1, count))
            {
                return;
            }
        }
    }

    private bool TryRemove(TResourceId resourceId, RetryRequestEntry entry)
    {
        if (_retryRequests.TryRemove(new KeyValuePair<TResourceId, RetryRequestEntry>(resourceId, entry)))
        {
            Return(entry);
            return true;
        }

        return false;
    }

    private int Return(RetryRequestEntry entry)
    {
        ReleaseHandlerSlot(entry.SourceHandler);
        HandlerSlotReleaser releaser = new(this);
        if (entry.Handlers.Deactivate(entry.Generation, ref releaser))
        {
            _handlerBagsPool.Return(entry.Handlers);
        }

        return releaser.Count;
    }

    private struct HandlerSlotReleaser(RetryCache<TMessage, TResourceId> cache) : IHandlerBagProcessor<TMessage>
    {
        public int Count { get; private set; }

        public void Process(IMessageHandler<TMessage> handler)
        {
            cache.ReleaseHandlerSlot(handler);
            Count++;
        }
    }

    private long GetElapsedMilliseconds(long startTimestamp) =>
        (long)_timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;

    internal readonly struct BatchedHandlerPreference(
        Dictionary<IBatchMessageHandler<TMessage, TResourceId>, ArrayPoolList<TResourceId>>? batchedRetryRequests,
        int maxPreferredResourcesPerHandler)
        : IHandlerBagPreference<TMessage>
    {
        public HandlerPreference GetPreference(IMessageHandler<TMessage> handler)
        {
            if (batchedRetryRequests is null
                || handler is not IBatchMessageHandler<TMessage, TResourceId> batchHandler
                || !batchedRetryRequests.TryGetValue(batchHandler, out ArrayPoolList<TResourceId>? resources))
            {
                return HandlerPreference.Neutral;
            }

            return resources.Count < maxPreferredResourcesPerHandler
                ? HandlerPreference.Preferred
                : HandlerPreference.Neutral;
        }
    }

    private static void DisposeBatchedRetryRequests(Dictionary<IBatchMessageHandler<TMessage, TResourceId>, ArrayPoolList<TResourceId>>? batchedRetryRequests)
    {
        if (batchedRetryRequests is null)
        {
            return;
        }

        foreach (KeyValuePair<IBatchMessageHandler<TMessage, TResourceId>, ArrayPoolList<TResourceId>> kvp in batchedRetryRequests)
        {
            kvp.Value.Dispose();
        }
    }

}

public enum AnnounceResult
{
    RequestRequired,
    Delayed
}

/// <summary>
/// Poolable handler collection with generation-guarded lifecycle.
/// <para>
/// Bags start inactive in the pool. <see cref="Activate"/> begins a new generation,
/// and every operation must present that generation. This prevents a stale reference
/// from a previous dictionary entry from mutating a pooled bag after reuse.
/// </para>
/// <para>
/// Each distinct handler can be selected once, and the total number accepted during a
/// lifecycle is bounded. Late announcements can therefore improve recovery without
/// extending a retry cycle indefinitely.
/// </para>
/// </summary>
internal sealed class HandlerBag<TMessage>
{
    private readonly List<IMessageHandler<TMessage>> _handlers = [];
    private readonly Lock _lock = new();
    private bool _active;
    private int _pendingCount;
    private long _generation;

    /// <summary>
    /// Activates the bag for a new lifecycle and returns its generation.
    /// </summary>
    public long Activate()
    {
        lock (_lock)
        {
            _active = true;
            _pendingCount = 0;
            return ++_generation;
        }
    }

    /// <summary>
    /// Deactivates the matching lifecycle before returning the bag to the pool.
    /// </summary>
    public bool Deactivate<TProcessor>(long generation, ref TProcessor processor)
        where TProcessor : struct, IHandlerBagProcessor<TMessage>
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                return false;
            }

            _active = false;
            for (int i = 0; i < _pendingCount; i++)
            {
                processor.Process(_handlers[i]);
            }

            _handlers.Clear();
            _pendingCount = 0;
            return true;
        }
    }

    /// <summary>
    /// Adds a distinct handler while the matching lifecycle is accepting candidates.
    /// </summary>
    public HandlerBagAddResult Add(IMessageHandler<TMessage> handler, int maxCount, long generation)
    {
        lock (_lock)
        {
            if (!_active || generation != _generation)
                return HandlerBagAddResult.Inactive;

            if (_handlers.Count >= maxCount)
                return HandlerBagAddResult.Full;

            for (int i = 0; i < _handlers.Count; i++)
            {
                if (ReferenceEquals(_handlers[i], handler))
                {
                    return HandlerBagAddResult.Duplicate;
                }
            }

            _handlers.Add(handler);
            if (_pendingCount < _handlers.Count - 1)
            {
                (_handlers[_pendingCount], _handlers[^1]) = (_handlers[^1], _handlers[_pendingCount]);
            }

            _pendingCount++;
            return HandlerBagAddResult.Added;
        }
    }

    /// <summary>
    /// Selects one pending retry handler from the matching lifecycle.
    /// </summary>
    public bool TryTake(long generation, out IMessageHandler<TMessage>? handler, out bool hasMoreHandlers)
    {
        NoHandlerPreference<TMessage> preference = default;
        return TryTake(generation, ref preference, out handler, out hasMoreHandlers);
    }

    /// <summary>
    /// Selects one pending retry handler, preferring handlers selected for related work.
    /// </summary>
    public bool TryTake<TPreference>(
        long generation,
        ref TPreference preference,
        out IMessageHandler<TMessage>? handler,
        out bool hasMoreHandlers)
        where TPreference : struct, IHandlerBagPreference<TMessage>
    {
        lock (_lock)
        {
            handler = null;
            hasMoreHandlers = false;

            if (!_active || generation != _generation)
            {
                return false;
            }

            if (_pendingCount == 0)
            {
                _active = false;
                return false;
            }

            int preferredIndex = -1;
            int preferredCount = 0;
            int neutralIndex = -1;
            int neutralCount = 0;
            for (int i = 0; i < _pendingCount; i++)
            {
                switch (preference.GetPreference(_handlers[i]))
                {
                    case HandlerPreference.Preferred:
                        if (++preferredCount == 1 || Random.Shared.Next(preferredCount) == 0)
                        {
                            preferredIndex = i;
                        }
                        break;
                    case HandlerPreference.Neutral:
                        if (++neutralCount == 1 || Random.Shared.Next(neutralCount) == 0)
                        {
                            neutralIndex = i;
                        }
                        break;
                }
            }

            int index = preferredIndex >= 0
                ? preferredIndex
                : neutralIndex >= 0
                    ? neutralIndex
                    : _pendingCount == 1 ? 0 : Random.Shared.Next(_pendingCount);

            handler = _handlers[index];
            _pendingCount--;
            (_handlers[index], _handlers[_pendingCount]) = (_handlers[_pendingCount], _handlers[index]);
            hasMoreHandlers = _pendingCount > 0;
            return true;
        }
    }

    /// <summary>
    /// Called by the pool on Return. Clears any late additions.
    /// Does NOT reactivate — only Activate() does.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _active = false;
            _pendingCount = 0;
            _handlers.Clear();
        }
    }
}

internal enum HandlerBagAddResult
{
    Added,
    Duplicate,
    Full,
    Inactive
}

internal interface IHandlerBagProcessor<TMessage>
{
    void Process(IMessageHandler<TMessage> handler);
}

internal interface IHandlerBagPreference<TMessage>
{
    HandlerPreference GetPreference(IMessageHandler<TMessage> handler);
}

internal readonly struct NoHandlerPreference<TMessage> : IHandlerBagPreference<TMessage>
{
    public HandlerPreference GetPreference(IMessageHandler<TMessage> handler) => HandlerPreference.Neutral;
}

internal enum HandlerPreference
{
    Neutral,
    Preferred
}

internal sealed class HandlerBagPolicy<TMessage> : IPooledObjectPolicy<HandlerBag<TMessage>>
{
    public HandlerBag<TMessage> Create() => new();

    public bool Return(HandlerBag<TMessage> obj)
    {
        obj.Reset();
        return true;
    }
}
