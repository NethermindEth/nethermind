// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Nethermind.Network.Discovery.Kademlia;

public class Kademlia<TPublicKey, THash, TNode> : IKademlia<TPublicKey, TNode>
    where TNode : notnull
    where THash : struct
{
    private readonly IKademliaMessageSender<TPublicKey, TNode> _kademliaMessageSender;
    private readonly IKeyOperator<TPublicKey, THash, TNode> _keyOperator;
    private readonly IRoutingTable<THash, TNode> _routingTable;
    private readonly ILookupAlgo<THash, TNode> _lookupAlgo;
    private readonly INodeHealthTracker<TNode> _nodeHealthTracker;
    private readonly ILogger _logger;

    private readonly TNode _currentNodeId;
    private readonly THash _currentNodeIdAsHash;
    private readonly int _kSize;
    private readonly TimeSpan _refreshInterval;
    private readonly IReadOnlyList<TNode> _bootNodes;

    public Kademlia(
        IKeyOperator<TPublicKey, THash, TNode> keyOperator,
        IKademliaMessageSender<TPublicKey, TNode> sender,
        IRoutingTable<THash, TNode> routingTable,
        ILookupAlgo<THash, TNode> lookupAlgo,
        ILoggerFactory logManager,
        INodeHealthTracker<TNode> nodeHealthTracker,
        KademliaConfig<TNode> config)
    {
        _keyOperator = keyOperator;
        _kademliaMessageSender = sender;
        _routingTable = routingTable;
        _lookupAlgo = lookupAlgo;
        _nodeHealthTracker = nodeHealthTracker;
        _logger = logManager.CreateLogger<Kademlia<TPublicKey, THash, TNode>>();

        _currentNodeId = config.CurrentNodeId;
        _currentNodeIdAsHash = _keyOperator.GetNodeHash(_currentNodeId);
        _kSize = config.KSize;
        _refreshInterval = config.RefreshInterval;
        _bootNodes = config.BootNodes;

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
        _routingTable.Remove(_keyOperator.GetNodeHash(node));
    }

    public TNode[] GetAllAtDistance(int i)
    {
        return _routingTable.GetAllAtDistance(i);
    }

    private bool SameAsSelf(TNode node)
    {
        return _keyOperator.GetNodeHash(node).Equals(_currentNodeIdAsHash);
    }

    public Task<TNode[]> LookupNodesClosest(TPublicKey key, CancellationToken token, int? k = null)
    {
        return _lookupAlgo.Lookup(
            _keyOperator.GetKeyHash(key),
            k ?? _kSize,
            async (nextNode, token) =>
            {
                if (SameAsSelf(nextNode))
                {
                    THash keyHash = _keyOperator.GetKeyHash(key);
                    return _routingTable.GetKNearestNeighbour(keyHash);
                }
                return await _kademliaMessageSender.FindNeighbours(nextNode, key, token);
            },
            token
        );
    }

    public async Task Run(CancellationToken token)
    {
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

        int onlineBootNodes = 0;

        // Check bootnodes is online
        await Parallel.ForEachAsync(_bootNodes, token, async (node, token) =>
        {
            try
            {
                // Should be added on Pong.
                await _kademliaMessageSender.Ping(node, token);
                onlineBootNodes++;
            }
            catch (OperationCanceledException)
            {
                // Unreachable
            }
        });

        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation($"Online bootnodes: {onlineBootNodes}");

        TPublicKey currentNodeIdAsKey = _keyOperator.GetKey(_currentNodeId);
        await LookupNodesClosest(currentNodeIdAsKey, token);

        token.ThrowIfCancellationRequested();

        // Refreshes all bucket. one by one. That is not empty.
        // A refresh means to do a k-nearest node lookup for a random hash for that particular bucket.
        foreach ((THash Prefix, int Distance, KBucket<THash, TNode> Bucket) in _routingTable.IterateBuckets())
        {
            var keyToLookup = _keyOperator.CreateRandomKeyAtDistance(Prefix, Distance);
            await LookupNodesClosest(keyToLookup, token);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug($"Bootstrap completed. Took {sw}.");
            _routingTable.LogDebugInfo();
        }
    }

    public TNode[] GetKNeighbour(TPublicKey target, TNode? excluding = default, bool excludeSelf = false)
    {
        THash? excludeHash = null;
        if (excluding != null) excludeHash = _keyOperator.GetNodeHash(excluding);
        THash hash = _keyOperator.GetKeyHash(target);
        return _routingTable.GetKNearestNeighbour(hash, excludeHash, excludeSelf);
    }

    public event EventHandler<TNode> OnNodeAdded
    {
        add => _routingTable.OnNodeAdded += value;
        remove => _routingTable.OnNodeAdded -= value;
    }

    public IEnumerable<TNode> IterateNodes()
    {
        foreach ((THash _, int _, KBucket<THash, TNode> Bucket) in _routingTable.IterateBuckets())
        {
            foreach (var node in Bucket.GetAll())
            {
                yield return node;
            }
        }
    }
}
