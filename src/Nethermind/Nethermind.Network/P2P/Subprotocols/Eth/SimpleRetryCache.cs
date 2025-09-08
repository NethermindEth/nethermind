// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.P2P.Subprotocols.Eth;

public class SimpleRetryCache<Hash256, NodeId> where Hash256 : notnull where NodeId : notnull
{
    public int TimeoutMs = 3000;
    public int CheckMs = 200;
    private ILogger? _logger;

    private readonly ConcurrentDictionary<Hash256, Dictionary<NodeId, Action>> _dict = new();
    private readonly ConcurrentQueue<(Hash256 TxHash, DateTimeOffset Expires)> expiringQueue = new();

    public SimpleRetryCache(ILogManager? logManager, CancellationToken token = default)
    {
        _logger = logManager?.GetClassLogger();

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (expiringQueue.TryPeek(out (Hash256 TxHash, DateTimeOffset Expires) item) && item.Expires <= DateTimeOffset.UtcNow)
                {
                    expiringQueue.TryDequeue(out item);
                    if (_dict.TryRemove(item.TxHash, out Dictionary<NodeId, Action> requests))
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
    public bool Announced(Hash256 txHash, NodeId nodeId, Action request)
    {
        bool added = false;
        _dict.AddOrUpdate(txHash, (txHash) => Add(txHash, ref added), (txhash, dict) => Update(nodeId, request, dict));
        return added;
    }

    private Dictionary<NodeId, Action> Add(Hash256 txHash, ref bool added)
    {
        expiringQueue.Enqueue((txHash, DateTimeOffset.UtcNow.AddMilliseconds(TimeoutMs)));
        added = true;
        return [];
    }

    private static Dictionary<NodeId, Action> Update(NodeId nodeId, Action request, Dictionary<NodeId, Action> dictionary)
    {
        dictionary.TryAdd(nodeId, request);
        return dictionary;
    }

    public void Received(Hash256 txHash)
    {
        _dict.TryRemove(txHash, out Dictionary<NodeId, Action> _);
    }
}
