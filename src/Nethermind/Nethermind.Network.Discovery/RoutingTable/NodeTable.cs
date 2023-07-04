// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable;

public class NodeTable : INodeTable
{
    private readonly ILogger _logger;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly INodeDistanceCalculator _nodeDistanceCalculator;

    public NodeTable(
        INodeDistanceCalculator? nodeDistanceCalculator,
        IDiscoveryConfig? discoveryConfig,
        INetworkConfig? networkConfig,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _nodeDistanceCalculator = nodeDistanceCalculator ?? throw new ArgumentNullException(nameof(nodeDistanceCalculator));

        Buckets = new NodeBucket[_discoveryConfig.BucketsCount];
        for (int i = 0; i < Buckets.Length; i++)
        {
            Buckets[i] = new NodeBucket(i, _discoveryConfig.BucketSize);
        }
    }

    public Node? MasterNode { get; private set; }

    public NodeBucket[] Buckets { get; }

    public NodeAddResult AddNode(Node node)
    {
        CheckInitialization();

        if (_logger.IsTrace) _logger.Trace($"Adding node to NodeTable: {node}");
        int distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode!.IdHash.Bytes, node.IdHash.Bytes);
        NodeBucket bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
        return bucket.AddNode(node);
    }

    public void ReplaceNode(Node nodeToRemove, Node nodeToAdd)
    {
        CheckInitialization();

        int distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode!.IdHash.Bytes, nodeToAdd.IdHash.Bytes);
        NodeBucket bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
        bucket.ReplaceNode(nodeToRemove, nodeToAdd);
    }

    private void CheckInitialization()
    {
        if (MasterNode is null)
        {
            throw new InvalidOperationException("Master not has not been initialized");
        }
    }

    public void RefreshNode(Node node)
    {
        CheckInitialization();

        int distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode!.IdHash.Bytes, node.IdHash.Bytes);
        NodeBucket bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
        bucket.RefreshNode(node);
    }

    public IEnumerable<Node> GetClosestNodes()
    {
        int count = 0;
        int bucketSize = _discoveryConfig.BucketSize;

        foreach (NodeBucket nodeBucket in Buckets)
        {
            foreach (NodeBucketItem nodeBucketItem in nodeBucket.BondedItems)
            {
                if (count < bucketSize)
                {
                    count++;
                    if (nodeBucketItem.Node is not null)
                    {
                        yield return nodeBucketItem.Node;
                    }
                }
                else
                {
                    yield break;
                }
            }
        }
    }

    public IEnumerable<Node> GetClosestNodes(byte[] nodeId)
    {
        CheckInitialization();

        Keccak idHash = Keccak.Compute(nodeId);
        return Buckets.SelectMany(x => x.BondedItems)
            .Where(x => x.Node?.IdHash != idHash && x.Node is not null)
            .Select(x => new { x.Node, Distance = _nodeDistanceCalculator.CalculateDistance(x.Node!.Id.Bytes, nodeId) })
            .OrderBy(x => x.Distance)
            .Take(_discoveryConfig.BucketSize)
            .Select(x => x.Node!);
    }

    public void Initialize(PublicKey masterNodeKey)
    {
        MasterNode = new Node(masterNodeKey, _networkConfig.ExternalIp, _networkConfig.DiscoveryPort);
        if (_logger.IsTrace) _logger.Trace($"Created MasterNode: {MasterNode}");
    }
}
