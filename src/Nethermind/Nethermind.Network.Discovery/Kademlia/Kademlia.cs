// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Kademlia;

public class Kademlia<TKey, TNode> : IKademlia<TKey, TNode> where TNode : notnull
{
    private readonly IKademliaMessageSender<TKey, TNode> _kademliaMessageSender;
    private readonly IKeyOperator<TKey, TNode> _keyOperator;
    private readonly IRoutingTable<TNode> _routingTable;
    private readonly ILookupAlgo<TNode> _lookupAlgo;
    private readonly INodeHealthTracker<TNode> _nodeHealthTracker;
    private readonly ILogger _logger;

    private readonly TNode _currentNodeId;
    private readonly ValueHash256 _currentNodeIdAsHash;
    private readonly int _kSize;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _bucketRefreshInterval;
    private readonly IReadOnlyList<TNode> _bootNodes;
    private readonly ITimestamper _timestamper;
    private readonly Dictionary<ValueHash256, long> _lastBucketRefreshTicks = [];
    private readonly object _lastBucketRefreshLock = new();

    public Kademlia(
        IKeyOperator<TKey, TNode> keyOperator,
        IKademliaMessageSender<TKey, TNode> sender,
        IRoutingTable<TNode> routingTable,
        ILookupAlgo<TNode> lookupAlgo,
        ILogManager logManager,
        INodeHealthTracker<TNode> nodeHealthTracker,
        ITimestamper timestamper,
        KademliaConfig<TNode> config)
    {
        _keyOperator = keyOperator;
        _kademliaMessageSender = sender;
        _routingTable = routingTable;
        _lookupAlgo = lookupAlgo;
        _nodeHealthTracker = nodeHealthTracker;
        _logger = logManager.GetClassLogger<Kademlia<TKey, TNode>>();

        _currentNodeId = config.CurrentNodeId;
        _currentNodeIdAsHash = _keyOperator.GetNodeHash(_currentNodeId);
        _kSize = config.KSize;
        _refreshInterval = config.RefreshInterval;
        _bucketRefreshInterval = config.BucketRefreshInterval;
        _bootNodes = config.BootNodes;
        _timestamper = timestamper;

        AddOrRefresh(_currentNodeId);
    }

    public TNode CurrentNode => _currentNodeId;

    public void AddOrRefresh(TNode node) => _nodeHealthTracker.OnIncomingMessageFrom(node);

    public void Remove(TNode node) => _routingTable.Remove(_keyOperator.GetNodeHash(node));

    public TNode[] GetAllAtDistance(int i) => _routingTable.GetAllAtDistance(i);

    private bool SameAsSelf(TNode node) => _keyOperator.GetNodeHash(node) == _currentNodeIdAsHash;

    public Task<TNode[]> LookupNodesClosest(TKey key, CancellationToken token, int? k = null) => _lookupAlgo.Lookup(
            _keyOperator.GetKeyHash(key),
            k ?? _kSize,
            async (nextNode, token) =>
            {
                if (SameAsSelf(nextNode))
                {
                    ValueHash256 keyHash = _keyOperator.GetKeyHash(key);
                    return _routingTable.GetKNearestNeighbour(keyHash);
                }
                return await _kademliaMessageSender.FindNeighbours(nextNode, key, token);
            },
            token
        );

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
                if (_logger.IsError) _logger.Error("Bootstrap iteration failed.", e);
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
                    System.Threading.Interlocked.Increment(ref onlineBootNodes);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Debug($"Bootnode ping failed for {node}. {e}");
            }
        });

        if (_logger.IsDebug) _logger.Debug($"Online bootnodes: {onlineBootNodes}");

        TKey currentNodeIdAsKey = _keyOperator.GetKey(_currentNodeId);
        await LookupNodesClosest(currentNodeIdAsKey, token);

        token.ThrowIfCancellationRequested();

        // Refresh stale non-empty buckets one by one. A refresh means to do a k-nearest node lookup for a random hash
        // for that particular bucket.
        foreach ((ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket) in _routingTable.IterateBuckets())
        {
            if (!ShouldRefreshBucket(Prefix, Bucket)) continue;

            TKey? keyToLookup = _keyOperator.CreateRandomKeyAtDistance(Prefix, Distance);
            await LookupNodesClosest(keyToLookup, token);
        }

        if (_logger.IsDebug)
        {
            _logger.Debug($"Bootstrap completed. Took {sw}.");
            _routingTable.LogDebugInfo();
        }
    }

    private bool ShouldRefreshBucket(ValueHash256 prefix, KBucket<TNode> bucket)
    {
        if (bucket.Count == 0) return false;

        long nowTicks = _timestamper.UtcNow.Ticks;
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

    public TNode[] GetKNeighbour(TKey target, TNode? excluding = default, bool excludeSelf = false)
    {
        ValueHash256? excludeHash = null;
        if (excluding != null) excludeHash = _keyOperator.GetNodeHash(excluding);
        ValueHash256 hash = _keyOperator.GetKeyHash(target);
        return _routingTable.GetKNearestNeighbour(hash, excludeHash, excludeSelf);
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
        foreach ((ValueHash256 _, int _, KBucket<TNode> Bucket) in _routingTable.IterateBuckets())
        {
            foreach (TNode node in Bucket.GetAll())
            {
                yield return node;
            }
        }
    }
}
