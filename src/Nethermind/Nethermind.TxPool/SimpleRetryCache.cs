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

public abstract class SimpleRetryCache
{
    public const int TimeoutMs = 2500;
    public const int CheckMs = 300;
    protected static int RequestingCacheSize = MemoryAllowance.TxHashCacheSize / 10;
}

public class SimpleRetryCache<TResourceId, TNodeId> : SimpleRetryCache
    where TResourceId : struct, IEquatable<TResourceId>
    where TNodeId : notnull, IEquatable<TNodeId>
{
    private readonly ConcurrentDictionary<TResourceId, Dictionary<TNodeId, Action>> _retryRequests = new();
    private readonly ConcurrentQueue<(TResourceId ResourceId, DateTimeOffset Expires)> _expiringQueue = new();
    private readonly ClockKeyCache<TResourceId> _requestingResources = new(RequestingCacheSize);
    private readonly ILogger _logger;

    public SimpleRetryCache(ILogManager logManager, CancellationToken token = default)
    {
        _logger = logManager.GetClassLogger();

        Task.Run(async () =>
        {
            PeriodicTimer timer = new(TimeSpan.FromMilliseconds(CheckMs));

            while (await timer.WaitForNextTickAsync(token))
            {
                while (!token.IsCancellationRequested && _expiringQueue.TryPeek(out (TResourceId ResourceId, DateTimeOffset ExpiresAfter) item) && item.ExpiresAfter <= DateTimeOffset.UtcNow)
                {
                    _expiringQueue.TryDequeue(out item);

                    if (_retryRequests.TryRemove(item.ResourceId, out Dictionary<TNodeId, Action>? requests))
                    {
                        if (requests.Count > 0)
                        {
                            _requestingResources.Set(item.ResourceId);
                        }

                        if (_logger.IsTrace) _logger.Trace($"Sending retry requests for {item.ResourceId} after timeout");


                        foreach ((TNodeId nodeId, Action request) in requests)
                        {
                            try
                            {
                                request();
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsTrace) _logger.Error($"Failed to send retry request to {nodeId} for {item.ResourceId}", ex);
                            }
                        }
                    }
                }
            }
        }, token);
    }

    public AnnounceResult Announced(TResourceId resourceId, TNodeId nodeId, Action request)
    {
        if (!_requestingResources.Contains(resourceId))
        {
            bool added = false;

            _retryRequests.AddOrUpdate(resourceId, (resourceId) =>
            {
                if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {nodeId}: NEW");

                _expiringQueue.Enqueue((resourceId, DateTimeOffset.UtcNow.AddMilliseconds(TimeoutMs)));
                added = true;

                return [];
            }, (resourceId, dict) =>
            {
                if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {nodeId}: UPDATE");

                dict[nodeId] = request;
                return dict;
            });

            return added ? AnnounceResult.New : AnnounceResult.Enqueued;
        }

        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {nodeId}, but a retry is in progress already, immidietly firing");

        return AnnounceResult.New;
    }

    public void Received(TResourceId resourceId)
    {
        if (_logger.IsTrace) _logger.Trace($"Received {resourceId}");

        _retryRequests.TryRemove(resourceId, out Dictionary<TNodeId, Action>? _);
    }
}

public enum AnnounceResult
{
    New,
    Enqueued,
    PendingRequest
}

