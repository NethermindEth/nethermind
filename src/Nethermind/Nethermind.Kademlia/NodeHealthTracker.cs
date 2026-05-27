// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Logging;
using NonBlocking;

namespace Nethermind.Kademlia;

public class NodeHealthTracker<TKey, TNode>(
    KademliaConfig<TNode> config,
    IRoutingTable<TNode> routingTable,
    INodeHashProvider<TNode> nodeHashProvider,
    IKademliaMessageSender<TKey, TNode> kademliaMessageSender,
    ILogManager logManager
) : INodeHealthTracker<TNode>, IDisposable where TNode : notnull
{
    private readonly ILogger _logger = logManager.GetClassLogger<NodeHealthTracker<TKey, TNode>>();

    private readonly ConcurrentDictionary<KademliaHash, bool> _isRefreshing = new();
    private readonly ConcurrentDictionary<KademliaHash, Task> _refreshTasks = new();
    private readonly LruCache<KademliaHash, int> _peerFailures = new(1024, "peer failure");
    private readonly KademliaHash _currentNodeIdAsHash = nodeHashProvider.GetHash(config.CurrentNodeId);
    private readonly TimeSpan _refreshPingTimeout = config.RefreshPingTimeout;
    private readonly CancellationTokenSource _refreshCancellation = new();

    private int _disposed;

    private bool SameAsSelf(TNode node) => nodeHashProvider.GetHash(node) == _currentNodeIdAsHash;

    private void TryRefresh(TNode toRefresh)
    {
        KademliaHash nodeHash = nodeHashProvider.GetHash(toRefresh);
        if (_isRefreshing.TryAdd(nodeHash, true))
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _isRefreshing.TryRemove(nodeHash, out _);
                return;
            }

            _refreshTasks[nodeHash] = RefreshAsync(toRefresh, nodeHash, _refreshCancellation.Token);
        }
    }

    private async Task RefreshAsync(TNode toRefresh, KademliaHash nodeHash, CancellationToken token)
    {
        try
        {
            // First, we delay in case any new message come and clear the refresh task, so we don't need to send any ping.
            await Task.Delay(100, token);
            if (!_isRefreshing.ContainsKey(nodeHash))
            {
                return;
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_refreshPingTimeout);
            try
            {
                await kademliaMessageSender.Ping(toRefresh, cts.Token);
                OnIncomingMessageFrom(toRefresh);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _isRefreshing.TryRemove(nodeHash, out _);
                return;
            }
            catch (OperationCanceledException)
            {
                OnRequestFailed(toRefresh);
            }
            catch (Exception e)
            {
                OnRequestFailed(toRefresh);
                if (_logger.IsDebug) _logger.Debug($"Error while refreshing node {toRefresh}, {e}");
            }

            if (_isRefreshing.TryRemove(nodeHash, out _))
            {
                routingTable.Remove(nodeHash);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _isRefreshing.TryRemove(nodeHash, out _);
        }
        finally
        {
            _refreshTasks.TryRemove(nodeHash, out _);
        }
    }

    /// <summary>
    /// Call when an incoming message from a node is received. This is used by other algorithm for health checks.
    /// </summary>
    /// <param name="node"></param>
    public void OnIncomingMessageFrom(TNode node)
    {
        _isRefreshing.TryRemove(nodeHashProvider.GetHash(node), out _);

        BucketAddResult addResult = routingTable.TryAddOrRefresh(nodeHashProvider.GetHash(node), node, out TNode? toRefresh);
        if (addResult == BucketAddResult.Full && toRefresh != null)
        {
            if (SameAsSelf(toRefresh))
            {
                // Move the current node entry to the front of its bucket.
                routingTable.TryAddOrRefresh(_currentNodeIdAsHash, toRefresh, out TNode? _);
            }
            else
            {
                TryRefresh(toRefresh);
            }
        }
        _peerFailures.Delete(nodeHashProvider.GetHash(node));
    }

    /// <summary>
    /// Call when a request to a node failed. This is used by other algorithm for health checks.
    /// </summary>
    /// <param name="node"></param>
    public void OnRequestFailed(TNode node)
    {
        KademliaHash hash = nodeHashProvider.GetHash(node);
        if (!_peerFailures.TryGet(hash, out int currentFailure))
        {
            _peerFailures.Set(hash, 1);
            return;
        }

        if (currentFailure >= config.NodeRequestFailureThreshold)
        {
            routingTable.Remove(hash);
            _peerFailures.Delete(hash);
            return;
        }

        _peerFailures.Set(hash, currentFailure + 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _refreshCancellation.Cancel();
        Task[] refreshTasks = new Task[_refreshTasks.Count];
        int refreshTaskCount = 0;
        foreach ((_, Task refreshTask) in _refreshTasks)
        {
            if (refreshTaskCount == refreshTasks.Length)
            {
                Array.Resize(ref refreshTasks, refreshTaskCount + 1);
            }

            refreshTasks[refreshTaskCount++] = refreshTask;
        }

        if (refreshTaskCount == 0)
        {
            _refreshCancellation.Dispose();
            return;
        }

        if (refreshTaskCount != refreshTasks.Length)
        {
            Array.Resize(ref refreshTasks, refreshTaskCount);
        }

        bool completed = false;
        try
        {
            completed = Task.WaitAll(refreshTasks, _refreshPingTimeout + TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException e)
        {
            completed = true;
            if (!HasOnlyCancellationExceptions(e) && _logger.IsDebug) _logger.Debug($"Error while disposing node health tracker. {e}");
        }

        if (completed)
        {
            _refreshCancellation.Dispose();
        }
    }

    private static bool HasOnlyCancellationExceptions(AggregateException e)
    {
        foreach (Exception exception in e.InnerExceptions)
        {
            if (exception is not OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }
}
