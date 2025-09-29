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
/// Allows to announce request for a resource and track other nodes to request it from in case of timeout
/// </summary>
/// <typeparam name="TResourceId">Resource identifier</typeparam>
/// <typeparam name="TNodeId"></typeparam>
public class SimpleRetryCache<TResourceId, TNodeId>
    where TResourceId : struct, IEquatable<TResourceId>
    where TNodeId : notnull, IEquatable<TNodeId>
{
    public int TimeoutMs = 2000;
    public int CheckMs = 300;

    private readonly ConcurrentDictionary<TResourceId, Dictionary<TNodeId, Action>> _dict = new();
    private readonly ConcurrentQueue<(TResourceId TxHash, DateTimeOffset Expires)> expiringQueue = new();
    private readonly ClockKeyCache<TResourceId> _pendingHashes = new(MemoryAllowance.TxHashCacheSize / 10);
    private ILogger _logger;

    public SimpleRetryCache(ILogManager logManager, CancellationToken token = default)
    {
        _logger = logManager.GetClassLogger();

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                while (!token.IsCancellationRequested && expiringQueue.TryPeek(out (TResourceId ResId, DateTimeOffset Expires) item) && item.Expires <= DateTimeOffset.UtcNow)
                {
                    expiringQueue.TryDequeue(out item);

                    if (_dict.TryRemove(item.ResId, out Dictionary<TNodeId, Action>? requests))
                    {
                        if (requests.Count > 0)
                        {
                            _pendingHashes.Set(item.ResId);
                        }

                        if (_logger.IsTrace) _logger.Trace($"Sending retry requests for {item.ResId} after timeout");


                        foreach ((TNodeId nodeId, Action request) in requests)
                        {
                            try
                            {
                                request();
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsTrace) _logger.Error($"Failed to send retry request to {nodeId} for tx {item.ResId}", ex);
                            }
                        }
                    }
                }

                await Task.Delay(CheckMs, token);
            }
        }, token);
    }

    public AnnounceResult Announced(TResourceId txHash, TNodeId nodeId, Action request)
    {
        if (!_pendingHashes.Contains(txHash))
        {
            bool added = false;
            _dict.AddOrUpdate(txHash, (txHash) => Add(nodeId, txHash, ref added), (txhash, dict) => Update(nodeId, txHash, request, dict));
            return added ? AnnounceResult.New : AnnounceResult.Enqueued;
        }

        if(_logger.IsTrace)_logger.Trace($"Announced {txHash} by {nodeId}: PENDING");

        return AnnounceResult.PendingRequest;
    }

    private Dictionary<TNodeId, Action> Add(TNodeId nodeId, TResourceId txHash, ref bool added)
    {
        if (_logger.IsTrace) _logger.Trace($"Announced {txHash} by {nodeId}: NEW");

        expiringQueue.Enqueue((txHash, DateTimeOffset.UtcNow.AddMilliseconds(TimeoutMs)));
        added = true;

        return [];
    }

    private Dictionary<TNodeId, Action> Update(TNodeId nodeId, TResourceId txHash, Action request, Dictionary<TNodeId, Action> dictionary)
    {
        if (_logger.IsTrace) _logger.Trace($"Announced {txHash} by {nodeId}: UPDATE");

        dictionary.TryAdd(nodeId, request);
        return dictionary;
    }

    public void Received(TResourceId txHash)
    {
        if (_logger.IsTrace) _logger.Trace($"Received {txHash}");
        _dict.TryRemove(txHash, out Dictionary<TNodeId, Action>? _);
    }
}

public enum AnnounceResult
{
    New,
    Enqueued,
    PendingRequest
}
