// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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

    public RetryCache(ILogManager logManager, int timeoutMs = 2500, int requestingCacheSize = 1024, int expiringQueueLimit = 10000, int maxRetryRequests = 8, CancellationToken token = default)
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
                                    bool set = false;

                                    foreach (IMessageHandler<TMessage> retryHandler in bag.Drain())
                                    {
                                        if (!set)
                                        {
                                            _requestingResources.Set(item.ResourceId);
                                            set = true;

                                            if (_logger.IsTrace) _logger.Trace($"Sending retry requests for {item.ResourceId} after timeout");
                                        }

                                        try
                                        {
                                            retryHandler.HandleMessage(TMessage.New(item.ResourceId));
                                        }
                                        catch (Exception ex)
                                        {
                                            if (_logger.IsTrace) _logger.Error($"Failed to send retry request to {retryHandler} for {item.ResourceId}", ex);
                                        }
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
            if (_logger.IsDebug) _logger.Warn($"{typeof(TResourceId)} retry queue is full");

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

                bag.TryAdd(handler, _maxRetryRequests);
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
            bag.Deactivate();
            _handlerBagsPool.Return(bag);
        }

        _requestingResources.Delete(resourceId);
    }

    private void Clear()
    {
        _expiringQueueCounter = 0;
        _expiringQueue.Clear();
        _requestingResources.Clear();

        foreach (HandlerBag<TMessage> bag in _retryRequests.Values)
        {
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
    private bool _active;

    /// <summary>
    /// Called by the owner that wins GetOrAdd. Activates the bag for use.
    /// </summary>
    public void Activate()
    {
        lock (_lock)
        {
            _active = true;
        }
    }

    /// <summary>
    /// Deactivate without draining. Used by Received() before returning to pool.
    /// </summary>
    public void Deactivate()
    {
        lock (_lock)
        {
            _active = false;
        }
    }

    /// <summary>
    /// Try to add a handler. Rejected if the bag is inactive (drained/returned),
    /// the handler is already present (set semantics), or the bag is full.
    /// </summary>
    public bool TryAdd(IMessageHandler<TMessage> handler, int maxCount)
    {
        lock (_lock)
        {
            if (!_active)
                return false;

            if (_handlers.Count >= maxCount)
                return false;

            return _handlers.Add(handler);
        }
    }

    /// <summary>
    /// Snapshot the handlers, clear the set, and deactivate. After this call,
    /// any in-flight TryAdd will be rejected.
    /// </summary>
    public IMessageHandler<TMessage>[] Drain()
    {
        lock (_lock)
        {
            _active = false;

            if (_handlers.Count == 0)
                return [];

            IMessageHandler<TMessage>[] snapshot = [.. _handlers];
            _handlers.Clear();
            return snapshot;
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
            _handlers.Clear();
        }
    }
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
