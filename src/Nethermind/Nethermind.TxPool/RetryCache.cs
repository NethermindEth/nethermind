// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.TxPool;

public class RetryCache<TMessage, TResourceId> : IAsyncDisposable
    where TMessage : INew<TResourceId, TMessage>
    where TResourceId : struct, IEquatable<TResourceId>
{
    private readonly int _timeoutMs;
    private readonly int _maxQueueSize;

    private readonly ConcurrentDictionary<TResourceId, PooledSet<IMessageHandler<TMessage>>> _retryRequests = new();
    private readonly ConcurrentQueue<ResourceExpiry> _expiringQueue = new();
    private readonly ClockKeyCache<TResourceId> _requestingResources;
    private readonly ILogger _logger;
    private readonly Task _queueTask;

    private readonly record struct ResourceExpiry(TResourceId ResourceId, DateTime ExpiresAfter)
    {
        public bool IsExpired(DateTime now) => now > ExpiresAfter;
    }

    public RetryCache(ILogManager logManager, int timeoutMs = 2500, int requestingCacheSize = 4096, int maxQueueSize = 1024, CancellationToken token = default)
    {
        _logger = logManager.GetClassLogger();

        _timeoutMs = timeoutMs;
        _maxQueueSize = maxQueueSize;
        int checkMs = _timeoutMs / 5;
        _requestingResources = new(requestingCacheSize);

        _queueTask = Task.Run(async () =>
        {
            PeriodicTimer timer = new(TimeSpan.FromMilliseconds(checkMs));
            while (await timer.WaitForNextTickAsync(token))
            {
                DateTime now = DateTime.UtcNow;
                try
                {
                    while (!token.IsCancellationRequested
                           && _expiringQueue.TryPeek(out ResourceExpiry item)
                           && item.IsExpired(now))
                    {
                        _expiringQueue.TryDequeue(out _);

                        if (_retryRequests.TryRemove(item.ResourceId, out PooledSet<IMessageHandler<TMessage>>? r))
                        {
                            using PooledSet<IMessageHandler<TMessage>> requests = r;
                            if (requests.Count > 0)
                            {
                                _requestingResources.Set(item.ResourceId);
                                if (_logger.IsTrace) _logger.Trace($"Sending retry requests for {item.ResourceId} after timeout");

                                foreach (IMessageHandler<TMessage> retryHandler in requests)
                                {
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
                            else if (_logger.IsTrace) _logger.Trace($"No retry requests for {item.ResourceId} after timeout");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _expiringQueue.Clear();
                    _retryRequests.Clear();
                    _requestingResources.Clear();
                    if (_logger.IsError) _logger.Error("Retry queue exception", ex);
                }
            }
        }, token);
    }

    public ResourceFetchStatus NotifyAboutResource(in TResourceId resourceId, IMessageHandler<TMessage> retryHandler)
    {
        if (_requestingResources.Contains(resourceId))
        {
            if (_logger.IsTrace) _logger.Trace($"Notified about {resourceId} by {retryHandler}, but a retry is in progress already, immediately firing");
            return ResourceFetchStatus.PendingRequest;
        }

        ResourceFetchStatus result = ResourceFetchStatus.New;

        if (_expiringQueue.Count <= _maxQueueSize)
        {
            _retryRequests.AddOrUpdate(resourceId, Add, Update);
        }
        else if (_retryRequests.TryGetValue(resourceId, out PooledSet<IMessageHandler<TMessage>>? requests))
        {
            Update(resourceId, requests);
        }

        return result;

        PooledSet<IMessageHandler<TMessage>> Add(TResourceId id)
        {
            if (_logger.IsTrace) _logger.Trace($"Notified about {id} by {retryHandler}: NEW");
            _expiringQueue.Enqueue(new ResourceExpiry(id, DateTime.UtcNow.AddMilliseconds(_timeoutMs)));
            return [];
        }

        PooledSet<IMessageHandler<TMessage>> Update(TResourceId id, PooledSet<IMessageHandler<TMessage>> existing)
        {
            if (_logger.IsTrace) _logger.Trace($"Notified about {id} by {retryHandler}: UPDATE");
            result = ResourceFetchStatus.Enqueued;
            existing.Add(retryHandler);
            return existing;
        }
    }

    public void Received(in TResourceId resourceId)
    {
        _retryRequests.TryRemove(resourceId, out PooledSet<IMessageHandler<TMessage>>? _);
        _requestingResources.Delete(resourceId);

        if (_logger.IsTrace) _logger.Trace($"Received {resourceId}");
    }

    public async ValueTask DisposeAsync()
    {
        await _queueTask;
    }
}

public enum ResourceFetchStatus
{
    New,
    Enqueued,
    PendingRequest,
    Known,
    Ignored
}

