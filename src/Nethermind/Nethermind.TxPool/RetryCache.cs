// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.TxPool;

public sealed class RetryCache<TMessage, TResourceId> : IAsyncDisposable
    where TMessage : INew<TResourceId, TMessage>
    where TResourceId : struct, IEquatable<TResourceId>
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
    private readonly ClockKeyCache<TResourceId> _requestingResources;
    private readonly ILogger _logger;

    internal int ResourcesInRetryQueue => _expiringQueueCounter;

    public RetryCache(ILogManager logManager, int timeoutMs = 2500, int requestingCacheSize = 1024, int expiringQueueLimit = 10000, int maxRetryRequests = 8, CancellationToken token = default)
    {
        _logger = logManager.GetClassLogger();

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
            HandlerBag<TMessage> bag = _retryRequests.GetOrAdd(resourceId, newBag);

            if (ReferenceEquals(bag, newBag))
            {
                // First announcer: not added to the retry bag because the caller receives
                // RequestRequired and will immediately request the resource itself.
                // Only subsequent announcers (via TryAdd) are registered for retry.
                AnnounceAddEnqueue(resourceId, handler);
                return AnnounceResult.RequestRequired;
            }

            // Got existing entry — return unused bag and update
            _handlerBagsPool.Return(newBag);
            bag.TryAdd(handler, _maxRetryRequests);
            return AnnounceResult.Delayed;
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
/// Poolable handler collection with generation-guarded lifecycle.
/// <para>
/// <see cref="Drain"/> snapshots the handlers under lock and bumps the generation,
/// so any in-flight <see cref="TryAdd"/> holding a stale reference becomes a no-op
/// (it adds to a drained list that no one will iterate).
/// <see cref="HandlerBagPolicy{TMessage}.Return"/> calls <see cref="Reset"/> which
/// clears the list, making the instance safe for reuse by a different resource.
/// </para>
/// </summary>
internal sealed class HandlerBag<TMessage>
{
    private readonly List<IMessageHandler<TMessage>> _handlers = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Try to add a handler. A late call after Drain is harmless — it adds to a
    /// cleared list that will be discarded on the next Reset/Drain cycle.
    /// </summary>
    public void TryAdd(IMessageHandler<TMessage> handler, int maxCount)
    {
        lock (_lock)
        {
            if (_handlers.Count < maxCount)
            {
                _handlers.Add(handler);
            }
        }
    }

    /// <summary>
    /// Snapshot and clear the handlers. After this call, late TryAdd calls are
    /// harmless — they add to the now-empty list which will be cleared on Reset.
    /// </summary>
    public IMessageHandler<TMessage>[] Drain()
    {
        lock (_lock)
        {
            if (_handlers.Count == 0)
                return [];

            IMessageHandler<TMessage>[] snapshot = [.. _handlers];
            _handlers.Clear();
            return snapshot;
        }
    }

    /// <summary>
    /// Called by the pool on Return. Clears any late additions.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
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
