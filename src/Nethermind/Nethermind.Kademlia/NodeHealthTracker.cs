// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nethermind.Kademlia;

/// <summary>
/// Tracks node liveness signals and evicts peers that repeatedly fail Kademlia requests.
/// </summary>
/// <remarks>
/// Health checks and failure resets are serialized. Cooperative table mutations return deferred
/// notifications, published after both table and health serialization have been released.
/// </remarks>
public class NodeHealthTracker<TKey, TNode, TKadKey>(
    KademliaConfig<TNode> config,
    IHealthRoutingTable<TNode, TKadKey> routingTable,
    INodeHashProvider<TNode, TKadKey> nodeHashProvider,
    IKademliaMessageSender<TKey, TNode> kademliaMessageSender,
    ILoggerFactory loggerFactory
) : INodeHealthTracker<TNode>, IDisposable, IAsyncDisposable
    where TNode : notnull
    where TKadKey : notnull
{
    public NodeHealthTracker(
        KademliaConfig<TNode> config,
        IHealthRoutingTable<TNode, TKadKey> routingTable,
        INodeHashProvider<TNode, TKadKey> nodeHashProvider,
        IKademliaMessageSender<TKey, TNode> kademliaMessageSender)
        : this(config, routingTable, nodeHashProvider, kademliaMessageSender, NullLoggerFactory.Instance)
    {
    }

    private readonly Lock _healthLock = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<NodeHealthTracker<TKey, TNode, TKadKey>>();

    private readonly ConcurrentDictionary<TKadKey, bool> _isRefreshing = new();
    private readonly ConcurrentDictionary<TKadKey, Task> _refreshTasks = new();
    private readonly PeerFailureCache _peerFailures = new(1024, config.AreSameEndpoint);
    private readonly TKadKey _currentNodeIdAsHash = nodeHashProvider.GetHash(config.CurrentNodeId);
    private readonly TimeSpan _refreshPingTimeout = config.RefreshPingTimeout;
    private readonly TimeSpan _refreshPingDelay = config.RefreshPingDelay;
    private readonly CancellationTokenSource _refreshCancellation = new();

    internal TimeProvider RefreshTimeProvider { init => _peerFailures.TimeProvider = value; }

    private int _disposed;

    private bool SameAsSelf(TNode node) => EqualityComparer<TKadKey>.Default.Equals(nodeHashProvider.GetHash(node), _currentNodeIdAsHash);

    private bool TryRefresh(TNode toRefresh, TNode[]? replacements = null, int nextReplacement = 0)
    {
        TKadKey nodeHash = nodeHashProvider.GetHash(toRefresh);
        if (_isRefreshing.TryAdd(nodeHash, true))
        {
            if (Volatile.Read(ref _disposed) != 0 || !_peerFailures.TryBeginRefresh(nodeHash, toRefresh, config.RefreshRetryInterval))
            {
                _isRefreshing.TryRemove(nodeHash, out _);
                return false;
            }

            _refreshTasks[nodeHash] = RefreshAsync(toRefresh, nodeHash, _refreshCancellation.Token, replacements, nextReplacement);
            return true;
        }
        return false;
    }

    private async Task RefreshAsync(TNode toRefresh, TKadKey nodeHash, CancellationToken token, TNode[]? replacements, int nextReplacement)
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
                else if (_isRefreshing.ContainsKey(nodeHash))
                {
                    if (replacements is null) OnRequestFailed(toRefresh);
                    else TryRefreshReplacement(replacements, nextReplacement);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _isRefreshing.TryRemove(nodeHash, out _);
                return;
            }
            catch (KademliaRequestNotSentException)
            {
                _isRefreshing.TryRemove(nodeHash, out _);
                return;
            }
            catch (Exception e)
            {
                if (replacements is null) OnRequestFailed(toRefresh);
                else TryRefreshReplacement(replacements, nextReplacement);
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Error while refreshing node {toRefresh}: {e}");
            }

            _isRefreshing.TryRemove(nodeHash, out _);
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
        if (SameAsSelf(node)) node = config.CurrentNodeId;
        RoutingTableNotifications<TNode> notifications;
        TNode? toRefresh;
        bool refresh;
        lock (_healthLock) refresh = RecordIncoming(node, out toRefresh, out notifications);
        routingTable.Publish(notifications);
        if (refresh) TryRefresh(toRefresh!);
    }

    private bool RecordIncoming(TNode node, out TNode? toRefresh, out RoutingTableNotifications<TNode> notifications)
    {
        TKadKey hash = nodeHashProvider.GetHash(node);
        _isRefreshing.TryRemove(hash, out _);
        _peerFailures.ResetFailures(hash, node);
        notifications = default;
        BucketAddResult result = routingTable.AddOrRefreshDeferred(hash, node, out toRefresh, out notifications);
        if (result != BucketAddResult.Full || toRefresh is null) return false;
        if (!SameAsSelf(toRefresh)) return true;

        routingTable.AddOrRefreshDeferred(_currentNodeIdAsHash, toRefresh, out _, out notifications);
        return false;
    }

    /// <summary>
    /// Call when a request to a node failed. This is used by other algorithm for health checks.
    /// </summary>
    /// <param name="node"></param>
    public void OnRequestFailed(TNode node)
    {
        RoutingTableNotifications<TNode> notifications;
        TNode[] candidates;
        lock (_healthLock) RecordFailure(node, out notifications, out candidates);
        routingTable.Publish(notifications);
        if (candidates.Length != 0) TryRefreshReplacement(candidates, 0);
    }

    private void RecordFailure(TNode node, out RoutingTableNotifications<TNode> notifications, out TNode[] candidates)
    {
        notifications = default;
        candidates = [];
        TKadKey hash = nodeHashProvider.GetHash(node);
        if (SameAsSelf(node) || !routingTable.TryGetActive(hash, out TNode? known, out object? tableRevision) || !config.AreSameEndpoint(known, node)) return;
        if (_peerFailures.Increment(hash, node, out long revision) < Math.Max(1, config.NodeRequestFailureThreshold)) return;
        bool removed = routingTable.Remove(hash, known, tableRevision!, out notifications);
        if (!removed) return;

        _peerFailures.Delete(hash, revision);
        if (config.RequireReplacementValidation) candidates = routingTable.GetReplacementCandidates(hash);
    }

    private void TryRefreshReplacement(TNode[] candidates, int start)
    {
        int count = Math.Min(candidates.Length, KBucket<TNode, TKadKey>.DefaultReplacementCacheSize);
        for (int i = start; i < count; i++)
        {
            if (TryRefresh(candidates[i], candidates, i + 1)) return;
        }
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

    private sealed class PeerFailureCache(int capacity, Func<TNode, TNode, bool> areSameEndpoint)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<TKadKey, Entry> _values = [with(capacity)];
        private readonly LinkedList<TKadKey> _order = [];
        private long _revision;
        public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

        private sealed class Entry(TNode node, LinkedListNode<TKadKey> orderNode)
        {
            public TNode Node = node;
            public int FailureCount;
            public long Revision;
            public long LastRefresh;
            public bool HasRefreshed;
            public LinkedListNode<TKadKey> OrderNode = orderNode;
        }

        public int Increment(TKadKey hash, TNode node, out long revision)
        {
            lock (_lock)
            {
                Entry entry = GetOrAdd(hash, node);
                entry.Revision = revision = ++_revision;
                return ++entry.FailureCount;
            }
        }

        public bool TryBeginRefresh(TKadKey hash, TNode node, TimeSpan interval)
        {
            lock (_lock)
            {
                Entry entry = GetOrAdd(hash, node);
                long now = TimeProvider.GetTimestamp();
                if (entry.HasRefreshed && TimeProvider.GetElapsedTime(entry.LastRefresh, now) < interval) return false;
                entry.LastRefresh = now;
                entry.HasRefreshed = true;
                return true;
            }
        }

        public void ResetFailures(TKadKey hash, TNode node)
        {
            lock (_lock)
            {
                if (_values.ContainsKey(hash))
                {
                    Entry entry = GetOrAdd(hash, node);
                    entry.FailureCount = 0;
                    entry.Revision = ++_revision;
                }
            }
        }

        private Entry GetOrAdd(TKadKey hash, TNode node)
        {
            if (_values.TryGetValue(hash, out Entry? entry))
            {
                _order.Remove(entry.OrderNode);
                _order.AddLast(entry.OrderNode);
                if (!areSameEndpoint(entry.Node, node))
                {
                    entry.Node = node;
                    entry.FailureCount = 0;
                    entry.Revision = ++_revision;
                    entry.HasRefreshed = false;
                }
                return entry;
            }

            entry = new Entry(node, _order.AddLast(hash));
            _values.Add(hash, entry);
            if (_values.Count > capacity)
            {
                LinkedListNode<TKadKey> oldest = _order.First!;
                _order.RemoveFirst();
                _values.Remove(oldest.Value);
            }
            return entry;
        }

        public void Delete(TKadKey hash, long revision)
        {
            lock (_lock)
            {
                if (_values.TryGetValue(hash, out Entry? entry) && entry.Revision == revision)
                {
                    _values.Remove(hash);
                    _order.Remove(entry.OrderNode);
                }
            }
        }
    }
}
