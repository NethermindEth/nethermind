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

/// <summary>
/// Allows to announce request for a resource and track other nodes to request it from in case of timeout. The code does not request the resource initially, but does so on timeout.
/// </summary>
/// <typeparam name="TResourceId">Resource identifier</typeparam>
/// <typeparam name="TNodeId">Node that can be queried for the resource</typeparam>
public interface ISimpleRetryCache<TResourceId, TNodeId>
    where TResourceId : struct, IEquatable<TResourceId>
    where TNodeId : notnull, IEquatable<TNodeId>
{
    AnnounceResult Announced(TResourceId resourceId, TNodeId nodeId, Action request);
    void Received(TResourceId resourceId);
}
public abstract class SimpleRetryCache
{
    public const int TimeoutMs = 2500;
    public const int CheckMs = 300;
}

public class SimpleRetryCache<TResourceId, TNodeId> : SimpleRetryCache, ISimpleRetryCache<TResourceId, TNodeId>
    where TResourceId : struct, IEquatable<TResourceId>
    where TNodeId : notnull, IEquatable<TNodeId>
{
    private readonly ConcurrentDictionary<TResourceId, Dictionary<TNodeId, Action>> _retryRequests = new();
    private readonly ConcurrentQueue<(TResourceId ResourceId, DateTimeOffset Expires)> expiringQueue = new();
    private readonly ClockKeyCache<TResourceId> _requestingResources = new(MemoryAllowance.TxHashCacheSize / 10);
    private readonly ILogger _logger;

    public SimpleRetryCache(ILogManager logManager, CancellationToken token = default)
    {
        _logger = logManager.GetClassLogger();

        Task.Run(async () =>
        {
            PeriodicTimer timer = new(TimeSpan.FromMilliseconds(CheckMs));

            while (await timer.WaitForNextTickAsync(token))
            {
                while (!token.IsCancellationRequested && expiringQueue.TryPeek(out (TResourceId ResourceId, DateTimeOffset ExpiresAfter) item) && item.ExpiresAfter <= DateTimeOffset.UtcNow)
                {
                    expiringQueue.TryDequeue(out item);

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
            _retryRequests.AddOrUpdate(resourceId, (resourceId) => Add(nodeId, resourceId, ref added), (resourceId, dict) => Update(nodeId, resourceId, request, dict));
            return added ? AnnounceResult.New : AnnounceResult.Enqueued;
        }

        if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {nodeId}, but a retry is in progress already, immidietly firing");
        return AnnounceResult.New;

        Dictionary<TNodeId, Action> Add(TNodeId nodeId, TResourceId resourceId, ref bool added)
        {
            if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {nodeId}: NEW");

            expiringQueue.Enqueue((resourceId, DateTimeOffset.UtcNow.AddMilliseconds(TimeoutMs)));
            added = true;

            return [];
        }

        Dictionary<TNodeId, Action> Update(TNodeId nodeId, TResourceId resourceId, Action request, Dictionary<TNodeId, Action> dictionary)
        {
            if (_logger.IsTrace) _logger.Trace($"Announced {resourceId} by {nodeId}: UPDATE");

            dictionary[nodeId] = request;
            return dictionary;
        }
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


public class NullSimpleRetryCache<TResourceId, TNodeId> : ISimpleRetryCache<TResourceId, TNodeId>
    where TResourceId : struct, IEquatable<TResourceId>
    where TNodeId : notnull, IEquatable<TNodeId>
{
    public AnnounceResult Announced(TResourceId resourceId, TNodeId nodeId, Action request) => AnnounceResult.New;
    public void Received(TResourceId resourceId) { }
}
