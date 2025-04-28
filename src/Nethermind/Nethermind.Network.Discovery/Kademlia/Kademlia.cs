// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Kademlia;

public class Kademlia<TKey, TNode> : IKademlia<TKey, TNode> where TNode : notnull
{
    private readonly IKademliaMessageSender<TKey, TNode> _kademliaMessageSender;
    private readonly INodeHashProvider<TKey, TNode> _nodeHashProvider;
    private readonly IRoutingTable<TNode> _routingTable;
    private readonly ILookupAlgo<TKey, TNode> _lookupAlgo;
    private readonly INodeHealthTracker<TNode> _nodeHealthTracker;
    private readonly ILogger _logger;

    private readonly TNode _currentNodeId;
    private readonly TKey _currentNodeIdAsKey;
    private readonly ValueHash256 _currentNodeIdAsHash;
    private readonly int _kSize;
    private readonly TimeSpan _refreshInterval;

    public Kademlia(
        INodeHashProvider<TKey, TNode> nodeHashProvider,
        IKademliaMessageSender<TKey, TNode> sender,
        IRoutingTable<TNode> routingTable,
        ILookupAlgo<TKey, TNode> lookupAlgo,
        ILogManager logManager,
        INodeHealthTracker<TNode> nodeHealthTracker,
        KademliaConfig<TNode> config)
    {
        _nodeHashProvider = nodeHashProvider;
        _kademliaMessageSender = sender;
        _routingTable = routingTable;
        _lookupAlgo = lookupAlgo;
        _nodeHealthTracker = nodeHealthTracker;
        _logger = logManager.GetClassLogger<Kademlia<TKey, TNode>>();

        _currentNodeId = config.CurrentNodeId;
        _currentNodeIdAsKey = _nodeHashProvider.GetKey(_currentNodeId);
        _currentNodeIdAsHash = _nodeHashProvider.GetHash(_currentNodeId);
        _kSize = config.KSize;
        _refreshInterval = config.RefreshInterval;

        AddOrRefresh(_currentNodeId);
    }

    public TNode CurrentNode => _currentNodeId;

    public void AddOrRefresh(TNode node)
    {
        // It add to routing table and does the whole refresh logid.
        _nodeHealthTracker.OnIncomingMessageFrom(node);
    }
    public void Remove(TNode node)
    {
        _routingTable.Remove(_nodeHashProvider.GetHash(node));
    }

    public TNode[] GetAllAtDistance(int i)
    {
        return _routingTable.GetAllAtDistance(i);
    }

    private bool SameAsSelf(TNode node)
    {
        return _nodeHashProvider.GetHash(node) == _currentNodeIdAsHash;
    }

    public async Task<TNode[]> LookupNodesClosest(TKey key, CancellationToken token, int? k = null)
    {
        return await LookupNodesClosest(
            key,
            k ?? _kSize,
            async (nextNode, token) =>
            {
                if (SameAsSelf(nextNode))
                {
                    ValueHash256 keyHash = _nodeHashProvider.GetKeyHash(key);
                    return _routingTable.GetKNearestNeighbour(keyHash);
                }
                return await _kademliaMessageSender.FindNeighbours(nextNode, key, token);
            },
            token
        );
    }

    private Task<TNode[]> LookupNodesClosest(
        TKey target,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    )
    {
        return _lookupAlgo.Lookup(
            target,
            k,
            findNeighbourOp,
            token);
    }

    public async Task Run(CancellationToken token)
    {
        await LookupNodesClosest(_currentNodeIdAsKey, token);

        while (true)
        {
            await Bootstrap(token);
            // The main loop can potentially be parallelized with multiple concurrent lookups to improve efficiency.

            await Task.Delay(_refreshInterval, token);
        }
    }

    public async Task Bootstrap(CancellationToken token)
    {
        Stopwatch sw = Stopwatch.StartNew();
        await LookupNodesClosest(_currentNodeIdAsKey, token);

        token.ThrowIfCancellationRequested();

        // Refreshes all bucket. one by one. That is not empty.
        // A refresh means to do a k-nearest node lookup for a random hash for that particular bucket.
        foreach ((ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket) in _routingTable.IterateBuckets())
        {
            var keyToLookup = _nodeHashProvider.CreateRandomKeyAtDistance(Prefix, Distance);
            await LookupNodesClosest(keyToLookup, token);
        }

        if (_logger.IsDebug)
        {
            _logger.Debug($"Bootstrap completed. Took {sw}.");
            _routingTable.LogDebugInfo();
        }
    }

    public TNode[] GetKNeighbour(TKey target, TNode? excluding = default, bool excludeSelf = false)
    {
        ValueHash256? excludeHash = null;
        if (excluding != null) excludeHash = _nodeHashProvider.GetHash(excluding);
        ValueHash256 hash = _nodeHashProvider.GetKeyHash(target);
        return _routingTable.GetKNearestNeighbour(hash,  excludeHash, excludeSelf);
    }

    public event EventHandler<TNode> OnNodeAdded
    {
        add => _routingTable.OnNodeAdded += value;
        remove => _routingTable.OnNodeAdded -= value;
    }
}
