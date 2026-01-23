// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using ConcurrentCollections;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Logging;
using System;
using System.Collections.Concurrent;
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
    private static readonly ObjectPool<ConcurrentHashSet<IMessageHandler<TMessage>>> _handlerBagsPool = new DefaultObjectPool<ConcurrentHashSet<IMessageHandler<TMessage>>>(new ConcurrentHashSetPolicy<IMessageHandler<TMessage>>());
    private readonly ConcurrentDictionary<TResourceId, ConcurrentHashSet<IMessageHandler<TMessage>>> _retryRequests = new();
    private readonly ConcurrentQueue<(TResourceId ResourceId, DateTimeOffset ExpiresAfter)> _expiringQueue = new();
    private int _expiringQueueCounter = 0;
    private readonly ClockKeyCache<TResourceId> _requestingResources;
    private readonly ILogger _logger;
    private readonly Func<TResourceId, ConcurrentHashSet<IMessageHandler<TMessage>>, IMessageHandler<TMessage>, ConcurrentHashSet<IMessageHandler<TMessage>>> _announceUpdate;

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
        // Closure capture
        _announceUpdate = AnnounceUpdate;

        _mainLoopTask = Task.Run(async () =>
        {
            PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_checkMs));

            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    while (!token.IsCancellationRequested && _expiringQueue.TryPeek(out (TResourceId ResourceId, DateTimeOffset ExpiresAfter) item) && item.ExpiresAfter <= DateTimeOffset.UtcNow)
                    {
                        if (_expiringQueue.TryDequeue(out item))
                        {
                            Interlocked.Decrement(ref _expiringQueueCounter);

                            if (_retryRequests.TryRemove(item.ResourceId, out ConcurrentHashSet<IMessageHandler<TMessage>>? requests))
                            {
                                try
                                {
                                    bool set = false;

                                    foreach (IMessageHandler<TMessage> retryHandler in requests)
                                    {
                                        if (!set)
                                        {
                                            _requestingResources.Set(item.ResourceId);
                                            set = true;

                                            if (_logger.IsWarn) _logger.Trace($"Sending retry requests for {item.ResourceId} after timeout");
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
                                    _handlerBagsPool.Return(requests);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Unexpected error in {typeof(TResourceId).Name} retry cache loop", ex);
                    Clear();
                }
            }

            if (_logger.IsWarn) _logger.Warn($"{typeof(TResourceId).Name} retry cache stopped");
        }, token);
    }

    public AnnounceResult Announced(in TResourceId resourceId, IMessageHandler<TMessage> handler)
    {
        if (_token.IsCancellationRequested)
        {
            return AnnounceResult.RequestRequired;
        }

        if (_logger.IsError) _logger.Info($"{typeof(TResourceId).Name}({resourceId}) retry queue is {_expiringQueueCounter} long");


        if (_expiringQueueCounter > _expiringQueueLimit)
        {
            if (_logger.IsError) _logger.Error($"{typeof(TResourceId).Name} retry queue is full");

            return AnnounceResult.RequestRequired;
        }

        if (!_requestingResources.Contains(resourceId))
        {
            AnnounceResult result = AnnounceResult.Delayed;
            _retryRequests.AddOrUpdate(resourceId, (resourceId, retryHandler) =>
            {
                return AnnounceAdd(resourceId, retryHandler, out result);
            }, _announceUpdate, handler);

            return result;
        }

        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {handler}, but a retry is in progress already, immediately firing");

        return AnnounceResult.RequestRequired;
    }

    private ConcurrentHashSet<IMessageHandler<TMessage>> AnnounceAdd(TResourceId resourceId, IMessageHandler<TMessage> retryHandler, out AnnounceResult result)
    {
        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {retryHandler}: NEW");

        _expiringQueue.Enqueue((resourceId, DateTimeOffset.UtcNow.AddMilliseconds(_timeoutMs)));
        Interlocked.Increment(ref _expiringQueueCounter);

        result = AnnounceResult.RequestRequired;

        if (_logger.IsError) _logger.Info($"Request {resourceId}");

        return _handlerBagsPool.Get();
    }

    private ConcurrentHashSet<IMessageHandler<TMessage>> AnnounceUpdate(TResourceId resourceId, ConcurrentHashSet<IMessageHandler<TMessage>> requests, IMessageHandler<TMessage> retryHandler)
    {
        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {retryHandler}: UPDATE");

        if (_logger.IsError) _logger.Info($"Delay {resourceId}");

        if (requests.Count < _maxRetryRequests)
        {
            requests.Add(retryHandler);
        }

        return requests;
    }

    public void Received(in TResourceId resourceId)
    {
        if (_logger.IsTrace) _logger.Trace($"Received {resourceId}");

        if (_retryRequests.TryRemove(resourceId, out ConcurrentHashSet<IMessageHandler<TMessage>>? item))
        {
            _handlerBagsPool.Return(item);
        }

        _requestingResources.Delete(resourceId);
    }

    private void Clear()
    {
        _expiringQueueCounter = 0;
        _expiringQueue.Clear();
        _requestingResources.Clear();

        foreach (ConcurrentHashSet<IMessageHandler<TMessage>> requests in _retryRequests.Values)
        {
            _handlerBagsPool.Return(requests);
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

internal class ConcurrentHashSetPolicy<TItem> : IPooledObjectPolicy<ConcurrentHashSet<TItem>>
{
    public ConcurrentHashSet<TItem> Create() => [];

    public bool Return(ConcurrentHashSet<TItem> obj)
    {
        obj.Clear();
        return true;
    }
}
