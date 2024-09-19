// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Kademlia;

public class Kademlia<TNode> : IKademlia<TNode> where TNode : notnull
{
    private readonly IKademliaMessageSender<TNode> _kademliaMessageSender;
    private readonly INodeHashProvider<TNode> _nodeHashProvider;
    private readonly IRoutingTable<TNode> _routingTable;
    private readonly ILookupAlgo<TNode> _lookupAlgo;
    private readonly NodeHealthTracker<TNode> _nodeHealthTracker;
    private readonly ILogger _logger;

    private readonly TNode _currentNodeId;
    private readonly ValueHash256 _currentNodeIdAsHash;
    private readonly int _kSize;
    private readonly TimeSpan _refreshInterval;

    public Kademlia(
        INodeHashProvider<TNode> nodeHashProvider,
        IKademliaMessageSender<TNode> sender,
        IRoutingTable<TNode> routingTable,
        ILookupAlgo<TNode> lookupAlgo,
        ILogManager logManager,
        NodeHealthTracker<TNode> nodeHealthTracker,
        KademliaConfig<TNode> config)
    {
        _nodeHashProvider = nodeHashProvider;
        _kademliaMessageSender = sender;
        _routingTable = routingTable;
        _lookupAlgo = lookupAlgo;
        _nodeHealthTracker = nodeHealthTracker;
        _logger = logManager.GetClassLogger<Kademlia<TNode>>();

        _currentNodeId = config.CurrentNodeId;
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

    public async Task<TNode[]> LookupNodesClosest(ValueHash256 targetHash, CancellationToken token, int? k = null)
    {
        return await LookupNodesClosest(
            targetHash,
            k ?? _kSize,
            async (nextNode, token) =>
            {
                if (SameAsSelf(nextNode))
                {
                    return _routingTable.GetKNearestNeighbour(targetHash);
                }
                return await _kademliaMessageSender.FindNeighbours(nextNode, targetHash, token);
            },
            token
        );
    }

    private Task<TNode[]> LookupNodesClosest(
        ValueHash256 targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    )
    {
        return _lookupAlgo.Lookup(
            targetHash,
            k,
            findNeighbourOp,
            token);
    }

    public async Task Run(CancellationToken token)
    {
        await LookupNodesClosest(_currentNodeIdAsHash, token);

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
        await LookupNodesClosest(_currentNodeIdAsHash, token);

        token.ThrowIfCancellationRequested();

        // Refreshes all bucket. one by one. That is not empty.
        // A refresh means to do a k-nearest node lookup for a random hash for that particular bucket.
        foreach (ValueHash256 nodeToLookup in _routingTable.IterateBucketRandomHashes())
        {
            await LookupNodesClosest(nodeToLookup, token);
        }

        if (_logger.IsDebug)
        {
            _logger.Debug($"Bootstrap completed. Took {sw}.");
            _routingTable.LogDebugInfo();
        }
    }

    public TNode[] GetKNeighbour(ValueHash256 hash, TNode? excluding = default, bool excludeSelf = false)
    {
        ValueHash256? excludeHash = null;
        if (excluding != null) excludeHash = _nodeHashProvider.GetHash(excluding);
        return _routingTable.GetKNearestNeighbour(hash,  excludeHash, excludeSelf);
    }

    public event EventHandler<TNode> OnNodeAdded
    {
        add => _routingTable.OnNodeAdded += value;
        remove => _routingTable.OnNodeAdded -= value;
    }
}
