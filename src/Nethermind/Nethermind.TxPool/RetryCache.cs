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
using System.Diagnostics;
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

    private readonly int _timeoutMs;
    private readonly CancellationToken _token;
    private readonly int _checkMs;
    private readonly int _expiringQueueLimit;
    private readonly int _maxRetryRequests;
    private readonly Task _mainLoopTask;
    private static readonly ObjectPool<HandlerBag<TMessage>> _handlerBagsPool = new DefaultObjectPool<HandlerBag<TMessage>>(new HandlerBagPolicy<TMessage>(), maximumRetained: 512);
    private readonly ConcurrentDictionary<TResourceId, HandlerBag<TMessage>> _retryRequests = new();
    private readonly ConcurrentQueue<(TResourceId ResourceId, DateTimeOffset ExpiresAfter)> _expiringQueue = new();
    private int _expiringQueueCounter = 0;
    private readonly AssociativeKeyCache<TResourceId> _requestingResources;
    private readonly ILogger _logger;

    internal int ResourcesInRetryQueue => _expiringQueueCounter;

    public RetryCache(ILogManager logManager, int timeoutMs = DefaultTimeoutMs, int requestingCacheSize = DefaultRequestingCacheSize, int expiringQueueLimit = DefaultExpiringQueueLimit, int maxRetryRequests = DefaultMaxRetryRequests, CancellationToken token = default)
    {
        _logger = logManager.GetClassLogger(typeof(RetryCache<,>));

        _timeoutMs = timeoutMs;
        _token = token;
        _checkMs = _timeoutMs / 5;
        _requestingResources = new(requestingCacheSize);
        _expiringQueueLimit = expiringQueueLimit;
        _maxRetryRequests = maxRetryRequests;
        _mainLoopTask = Task.Run(async () =>
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_checkMs));

            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    while (!token.IsCancellationRequested && _expiringQueue.TryPeek(out (TResourceId ResourceId, DateTimeOffset ExpiresAfter) item) && item.ExpiresAfter <= DateTimeOffset.UtcNow)
                    {
                        if (_expiringQueue.TryDequeue(out item))
                        {
                            Interlocked.Decrement(ref _expiringQueueCounter);

                            if (_retryRequests.TryRemove(item.ResourceId, out HandlerBag<TMessage>? bag))
                            {
                                try
                                {
                                    RetryRequestSender sender = new(this, item.ResourceId);
                                    long activatedAtTimestamp = bag.Drain(ref sender);
                                    Metrics.AddPendingTransactionRetryHandlersCalledOnTimeout(sender.HandlersCalled);

                                    if (sender.HandlersCalled > 0)
                                    {
                                        Metrics.AddPendingTransactionRetryResourcesTimedOutWithHandlers(1);
                                        Metrics.AddPendingTransactionRetryResourcesTimedOutWithHandlersAgeMilliseconds(GetElapsedMilliseconds(activatedAtTimestamp));
                                    }
                                }
                                finally
                                {
                                    _handlerBagsPool.Return(bag);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Unexpected error in {typeof(TResourceId)} retry cache loop", ex);
                    Clear();
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

        if (_expiringQueueCounter > _expiringQueueLimit)
        {
            _logger.TraceWarn($"{typeof(TResourceId)} retry queue is full");
            Metrics.AddPendingTransactionRetryQueueFull(1);

            return AnnounceResult.RequestRequired;
        }

        if (!_requestingResources.Contains(resourceId))
        {
            HandlerBag<TMessage> newBag = _handlerBagsPool.Get();
            bool published = false;
            try
            {
                newBag.Activate();
                HandlerBag<TMessage> bag = _retryRequests.GetOrAdd(resourceId, newBag);
                published = ReferenceEquals(bag, newBag);

                if (published)
                {
                    // First announcer: not added to the retry bag because the caller receives
                    // RequestRequired and will immediately request the resource itself.
                    // Only subsequent announcers (via TryAdd) are registered for retry.
                    AnnounceAddEnqueue(resourceId, handler);
                    return AnnounceResult.RequestRequired;
                }

                if (!bag.TryAdd(handler, _maxRetryRequests, out bool limitReached) && limitReached)
                {
                    Metrics.AddPendingTransactionRetryHandlersRejectedByLimit(1);
                }

                return AnnounceResult.Delayed;
            }
            finally
            {
                if (!published)
                {
                    _handlerBagsPool.Return(newBag);
                }
            }
        }

        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {handler}, but a retry is in progress already, immediately firing");

        return AnnounceResult.RequestRequired;
    }

    private void AnnounceAddEnqueue(TResourceId resourceId, IMessageHandler<TMessage> retryHandler)
    {
        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {retryHandler}: NEW");

        _expiringQueue.Enqueue((resourceId, DateTimeOffset.UtcNow.AddMilliseconds(_timeoutMs)));
        Interlocked.Increment(ref _expiringQueueCounter);
    }

    public void Received(in TResourceId resourceId)
    {
        if (_logger.IsTrace) _logger.Trace($"Received {resourceId}");

        if (_retryRequests.TryRemove(resourceId, out HandlerBag<TMessage>? bag))
        {
            int skippedHandlers = bag.Deactivate(out long activatedAtTimestamp);
            Metrics.AddPendingTransactionRetryHandlersSkippedOnReceived(skippedHandlers);
            if (skippedHandlers > 0)
            {
                Metrics.AddPendingTransactionRetryResourcesSkippedOnReceived(1);
                Metrics.AddPendingTransactionRetryResourcesSkippedOnReceivedAgeMilliseconds(GetElapsedMilliseconds(activatedAtTimestamp));
            }

            _handlerBagsPool.Return(bag);
        }

        _requestingResources.Delete(resourceId);
    }

    private void Clear()
    {
        _expiringQueueCounter = 0;
        _expiringQueue.Clear();
        _requestingResources.Clear();

        foreach (KeyValuePair<TResourceId, HandlerBag<TMessage>> kvp in _retryRequests)
        {
            HandlerBag<TMessage> bag = kvp.Value;
            bag.Deactivate();
            _handlerBagsPool.Return(bag);
        }

        _retryRequests.Clear();
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

    private static long GetElapsedMilliseconds(long activatedAtTimestamp)
    {
        if (activatedAtTimestamp == 0)
        {
            return 0;
        }

        return (long)Stopwatch.GetElapsedTime(activatedAtTimestamp).TotalMilliseconds;
    }

    private struct RetryRequestSender(RetryCache<TMessage, TResourceId> cache, TResourceId resourceId) : IHandlerBagDrainProcessor<TMessage>
    {
        private bool _requestingResourceSet;
        public int HandlersCalled { get; private set; }

        public void Process(IMessageHandler<TMessage> retryHandler)
        {
            if (!_requestingResourceSet)
            {
                cache._requestingResources.Set(resourceId);
                _requestingResourceSet = true;

                if (cache._logger.IsTrace) cache._logger.Trace($"Sending retry requests for {resourceId} after timeout");
            }

            try
            {
                HandlersCalled++;
                retryHandler.HandleMessage(TMessage.New(resourceId));
            }
            catch (Exception ex)
            {
                cache._logger.TraceError($"Failed to send retry request to {retryHandler} for {resourceId}", ex);
            }
        }
    }
}

public enum AnnounceResult
{
    RequestRequired,
    Delayed
}

/// <summary>
/// Poolable handler collection with active/inactive lifecycle guard.
/// <para>
/// Bags start inactive in the pool. The owner that wins <c>GetOrAdd</c> calls
/// <see cref="Activate"/> once. <see cref="Drain"/> and <see cref="Deactivate"/>
/// permanently deactivate the bag for its current lifecycle — <see cref="TryAdd"/>
/// is rejected once inactive. <see cref="Reset"/> clears the bag but does NOT
/// reactivate — only an explicit <see cref="Activate"/> after pool <c>Get()</c> does.
/// This ensures stale references from a previous lifecycle can never mutate a
/// reused bag.
/// </para>
/// <para>
/// Uses <see cref="HashSet{T}"/> internally to preserve set semantics (no duplicate handlers).
/// All operations use a single lock acquisition.
/// </para>
/// </summary>
internal sealed class HandlerBag<TMessage>
{
    private readonly HashSet<IMessageHandler<TMessage>> _handlers = [];
    private readonly Lock _lock = new();
    private long _activatedAtTimestamp;
    private bool _active;

    /// <summary>
    /// Called by the owner that wins GetOrAdd. Activates the bag for use.
    /// </summary>
    public void Activate()
    {
        lock (_lock)
        {
            _activatedAtTimestamp = Stopwatch.GetTimestamp();
            _active = true;
        }
    }

    /// <summary>
    /// Deactivate without draining. Used by Received() before returning to pool.
    /// </summary>
    /// <returns>Number of handlers left in the bag without draining.</returns>
    public int Deactivate() => Deactivate(out _);

    public int Deactivate(out long activatedAtTimestamp)
    {
        lock (_lock)
        {
            _active = false;
            activatedAtTimestamp = _activatedAtTimestamp;
            return _handlers.Count;
        }
    }

    /// <summary>
    /// Try to add a handler. Rejected if the bag is inactive (drained/returned),
    /// the handler is already present (set semantics), or the bag is full.
    /// </summary>
    public bool TryAdd(IMessageHandler<TMessage> handler, int maxCount)
        => TryAdd(handler, maxCount, out _);

    public bool TryAdd(IMessageHandler<TMessage> handler, int maxCount, out bool limitReached)
    {
        lock (_lock)
        {
            limitReached = false;

            if (!_active)
                return false;

            if (_handlers.Count >= maxCount)
            {
                limitReached = true;
                return false;
            }

            return _handlers.Add(handler);
        }
    }

    /// <summary>
    /// Process the handlers, clear the set, and deactivate. After this call,
    /// any in-flight TryAdd will be rejected.
    /// </summary>
    public long Drain<TProcessor>(ref TProcessor processor)
        where TProcessor : struct, IHandlerBagDrainProcessor<TMessage>
    {
        long activatedAtTimestamp;
        lock (_lock)
        {
            _active = false;
            activatedAtTimestamp = _activatedAtTimestamp;
        }

        try
        {
            foreach (IMessageHandler<TMessage> handler in _handlers)
            {
                processor.Process(handler);
            }
        }
        finally
        {
            _handlers.Clear();
        }

        return activatedAtTimestamp;
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
            _activatedAtTimestamp = 0;
            _handlers.Clear();
        }
    }
}

internal interface IHandlerBagDrainProcessor<TMessage>
{
    void Process(IMessageHandler<TMessage> handler);
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
