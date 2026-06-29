// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Collections.Pooled;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nethermind.Kademlia;

/// <summary>
/// Maintains a Kademlia routing table and runs network lookups through caller-provided message transport.
/// </summary>
public class Kademlia<TKey, TNode, TKadKey> : IKademlia<TKey, TNode>
    where TNode : notnull
    where TKadKey : notnull
{
    private readonly IKademliaMessageSender<TKey, TNode> _kademliaMessageSender;
    private readonly IKeyOperator<TKey, TNode, TKadKey> _keyOperator;
    private readonly IRoutingTable<TNode, TKadKey> _routingTable;
    private readonly ILookupAlgo<TNode, TKadKey> _lookupAlgo;
    private readonly INodeHealthTracker<TNode> _nodeHealthTracker;
    private readonly ILogger _logger;

    private readonly TNode _currentNodeId;
    private readonly TKadKey _currentNodeIdAsHash;
    private readonly int _kSize;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _bucketRefreshInterval;
    private readonly IReadOnlyList<TNode> _bootNodes;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<TKadKey, long> _lastBucketRefreshTicks = [];
    private readonly Lock _lastBucketRefreshLock = new();

    /// <summary>
    /// Creates a Kademlia table over the supplied routing, lookup, health, and transport abstractions.
    /// </summary>
    public Kademlia(
        IKeyOperator<TKey, TNode, TKadKey> keyOperator,
        IKademliaMessageSender<TKey, TNode> sender,
        IRoutingTable<TNode, TKadKey> routingTable,
        ILookupAlgo<TNode, TKadKey> lookupAlgo,
        INodeHealthTracker<TNode> nodeHealthTracker,
        KademliaConfig<TNode> config,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        _keyOperator = keyOperator;
        _kademliaMessageSender = sender;
        _routingTable = routingTable;
        _lookupAlgo = lookupAlgo;
        _nodeHealthTracker = nodeHealthTracker;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Kademlia<TKey, TNode, TKadKey>>();

        _currentNodeId = config.CurrentNodeId;
        _currentNodeIdAsHash = _keyOperator.GetNodeHash(_currentNodeId);
        _kSize = config.KSize;
        _refreshInterval = config.RefreshInterval;
        _bucketRefreshInterval = config.BucketRefreshInterval;
        _bootNodes = config.BootNodes;
        _timeProvider = timeProvider ?? TimeProvider.System;

        AddOrRefresh(_currentNodeId);
        for (int i = 0; i < _bootNodes.Count; i++)
        {
            AddOrRefresh(_bootNodes[i]);
        }
    }

    public TNode CurrentNode => _currentNodeId;

    public void AddOrRefresh(TNode node) => _nodeHealthTracker.OnIncomingMessageFrom(node);

    public void Remove(TNode node) => _routingTable.Remove(_keyOperator.GetNodeHash(node));

    public TNode[] GetAllAtDistance(int i) => _routingTable.GetAllAtDistance(i);

    private bool SameAsSelf(TNode node) => EqualityComparer<TKadKey>.Default.Equals(_keyOperator.GetNodeHash(node), _currentNodeIdAsHash);

    public Task<TNode[]> LookupNodesClosest(TKey key, CancellationToken token, int? k = null)
    {
        TKadKey keyHash = _keyOperator.GetKeyHash(key);
        return _lookupAlgo.Lookup(
            keyHash,
            k ?? _kSize,
            (nextNode, token) => FindNeighbours(key, keyHash, nextNode, token),
            token
        );
    }

    public IAsyncEnumerable<TNode> LookupNodes(TKey key, CancellationToken token, int? maxResults = null)
    {
        TKadKey keyHash = _keyOperator.GetKeyHash(key);
        return _lookupAlgo.LookupNodes(
            keyHash,
            maxResults ?? _kSize,
            (nextNode, token) => FindNeighbours(key, keyHash, nextNode, token),
            token
        );
    }

    private async Task<TNode[]?> FindNeighbours(TKey key, TKadKey keyHash, TNode nextNode, CancellationToken token)
    {
        if (SameAsSelf(nextNode))
        {
            return _routingTable.GetKNearestNeighbour(keyHash);
        }

        return await _kademliaMessageSender.FindNeighbours(nextNode, key, token);
    }

    public async Task Run(CancellationToken token)
    {
        while (true)
        {
            try
            {
                await Bootstrap(token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Bootstrap iteration failed.");
            }

            await Task.Delay(_refreshInterval, token);
        }
    }

    public async Task Bootstrap(CancellationToken token)
    {
        Stopwatch sw = Stopwatch.StartNew();

        int onlineBootNodes = 0;

        // Check bootnodes is online
        await Parallel.ForEachAsync(_bootNodes, token, async (node, token) =>
        {
            try
            {
                // Should be added on Pong.
                if (await _kademliaMessageSender.Ping(node, token))
                {
                    Interlocked.Increment(ref onlineBootNodes);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Bootnode ping failed for {node}: {e}");
            }
        });

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug($"Online bootnodes: {onlineBootNodes}");
        }

        TKey currentNodeIdAsKey = _keyOperator.GetKey(_currentNodeId);
        await LookupNodesClosest(currentNodeIdAsKey, token);

        token.ThrowIfCancellationRequested();

        // Refresh stale non-empty buckets one by one. Protocols whose wire lookup target cannot be synthesized from a
        // bucket prefix may return a best-effort random lookup key here; discv4 public keys are one example.
        using PooledSet<TKadKey> activeBucketPrefixes = new();
        foreach (RoutingTableBucket<TNode, TKadKey> bucket in _routingTable.IterateBuckets())
        {
            activeBucketPrefixes.Add(bucket.Prefix);
            if (!ShouldRefreshBucket(bucket.Prefix, bucket.Count)) continue;

            TKey? keyToLookup = _keyOperator.CreateRandomKeyAtDistance(bucket.Prefix, bucket.Distance);
            await LookupNodesClosest(keyToLookup, token);
        }

        PruneLastBucketRefreshTicks(activeBucketPrefixes);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug($"Bootstrap completed. Took {sw.Elapsed}.");
            _routingTable.LogDebugInfo();
        }
    }

    private bool ShouldRefreshBucket(TKadKey prefix, int bucketCount)
    {
        if (bucketCount == 0) return false;

        long nowTicks = _timeProvider.GetUtcNow().Ticks;
        lock (_lastBucketRefreshLock)
        {
            if (_lastBucketRefreshTicks.TryGetValue(prefix, out long lastRefreshTicks) &&
                nowTicks - lastRefreshTicks < _bucketRefreshInterval.Ticks)
            {
                return false;
            }

            _lastBucketRefreshTicks[prefix] = nowTicks;
            return true;
        }
    }

    private void PruneLastBucketRefreshTicks(ISet<TKadKey> activeBucketPrefixes)
    {
        lock (_lastBucketRefreshLock)
        {
            // Dictionary.Remove is safe during key enumeration since .NET Core 3.0.
            foreach (TKadKey prefix in _lastBucketRefreshTicks.Keys)
            {
                if (!activeBucketPrefixes.Contains(prefix))
                {
                    _lastBucketRefreshTicks.Remove(prefix);
                }
            }
        }
    }

    public TNode[] GetKNeighbour(TKey target, TNode? excluding = default, bool excludeSelf = false)
    {
        TKadKey hash = _keyOperator.GetKeyHash(target);
        if (excluding is null)
        {
            return _routingTable.GetKNearestNeighbour(hash, excludeSelf);
        }

        TKadKey excludeHash = _keyOperator.GetNodeHash(excluding);
        return _routingTable.GetKNearestNeighbourExcluding(hash, excludeHash, excludeSelf);
    }

    public event EventHandler<TNode> OnNodeAdded
    {
        add => _routingTable.OnNodeAdded += value;
        remove => _routingTable.OnNodeAdded -= value;
    }

    public event EventHandler<TNode> OnNodeRemoved
    {
        add => _routingTable.OnNodeRemoved += value;
        remove => _routingTable.OnNodeRemoved -= value;
    }

    public IEnumerable<TNode> IterateNodes()
    {
        foreach (RoutingTableBucket<TNode, TKadKey> bucket in _routingTable.IterateBuckets())
        {
            foreach (TNode node in bucket.Nodes)
            {
                yield return node;
            }
        }
    }
}
