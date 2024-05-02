// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;
using static Nethermind.Network.Discovery.RoutingTable.NodeBucket;

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
            Buckets[i] = new NodeBucket(i, _discoveryConfig.BucketSize, _discoveryConfig.DropFullBucketNodeProbability);
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

    public ClosestNodesEnumerator GetClosestNodes()
    {
        return new ClosestNodesEnumerator(Buckets, _discoveryConfig.BucketSize);
    }

    public struct ClosestNodesEnumerator : IEnumerator<Node>, IEnumerable<Node>
    {
        private readonly NodeBucket[] _buckets;
        private readonly int _bucketSize;
        private BondedItemsEnumerator _itemEnumerator;
        private bool _enumeratorSet;
        private int _bucketIndex;
        private int _count;

        public ClosestNodesEnumerator(NodeBucket[] buckets, int bucketSize)
        {
            _buckets = buckets;
            _bucketSize = bucketSize;
            Current = null!;
            _bucketIndex = -1;
            _count = 0;
        }

        public Node Current { get; private set; }

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            try
            {
                while (_count < _bucketSize)
                {
                    if (!_enumeratorSet || !_itemEnumerator.MoveNext())
                    {
                        _itemEnumerator.Dispose();
                        _bucketIndex++;
                        if (_bucketIndex >= _buckets.Length)
                        {
                            return false;
                        }

                        _itemEnumerator = _buckets[_bucketIndex].BondedItems.GetEnumerator();
                        _enumeratorSet = true;
                        continue;
                    }

                    Current = _itemEnumerator.Current.Node!;
                    _count++;
                    return true;
                }
            }
            finally
            {
                _itemEnumerator.Dispose();
            }

            return false;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose() { }

        public ClosestNodesEnumerator GetEnumerator() => this;

        IEnumerator<Node> IEnumerable<Node>.GetEnumerator() => this;

        IEnumerator IEnumerable.GetEnumerator() => this;
    }

    public ClosestNodesFromNodeEnumerator GetClosestNodes(byte[] nodeId)
    {
        return GetClosestNodes(nodeId, _discoveryConfig.BucketSize);
    }

    public ClosestNodesFromNodeEnumerator GetClosestNodes(byte[] nodeId, int bucketSize)
    {
        CheckInitialization();
        return new ClosestNodesFromNodeEnumerator(Buckets, nodeId, _nodeDistanceCalculator, Math.Min(bucketSize, _discoveryConfig.BucketSize));
    }

    public struct ClosestNodesFromNodeEnumerator : IEnumerator<Node>, IEnumerable<Node>
    {
        private readonly List<Node> _sortedNodes;
        private int _currentIndex;

        public ClosestNodesFromNodeEnumerator(NodeBucket[] buckets, byte[] targetNodeId, INodeDistanceCalculator calculator, int bucketSize)
        {
            _sortedNodes = new List<Node>();
            Hash256 idHash = Keccak.Compute(targetNodeId);
            foreach (var bucket in buckets)
            {
                foreach (var item in bucket.BondedItems)
                {
                    if (item.Node != null && item.Node.IdHash != idHash)
                    {
                        _sortedNodes.Add(item.Node);
                    }
                }
            }

            _sortedNodes.Sort((a, b) => calculator.CalculateDistance(a.Id.Bytes, targetNodeId).CompareTo(calculator.CalculateDistance(b.Id.Bytes, targetNodeId)));
            if (_sortedNodes.Count > bucketSize)
            {
                CollectionsMarshal.SetCount(_sortedNodes, bucketSize);
            }

            _currentIndex = -1;
        }

        public readonly int Count => _sortedNodes.Count;

        public Node Current => _sortedNodes[_currentIndex];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_currentIndex + 1 < _sortedNodes.Count)
            {
                _currentIndex++;
                return true;
            }
            return false;
        }

        public void Reset() => throw new NotSupportedException();
        public void Dispose() { }

        public ClosestNodesFromNodeEnumerator GetEnumerator() => this;
        IEnumerator<Node> IEnumerable<Node>.GetEnumerator() => this;

        IEnumerator IEnumerable.GetEnumerator() => this;
    }

    public void Initialize(PublicKey masterNodeKey)
    {
        MasterNode = new Node(masterNodeKey, _networkConfig.ExternalIp, _networkConfig.DiscoveryPort);
        if (_logger.IsTrace) _logger.Trace($"Created MasterNode: {MasterNode}");
    }
}
