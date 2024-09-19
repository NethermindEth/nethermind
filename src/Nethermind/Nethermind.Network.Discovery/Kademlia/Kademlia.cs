// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using NonBlocking;

namespace Nethermind.Network.Discovery.Kademlia;

public class Kademlia<TNode> : IKademlia<TNode> where TNode : notnull
{
    private readonly IMessageSender<TNode> _messageSender;
    private readonly INodeHashProvider<TNode> _nodeHashProvider;
    private readonly IRoutingTable<TNode> _routingTable;
    private readonly ILookupAlgo<TNode> _lookupAlgo;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<ValueHash256, bool> _isRefreshing = new();
    private readonly TNode _currentNodeId;
    private readonly ValueHash256 _currentNodeIdAsHash;
    private readonly int _kSize;
    private readonly LruCache<ValueHash256, int> _peerFailures;
    private readonly TimeSpan _refreshInterval;

    public Kademlia(
        INodeHashProvider<TNode> nodeHashProvider,
        IMessageSender<TNode> sender,
        IRoutingTable<TNode> routingTable,
        ILookupAlgo<TNode> lookupAlgo,
        ILogManager logManager,
        KademliaConfig<TNode> config)
    {
        _nodeHashProvider = nodeHashProvider;
        _messageSender = new MessageSenderMonitor(sender, this);
        _routingTable = routingTable;
        _lookupAlgo = lookupAlgo;
        _logger = logManager.GetClassLogger<Kademlia<TNode>>();

        _currentNodeId = config.CurrentNodeId;
        _currentNodeIdAsHash = _nodeHashProvider.GetHash(_currentNodeId);
        _kSize = config.KSize;
        _refreshInterval = config.RefreshInterval;

        _peerFailures = new LruCache<ValueHash256, int>(1024, "peer failure");

        AddOrRefresh(_currentNodeId);
    }

    public void AddOrRefresh(TNode node)
    {
        _isRefreshing.TryRemove(_nodeHashProvider.GetHash(node), out _);

        var addResult = _routingTable.TryAddOrRefresh(_nodeHashProvider.GetHash(node), node, out TNode? toRefresh);
        if (addResult == BucketAddResult.Added)
        {
            OnNodeAdded?.Invoke(this, node);
        }
        if (addResult == BucketAddResult.Full)
        {
            if (toRefresh != null)
            {
                if (SameAsSelf(toRefresh))
                {
                    _routingTable.TryAddOrRefresh(_currentNodeIdAsHash, node, out TNode? _);
                }
                else
                {
                    TryRefresh(toRefresh);
                }
            }
        }
    }

    public void Remove(TNode node)
    {
        _routingTable.Remove(_nodeHashProvider.GetHash(node));
    }

    private void TryRefresh(TNode toRefresh)
    {
        ValueHash256 nodeHash = _nodeHashProvider.GetHash(toRefresh);
        if (_isRefreshing.TryAdd(nodeHash, true))
        {
            Task.Run(async () =>
            {
                // First, we delay in case any new message come and clear the refresh task, so we don't send any ping.
                await Task.Delay(100);
                if (!_isRefreshing.ContainsKey(nodeHash))
                {
                    return;
                }

                // OK, fine, we'll ping it.
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                try
                {
                    await _messageSender.Ping(toRefresh, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Error while refreshing node {toRefresh}, {e}");
                }

                // In any case, if a pong happened, AddOrRefresh would have been called and _isRefreshing would
                // remove the entry.
                if (_isRefreshing.TryRemove(nodeHash, out _))
                {
                    _routingTable.Remove(nodeHash);
                }
            });
        }
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
                    return _routingTable.GetKNearestNeighbour(targetHash, null);
                }
                return await _messageSender.FindNeighbours(nextNode, targetHash, token);
            },
            token
        );
    }

    /// <summary>
    /// Main find closest-k node within the network. See the kademlia paper, 2.3.
    /// Since find value is basically the same also just with a shortcut, this allow changing the find neighbour op.
    /// Find closest-k is also used to determine which node should store a particular value which is used by
    /// store RPC (not implemented).
    /// </summary>
    /// <param name="targetHash"></param>
    /// <param name="k"></param>
    /// <param name="findNeighbourOp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
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

    public TNode[] GetKNeighbour(ValueHash256 hash, TNode? excluding)
    {
        ValueHash256? excludeHash = null;
        if (excluding != null) excludeHash = _nodeHashProvider.GetHash(excluding);
        return _routingTable.GetKNearestNeighbour(hash,  excludeHash);
    }

    public event EventHandler<TNode>? OnNodeAdded;

    public void OnIncomingMessageFrom(TNode sender)
    {
        AddOrRefresh(sender);
        _peerFailures.Delete(_nodeHashProvider.GetHash(sender));
    }

    public void OnRequestFailed(TNode receiver)
    {
        ValueHash256 hash = _nodeHashProvider.GetHash(receiver);
        if (!_peerFailures.TryGet(hash, out var currentFailure))
        {
            _peerFailures.Set(hash, 1);
            return;
        }

        if (currentFailure >= 5)
        {
            _routingTable.Remove(hash);
            _peerFailures.Delete(hash);
        }

        _peerFailures.Set(hash, currentFailure + 1);
    }

    /// <summary>
    /// Monitor requests for success or failure.
    /// </summary>
    /// <param name="implementation"></param>
    /// <param name="kademlia"></param>
    private class MessageSenderMonitor(IMessageSender<TNode> implementation, Kademlia<TNode> kademlia) : IMessageSender<TNode>
    {
        public async Task Ping(TNode receiver, CancellationToken token)
        {
            try
            {
                await implementation.Ping(receiver, token);
                kademlia.OnIncomingMessageFrom(receiver);
            }
            catch (OperationCanceledException)
            {
                kademlia.OnRequestFailed(receiver);
                throw;
            }
        }

        public async Task<TNode[]> FindNeighbours(TNode receiver, ValueHash256 hash, CancellationToken token)
        {
            try
            {
                TNode[] res = await implementation.FindNeighbours(receiver, hash, token);
                kademlia.OnIncomingMessageFrom(receiver);
                return res;
            }
            catch (OperationCanceledException)
            {
                kademlia.OnRequestFailed(receiver);
                throw;
            }
        }
    }
}
