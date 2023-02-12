// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

    public int FillClosestNodes(Node[] nodes, HashSet<Keccak>? filter = null)
    {
        if (nodes.Length == 0) return 0;

        int count = 0;
        foreach (NodeBucket nodeBucket in Buckets)
        {
            // Fast count check, don't create enumerator if nodeBucket is empty
            if (nodeBucket.Count == 0) continue;

            foreach (NodeBucketItem nodeBucketItem in nodeBucket)
            {
                if (nodeBucketItem.Node is Node node &&
                    !(filter?.Contains(node.IdHash) ?? false))
                {
                    nodes[count] = node;
                    count++;

                    // Complete when nodes is full
                    if (count >= nodes.Length) return count;
                }
            }
        }

        return count;
    }

    public IEnumerable<Node> GetClosestNodes(HashSet<Keccak>? filter = null)
    {
        int maxSize = _discoveryConfig.BucketSize;
        Node[] nodes = new Node[maxSize];
        int count = FillClosestNodes(nodes, filter);
        if (count < maxSize)
        {
            Array.Resize(ref nodes, count);
        }
        return nodes;
    }

    [ThreadStatic]
    private static DistanceComparer? s_distanceComparerCache;
    private static DistanceComparer NodeDistanceComparer => s_distanceComparerCache ??= new DistanceComparer();

    public int FillClosestNodes(byte[] nodeId, Node[] nodes, HashSet<Keccak>? filter = null)
    {
        if (nodes.Length == 0) return 0;

        CheckInitialization();

        ValueKeccak idHash = ValueKeccak.Compute(nodeId);
        DistanceComparer comparer = NodeDistanceComparer;
        comparer.Initialize(nodeId, _nodeDistanceCalculator);

        int count = 0;
        foreach (NodeBucket nodeBucket in Buckets)
        {
            // Fast count check, don't create enumerator if nodeBucket is empty
            if (nodeBucket.Count == 0) continue;

            foreach (NodeBucketItem nodeBucketItem in nodeBucket)
            {
                if (nodeBucketItem.Node is not Node node ||
                    node.IdHash == idHash ||
                    (filter?.Contains(node.IdHash) ?? false))
                {
                    // Null node, same node or filtered node; skip.
                    continue;
                }

                if (count + 1 == nodes.Length)
                {
                    nodes[count] = node;
                    count++;
                    // Just filled, sort the array
                    Array.Sort<Node>(nodes, comparer);
                }
                else if (count == nodes.Length)
                {
                    // Full array, so evict furthest if closer
                    ref Node last = ref nodes[count - 1];
                    int distance = _nodeDistanceCalculator.CalculateDistance(node.Id.Bytes, nodeId);
                    if (comparer.Compare(last, distance) >= 0)
                    {
                        // Not closer than furthest; skip.
                        continue;
                    }

                    // Drop last element
                    last = null!;
                    // Insert new one at correct sort index
                    MoveUp(node, distance, count - 1);
                }
                else
                {
                    // Not full yet, just add to next slot
                    nodes[count] = node;
                    count++;
                }
            }
        }

        comparer.Clear();
        return count;

        void MoveUp(Node node, int distance, int nodeIndex)
        {
            // Instead of swapping items all the way to the root, we will perform
            // a similar optimization as in insertion sort.
            while (nodeIndex > 0)
            {
                int parentIndex = GetParentIndex(nodeIndex);
                Node parent = nodes[parentIndex];

                if (comparer.Compare(parent, distance) > 0)
                {
                    nodes[nodeIndex] = parent;
                    nodeIndex = parentIndex;
                }
                else
                {
                    break;
                }
            }

            nodes[nodeIndex] = node;
        }

        static int GetParentIndex(int index)
        {
            const int log2Arity = 2;
            return (index - 1) >> log2Arity;
        }
    }

    public IEnumerable<Node> GetClosestNodes(byte[] nodeId, HashSet<Keccak>? filter = null)
    {
        int maxSize = _discoveryConfig.BucketSize;
        Node[] nodes = new Node[maxSize];
        int count = FillClosestNodes(nodeId, nodes, filter);
        if (count < maxSize)
        {
            Array.Resize(ref nodes, count);
        }
        return nodes;
    }

    public void Initialize(PublicKey masterNodeKey)
    {
        MasterNode = new Node(masterNodeKey, _networkConfig.ExternalIp, _networkConfig.DiscoveryPort);
        if (_logger.IsTrace) _logger.Trace($"Created MasterNode: {MasterNode}");
    }

    private class DistanceComparer : IComparer<Node>
    {
        byte[]? _nodeId;
        INodeDistanceCalculator? _nodeDistanceCalculator;

        public void Initialize(byte[] nodeId, INodeDistanceCalculator nodeDistanceCalculator)
        {
            _nodeId = nodeId;
            _nodeDistanceCalculator = nodeDistanceCalculator ?? throw new ArgumentNullException(nameof(nodeDistanceCalculator));
        }

        public void Clear()
        {
            _nodeId = null;
            _nodeDistanceCalculator = null;
        }

        public int Compare(Node? x, Node? y)
        {
            if (x is null)
            {
                return y is null ? 0 : -1;
            }

            if (y is null) return 1;

            int dx = _nodeDistanceCalculator!.CalculateDistance(x!.Id.Bytes, _nodeId!);
            int dy = _nodeDistanceCalculator.CalculateDistance(y!.Id.Bytes, _nodeId!);

            return dx.CompareTo(dy);
        }

        public int Compare(Node? x, int distance)
        {
            if (x is null)
            {
                return -1;
            }

            int dx = _nodeDistanceCalculator!.CalculateDistance(x!.Id.Bytes, _nodeId!);

            return dx.CompareTo(distance);
        }
    }
}
