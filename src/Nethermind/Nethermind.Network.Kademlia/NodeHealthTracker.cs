// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Network.Discovery.Discv4;
using System.Collections.Concurrent;

namespace Nethermind.Network.Discovery.Kademlia;

public class NodeHealthTracker<TPublicKey, THash, TNode>(
    KademliaConfig<TNode> config,
    IRoutingTable<THash, TNode> routingTable,
    INodeHashProvider<THash, TNode> nodeHashProvider,
    IKademliaMessageSender<TPublicKey, TNode> kademliaMessageSender,
    ILoggerFactory logManager
) : INodeHealthTracker<TNode> where TNode : notnull where THash : struct, IKademiliaHash<THash>
{
    private readonly ILogger _logger = logManager.CreateLogger<NodeHealthTracker<TPublicKey, THash, TNode>>();

    private readonly ConcurrentDictionary<THash, bool> _isRefreshing = new();
    private readonly LruCache<THash, int> _peerFailures = new(1024, "peer failure");
    private readonly THash _currentNodeIdAsHash = nodeHashProvider.GetHash(config.CurrentNodeId);
    private readonly TimeSpan _refreshPingTimeout = config.RefreshPingTimeout;

    private bool SameAsSelf(TNode node)
    {
        return nodeHashProvider.GetHash(node).Equals(_currentNodeIdAsHash);
    }

    private void TryRefresh(TNode toRefresh)
    {
        THash nodeHash = nodeHashProvider.GetHash(toRefresh);
        if (_isRefreshing.TryAdd(nodeHash, true))
        {
            Task.Run(async () =>
            {
                // First, we delay in case any new message come and clear the refresh task, so we don't need to send any ping.
                await Task.Delay(100);
                if (!_isRefreshing.ContainsKey(nodeHash))
                {
                    return;
                }

                // OK, fine, we'll ping it.
                using CancellationTokenSource cts = new CancellationTokenSource(_refreshPingTimeout);
                try
                {
                    await kademliaMessageSender.Ping(toRefresh, cts.Token);
                    OnIncomingMessageFrom(toRefresh);
                }
                catch (OperationCanceledException)
                {
                    OnRequestFailed(toRefresh);
                }
                catch (Exception e)
                {
                    OnRequestFailed(toRefresh);
                    if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Error while refreshing node {toRefresh}, {e}");
                }

                if (_isRefreshing.TryRemove(nodeHash, out _))
                {
                    routingTable.Remove(nodeHash);
                }
            });
        }
    }

    /// <summary>
    /// Call when an incoming message from a node is received. This is used by other algorithm for health checks.
    /// </summary>
    /// <param name="node"></param>
    public void OnIncomingMessageFrom(TNode node)
    {
        _isRefreshing.TryRemove(nodeHashProvider.GetHash(node), out _);

        var addResult = routingTable.TryAddOrRefresh(nodeHashProvider.GetHash(node), node, out TNode? toRefresh);
        if (addResult == BucketAddResult.Full && toRefresh != null)
        {
            if (SameAsSelf(toRefresh))
            {
                // Move the current node entry to the front of its bucket.
                routingTable.TryAddOrRefresh(_currentNodeIdAsHash, node, out TNode? _);
            }
            else
            {
                TryRefresh(toRefresh);
            }
        }
        _peerFailures.Delete(nodeHashProvider.GetHash(node));
    }

    /// <summary>
    /// Call when a requset to a node failed. This is used by other algorithm for health checks.
    /// </summary>
    /// <param name="node"></param>
    public void OnRequestFailed(TNode node)
    {
        THash hash = nodeHashProvider.GetHash(node);
        if (!_peerFailures.TryGet(hash, out var currentFailure))
        {
            _peerFailures.Set(hash, 1);
            return;
        }

        if (currentFailure >= config.NodeRequestFailureThreshold)
        {
            routingTable.Remove(hash);
            _peerFailures.Delete(hash);
        }

        _peerFailures.Set(hash, currentFailure + 1);
    }
}
