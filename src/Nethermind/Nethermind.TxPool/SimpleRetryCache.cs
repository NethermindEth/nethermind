// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.TxPool;

public interface IMessageHandler<TMessage>
{
    void HandleMessage(TMessage message);
}

public class SimpleRetryCache<TResourceId>
    where TResourceId : struct, IEquatable<TResourceId>
{
    private readonly int _timeoutMs;
    private readonly int _checkMs;
    private readonly int _requestingCacheSize;

    private readonly ConcurrentDictionary<TResourceId, HashSet<IMessageHandler<TResourceId>>> _retryRequests = new();
    private readonly ConcurrentQueue<(TResourceId ResourceId, DateTimeOffset Expires)> _expiringQueue = new();
    private readonly ClockKeyCache<TResourceId> _requestingResources;
    private readonly ILogger _logger;

    public SimpleRetryCache(ILogManager logManager, int timeoutMs = 2500, int requestingCacheSize = 1024, CancellationToken token = default)
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

                    if (_retryRequests.TryRemove(item.ResourceId, out HashSet<IMessageHandler<TResourceId>>? requests))
                    {
                        if (requests.Count > 0)
                        {
                            _requestingResources.Set(item.ResourceId);
                        }

                        if (_logger.IsTrace) _logger.Trace($"Sending retry requests for {item.ResourceId} after timeout");


                        foreach (IMessageHandler<TResourceId> handler in requests)
                        {
                            try
                            {
                                handler.HandleMessage(item.ResourceId);
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsTrace) _logger.Error($"Failed to send retry request to {handler} for {item.ResourceId}", ex);
                            }
                        }
                    }
                }
            }
        }, token);
    }

    public AnnounceResult Announced(TResourceId resourceId, IMessageHandler<TResourceId> handler)
    {
        if (!_requestingResources.Contains(resourceId))
        {
            bool added = false;

            _retryRequests.AddOrUpdate(resourceId, (resourceId) =>
            {
                if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {handler}: NEW");

                _expiringQueue.Enqueue((resourceId, DateTimeOffset.UtcNow.AddMilliseconds(_timeoutMs)));
                added = true;

                return [];
            }, (resourceId, dict) =>
            {
                if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {handler}: UPDATE");

                dict.Add(handler);
                return dict;
            });

            return added ? AnnounceResult.New : AnnounceResult.Enqueued;
        }

        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {handler}, but a retry is in progress already, immidietly firing");

        return AnnounceResult.New;
    }

    public void Received(TResourceId resourceId)
    {
        if (_logger.IsTrace) _logger.Trace($"Received {resourceId}");

        _retryRequests.TryRemove(resourceId, out HashSet<IMessageHandler<TResourceId>>? _);
    }
}

public enum AnnounceResult
{
    New,
    Enqueued,
    PendingRequest
}

