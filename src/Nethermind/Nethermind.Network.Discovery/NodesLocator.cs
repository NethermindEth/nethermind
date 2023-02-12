// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public class NodesLocator : INodesLocator
{
    private static HashSet<Keccak>? s_triedNodesCache;
    private readonly ILogger _logger;
    private readonly NodeTable _nodeTable;
    private readonly IDiscoveryManager _discoveryManager;
    private readonly IDiscoveryConfig _discoveryConfig;
    private Node? _masterNode;

    public NodesLocator(NodeTable? nodeTable, IDiscoveryManager? discoveryManager, IDiscoveryConfig? discoveryConfig, ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _discoveryManager = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
    }

    public void Initialize(Node masterNode)
    {
        _masterNode = masterNode;
    }

    public ValueTask LocateNodesAsync(CancellationToken cancellationToken) => LocateNodesAsync(null, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask LocateNodesAsync(byte[]? searchedNodeId, CancellationToken cancellationToken)
    {
        if (_masterNode is null)
        {
            throw new InvalidOperationException("Master node has not been initialized");
        }

        HashSet<Keccak> alreadyTriedNodes = Interlocked.Exchange(ref s_triedNodesCache, null) ?? new HashSet<Keccak>();

        if (_logger.IsDebug) _logger.Debug($"Starting discovery process for node: {(searchedNodeId is not null ? $"randomNode: {new PublicKey(searchedNodeId).ToShortString()}" : $"masterNode: {_masterNode.Id}")}");
        int nodesCountBeforeDiscovery = NodesCountBeforeDiscovery;

        Node[] tryCandidates = new Node[_discoveryConfig.BucketSize]; // max bucket size here
        for (int i = 0; i < _discoveryConfig.MaxDiscoveryRounds; i++)
        {
            Array.Clear(tryCandidates, 0, tryCandidates.Length);
            int candidatesCount;

            int attemptsCount = 0;
            while (true)
            {
                candidatesCount = 0;
                if (searchedNodeId is null)
                {
                    //if searched node is not specified master node is used
                    _nodeTable.FillClosestNodes(tryCandidates, alreadyTriedNodes);
                }
                else
                {
                    _nodeTable.FillClosestNodes(searchedNodeId, tryCandidates, alreadyTriedNodes);
                }

                if (attemptsCount++ > 20 || candidatesCount > 0)
                {
                    break;
                }

                if (_logger.IsTrace) _logger.Trace($"Waiting {_discoveryConfig.DiscoveryNewCycleWaitTime} for new nodes");

                //we need to wait some time for pong messages received from new nodes we reached out to
                try
                {
                    await Task.Delay(_discoveryConfig.DiscoveryNewCycleWaitTime, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (candidatesCount == 0)
            {
                if (_logger.IsTrace) _logger.Trace("No more closer candidates");
                break;
            }

            int successRequestsCount = 0;
            int failRequestCount = 0;
            int nodesTriedCount = 0;
            while (true)
            {
                int count = failRequestCount > 0 ? failRequestCount : _discoveryConfig.Concurrency;
                IEnumerable<Node> nodesToSend = tryCandidates.Skip(nodesTriedCount).Take(count);

                IEnumerable<Task<Result>> sendFindNodeTasks = SendFindNodes(searchedNodeId, nodesToSend, alreadyTriedNodes);
                Result?[] results = await Task.WhenAll(sendFindNodeTasks);

                if (results.Length == 0)
                {
                    if (_logger.IsDebug) _logger.Debug($"No more nodes to send, sent {successRequestsCount} successful requests, failedRequestCounter: {failRequestCount}, nodesTriedCounter: {nodesTriedCount}");
                    break;
                }

                nodesTriedCount += results.Length;

                foreach (Result? result in results)
                {
                    if ((result?.ResultType ?? ResultType.Failure) == ResultType.Failure)
                    {
                        failRequestCount++;
                    }
                    else
                    {
                        successRequestsCount++;
                    }
                }

                if (successRequestsCount >= _discoveryConfig.Concurrency)
                {
                    if (_logger.IsTrace) _logger.Trace($"Sent {successRequestsCount} successful requests, failedRequestCounter: {failRequestCount}, nodesTriedCounter: {nodesTriedCount}");
                    break;
                }
            }
        }
        int nodesCountAfterDiscovery = 0;
        foreach (NodeBucket bucket in _nodeTable.Buckets)
        {
            nodesCountAfterDiscovery += bucket.Count;
        }

        if (_logger.IsDebug) _logger.Debug($"Finished discovery cycle, tried contacting {alreadyTriedNodes.Count} nodes. All nodes count before the process: {nodesCountBeforeDiscovery}, after the process: {nodesCountAfterDiscovery}");

        if (_logger.IsTrace)
        {
            LogNodeTable();
        }

        alreadyTriedNodes.Clear();
        s_triedNodesCache = alreadyTriedNodes;
    }

    private IEnumerable<Task<Result>> SendFindNodes(
        byte[]? searchedNodeId,
        IEnumerable<Node?> nodesToSend,
        HashSet<Keccak> alreadyTriedNodes)
    {
        foreach (Node? node in nodesToSend)
        {
            if (node is null) continue;

            alreadyTriedNodes.Add(node!.IdHash);
            yield return SendFindNode(node, searchedNodeId);
        }
    }

    private int NodesCountBeforeDiscovery
    {
        get
        {
            int nodesCountBeforeDiscovery = 0;
            for (int index = 0; index < _nodeTable.Buckets.Length; index++)
            {
                NodeBucket x = _nodeTable.Buckets[index];
                nodesCountBeforeDiscovery += x.Count;
            }

            return nodesCountBeforeDiscovery;
        }
    }

    private void LogNodeTable()
    {
        StringBuilder sb = new();

        int length = 0;
        int bondedItemsCount = 0;

        foreach (NodeBucket nodeBucket in _nodeTable.Buckets)
        {
            if (nodeBucket.Count == 0) continue;

            length++;
            int itemsCount = nodeBucket.Count;
            bondedItemsCount += itemsCount;
            sb.AppendLine($"Bucket: {nodeBucket.Distance}, count: {itemsCount}");
            foreach (NodeBucketItem bucketItem in nodeBucket)
            {
                sb.AppendLine($"{bucketItem.Node}, LastContactTime: {bucketItem.LastContactTime:yyyy-MM-dd HH:mm:ss:000}");
            }
        }

        sb.Insert(0, $"------------------------------------------------------{Environment.NewLine}NodeTable, non-empty bucket count: {length}, total items count: {bondedItemsCount}");
        sb.AppendLine("------------------------------------------------------");
        _logger.Trace(sb.ToString());
    }

    private async Task<Result> SendFindNode(Node destinationNode, byte[]? searchedNodeId)
    {
        try
        {
            INodeLifecycleManager? nodeManager = _discoveryManager.GetNodeLifecycleManager(destinationNode);

            nodeManager?.SendFindNode(searchedNodeId ?? _masterNode!.Id.Bytes);

            return await _discoveryManager.WasMessageReceived(destinationNode.IdHash, MsgType.Neighbors, _discoveryConfig.SendNodeTimeout)
                ? Result.Success
                : Result.Fail($"Did not receive Neighbors response in time from: {destinationNode.Host}");
        }
        catch (OperationCanceledException)
        {
            return Result.Fail("Cancelled");
        }
    }
}
