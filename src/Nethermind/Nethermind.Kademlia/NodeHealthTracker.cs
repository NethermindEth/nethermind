// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nethermind.Kademlia;

/// <summary>
/// Tracks node liveness signals and evicts peers that repeatedly fail Kademlia requests.
/// </summary>
public class NodeHealthTracker<TKey, TNode, TKadKey>(
    KademliaConfig<TNode> config,
    IRoutingTable<TNode, TKadKey> routingTable,
    INodeHashProvider<TNode, TKadKey> nodeHashProvider,
    IKademliaMessageSender<TKey, TNode> kademliaMessageSender,
    ILoggerFactory loggerFactory
) : INodeHealthTracker<TNode>, IDisposable, IAsyncDisposable
    where TNode : notnull
    where TKadKey : notnull
{
    public NodeHealthTracker(
        KademliaConfig<TNode> config,
        IRoutingTable<TNode, TKadKey> routingTable,
        INodeHashProvider<TNode, TKadKey> nodeHashProvider,
        IKademliaMessageSender<TKey, TNode> kademliaMessageSender)
        : this(config, routingTable, nodeHashProvider, kademliaMessageSender, NullLoggerFactory.Instance)
    {
    }

    private readonly ILogger _logger = loggerFactory.CreateLogger<NodeHealthTracker<TKey, TNode, TKadKey>>();

    private readonly ConcurrentDictionary<TKadKey, bool> _isRefreshing = new();
    private readonly ConcurrentDictionary<TKadKey, Task> _refreshTasks = new();
    private readonly PeerFailureCache _peerFailures = new(1024);
    private readonly TKadKey _currentNodeIdAsHash = nodeHashProvider.GetHash(config.CurrentNodeId);
    private readonly TimeSpan _refreshPingTimeout = config.RefreshPingTimeout;
    private readonly TimeSpan _refreshPingDelay = config.RefreshPingDelay;
    private readonly CancellationTokenSource _refreshCancellation = new();

    private int _disposed;

    private bool SameAsSelf(TNode node) => EqualityComparer<TKadKey>.Default.Equals(nodeHashProvider.GetHash(node), _currentNodeIdAsHash);

    private void TryRefresh(TNode toRefresh)
    {
        TKadKey nodeHash = nodeHashProvider.GetHash(toRefresh);
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

    private async Task RefreshAsync(TNode toRefresh, TKadKey nodeHash, CancellationToken token)
    {
        try
        {
            // First, we delay in case any new message come and clear the refresh task, so we don't need to send any ping.
            await Task.Delay(_refreshPingDelay, token);
            if (!_isRefreshing.ContainsKey(nodeHash))
            {
                return;
            }

            try
            {
                if (await kademliaMessageSender.Ping(toRefresh, token))
                {
                    OnIncomingMessageFrom(toRefresh);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _isRefreshing.TryRemove(nodeHash, out _);
                return;
            }
            catch (Exception e)
            {
                OnRequestFailed(toRefresh);
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Error while refreshing node {toRefresh}: {e}");
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
        if (addResult == BucketAddResult.Full && toRefresh is not null)
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
        TKadKey hash = nodeHashProvider.GetHash(node);
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
        Task[] refreshTasks = CancelAndGetRefreshTasks();
        if (refreshTasks.Length == 0) return;

        bool completed = false;
        try
        {
            completed = Task.WaitAll(refreshTasks, _refreshPingTimeout + TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException e)
        {
            completed = true;
            if (!HasOnlyCancellationExceptions(e))
            {
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Error while disposing node health tracker: {e}");
            }
        }

        if (completed)
        {
            _refreshCancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] refreshTasks = CancelAndGetRefreshTasks();
        if (refreshTasks.Length == 0) return;

        bool completed = false;
        try
        {
            await Task.WhenAll(refreshTasks).WaitAsync(_refreshPingTimeout + TimeSpan.FromMilliseconds(500));
            completed = true;
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
            completed = true;
        }
        catch (Exception e)
        {
            completed = true;
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Error while disposing node health tracker: {e}");
        }

        if (completed)
        {
            _refreshCancellation.Dispose();
        }
    }

    private Task[] CancelAndGetRefreshTasks()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return [];
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
            return [];
        }

        if (refreshTaskCount != refreshTasks.Length)
        {
            Array.Resize(ref refreshTasks, refreshTaskCount);
        }

        return refreshTasks;
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

    private sealed class PeerFailureCache(int capacity)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<TKadKey, (int FailureCount, LinkedListNode<TKadKey> OrderNode)> _values = new(capacity);
        private readonly LinkedList<TKadKey> _order = [];

        public bool TryGet(TKadKey hash, out int failureCount)
        {
            lock (_lock)
            {
                if (!_values.TryGetValue(hash, out (int FailureCount, LinkedListNode<TKadKey> OrderNode) entry))
                {
                    failureCount = 0;
                    return false;
                }

                _order.Remove(entry.OrderNode);
                _order.AddLast(entry.OrderNode);
                failureCount = entry.FailureCount;
                return true;
            }
        }

        public void Set(TKadKey hash, int failureCount)
        {
            lock (_lock)
            {
                if (_values.TryGetValue(hash, out (int FailureCount, LinkedListNode<TKadKey> OrderNode) entry))
                {
                    _order.Remove(entry.OrderNode);
                    _order.AddLast(entry.OrderNode);
                    _values[hash] = (failureCount, entry.OrderNode);
                    return;
                }

                LinkedListNode<TKadKey> orderNode = _order.AddLast(hash);
                _values[hash] = (failureCount, orderNode);
                Trim();
            }
        }

        public void Delete(TKadKey hash)
        {
            lock (_lock)
            {
                if (_values.Remove(hash, out (int FailureCount, LinkedListNode<TKadKey> OrderNode) entry))
                {
                    _order.Remove(entry.OrderNode);
                }
            }
        }

        private void Trim()
        {
            while (_values.Count > capacity)
            {
                LinkedListNode<TKadKey> oldest = _order.First!;

                _order.RemoveFirst();
                _values.Remove(oldest.Value);
            }
        }
    }
}
