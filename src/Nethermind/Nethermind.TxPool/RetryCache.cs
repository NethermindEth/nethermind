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

public class RetryCache<TMessage, TResourceId>
    where TMessage : INew<TResourceId, TMessage>
    where TResourceId : struct, IEquatable<TResourceId>
{
    private readonly int _timeoutMs;
    private readonly int _checkMs;
    private readonly int _requestingCacheSize;

    private readonly ConcurrentDictionary<TResourceId, PooledSet<IMessageHandler<TMessage>>> _retryRequests = new();
    private readonly ConcurrentQueue<(TResourceId ResourceId, DateTimeOffset ExpiresAfter)> _expiringQueue = new();
    private readonly ClockKeyCache<TResourceId> _requestingResources;
    private readonly ILogger _logger;

    public RetryCache(ILogManager logManager, int timeoutMs = 2500, int requestingCacheSize = 1024, CancellationToken token = default)
    {
        _logger = logManager.GetClassLogger();

        _timeoutMs = timeoutMs;
        _checkMs = _timeoutMs / 5;
        _requestingCacheSize = requestingCacheSize;
        _requestingResources = new(_requestingCacheSize);

        Task.Run(async () =>
        {
            PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_checkMs));

            while (await timer.WaitForNextTickAsync(token))
            {
                while (!token.IsCancellationRequested && _expiringQueue.TryPeek(out (TResourceId ResourceId, DateTimeOffset ExpiresAfter) item) && item.ExpiresAfter <= DateTimeOffset.UtcNow)
                {
                    _expiringQueue.TryDequeue(out item);

                    if (_retryRequests.TryRemove(item.ResourceId, out PooledSet<IMessageHandler<TMessage>>? requests))
                    {
                        if (requests.Count > 0)
                        {
                            _requestingResources.Set(item.ResourceId);
                        }

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
                }
            }
        }, token);
    }

    public AnnounceResult Announced(TResourceId resourceId, IMessageHandler<TMessage> retryHandler)
    {
        if (!_requestingResources.Contains(resourceId))
        {
            bool added = false;

            _retryRequests.AddOrUpdate(resourceId, (resourceId) =>
            {
                if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {retryHandler}: NEW");

                _expiringQueue.Enqueue((resourceId, DateTimeOffset.UtcNow.AddMilliseconds(_timeoutMs)));
                added = true;

                return [];
            }, (resourceId, dict) =>
            {
                if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {retryHandler}: UPDATE");

                dict.Add(retryHandler);
                return dict;
            });

            return added ? AnnounceResult.New : AnnounceResult.Enqueued;
        }

        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {retryHandler}, but a retry is in progress already, immidietly firing");

        return AnnounceResult.New;
    }

    public void Received(TResourceId resourceId)
    {
        if (_logger.IsTrace) _logger.Trace($"Received {resourceId}");

        _retryRequests.TryRemove(resourceId, out PooledSet<IMessageHandler<TMessage>>? _);
    }
}

public enum AnnounceResult
{
    New,
    Enqueued,
    PendingRequest
}

