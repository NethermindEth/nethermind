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

public class SimpleRetryCache<Hash256, NodeId> where Hash256 : struct, IEquatable<Hash256> where NodeId : notnull
{
    public int TimeoutMs = 3000;
    public int CheckMs = 200;
    private ILogger? _logger;

    private readonly ConcurrentDictionary<Hash256, Dictionary<NodeId, Action>> _dict = new();
    private readonly ConcurrentQueue<(Hash256 TxHash, DateTimeOffset Expires)> expiringQueue = new();
    private readonly ClockKeyCache<Hash256> _pendingHashes = new(MemoryAllowance.TxHashCacheSize / 10);

    public SimpleRetryCache(ILogManager? logManager, CancellationToken token = default)
    {
        _logger = logManager?.GetClassLogger();

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (expiringQueue.TryPeek(out (Hash256 TxHash, DateTimeOffset Expires) item) && item.Expires <= DateTimeOffset.UtcNow)
                {
                    _pendingHashes.Set(item.TxHash);
                    expiringQueue.TryDequeue(out item);
                    if (_dict.TryRemove(item.TxHash, out Dictionary<NodeId, Action>? requests))
                    {
                        foreach ((NodeId nodeId, Action request) in requests)
                        {
                            try
                            {
                                request();
                            }
                            catch (Exception ex)
                            {
                                if (_logger?.IsDebug is true) _logger?.Error($"Failed to send request to {nodeId} for tx {item.TxHash}", ex);
                            }
                        }
                    }
                }
                await Task.Delay(CheckMs, token);
            }
        }, token);
    }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="txHash"></param>
    /// <param name="nodeId"></param>
    /// <param name="request"></param>
    /// <returns>True if new added</returns>
    public AnnounceResult Announced(Hash256 txHash, NodeId nodeId, Action request)
    {
        if (!_pendingHashes.Contains(txHash))
        {
            var added = false;
            _dict.AddOrUpdate(txHash, (txHash) => Add(nodeId, txHash, ref added), (txhash, dict) => Update(nodeId, txHash, request, dict));
            return added ? AnnounceResult.New : AnnounceResult.Enqueued;
        }
        _logger?.Warn($"Announced {txHash} by {nodeId}: PENDING");

        return AnnounceResult.PendingRequest;
    }

    private Dictionary<NodeId, Action> Add(NodeId nodeId, Hash256 txHash, ref bool added)
    {
        _logger?.Warn($"Announced {txHash} by {nodeId}: NEW");

        expiringQueue.Enqueue((txHash, DateTimeOffset.UtcNow.AddMilliseconds(TimeoutMs)));
        added = true;
        return [];
    }

    private Dictionary<NodeId, Action> Update(NodeId nodeId, Hash256 txHash, Action request, Dictionary<NodeId, Action> dictionary)
    {
        _logger?.Warn($"Announced {txHash} by {nodeId}: UPDATE");

        dictionary.TryAdd(nodeId, request);
        return dictionary;
    }

    public void Received(Hash256 txHash)
    {
        _logger?.Warn($"Received {txHash}");
        _dict.TryRemove(txHash, out Dictionary<NodeId, Action>? _);
    }
}

public enum AnnounceResult
{
    New,
    Enqueued,
    PendingRequest
}
