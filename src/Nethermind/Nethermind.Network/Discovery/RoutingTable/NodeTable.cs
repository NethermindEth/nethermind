//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class NodeTable : INodeTable
    {
        private ILogger _logger;
        private INetworkConfig _networkConfig;
        private IDiscoveryConfig _discoveryConfig;
        private INodeDistanceCalculator _nodeDistanceCalculator;
        
        private ConcurrentDictionary<Keccak, Node> _nodes = new ConcurrentDictionary<Keccak, Node>(); 

        public NodeTable(INodeDistanceCalculator nodeDistanceCalculator, IDiscoveryConfig discoveryConfig, INetworkConfig networkConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
            
            _nodeDistanceCalculator = nodeDistanceCalculator ?? throw new ArgumentNullException(nameof(nodeDistanceCalculator)); 
        }

        public Node MasterNode { get; private set; }    
        
        public NodeBucket[] Buckets { get; private set; }

        public NodeAddResult AddNode(Node node)
        {
            if (_logger.IsTrace) _logger.Trace($"Adding node to NodeTable: {node}");
            int distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, node.IdHash.Bytes);
            NodeBucket bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            _nodes.AddOrUpdate(node.IdHash, node, (x, y) => node);
            return bucket.AddNode(node);
        }

        public void ReplaceNode(Node nodeToRemove, Node nodeToAdd)
        {
            int distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, nodeToAdd.IdHash.Bytes);
            NodeBucket bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            _nodes.AddOrUpdate(nodeToAdd.IdHash, nodeToAdd, (x, y) => nodeToAdd);
            _nodes.TryRemove(nodeToRemove.IdHash, out _);
            bucket.ReplaceNode(nodeToRemove, nodeToAdd);
        }

        public void RefreshNode(Node node)
        {
            int distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, node.IdHash.Bytes);
            NodeBucket bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            bucket.RefreshNode(node);
        }

        public IEnumerable<Node> GetClosestNodes()
        {
            int count = 0;
            int bucketSize = _discoveryConfig.BucketSize;
            
            foreach (NodeBucket nodeBucket in Buckets)
            {
                foreach (NodeBucketItem node in nodeBucket.BondedItems)
                {
                    if (count < bucketSize)
                    {
                        count++;
                        yield return node.Node;
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
            Keccak idHash = Keccak.Compute(nodeId);
            return Buckets.SelectMany(x => x.BondedItems)
                .Where(x => x.Node.IdHash != idHash)
                .Select(x => new {x.Node, Distance = _nodeDistanceCalculator.CalculateDistance(x.Node.Id.Bytes, nodeId)})
                .OrderBy(x => x.Distance)
                .Take(_discoveryConfig.BucketSize)
                .Select(x => x.Node);
        }

        public void Initialize(PublicKey masterNodeKey)
        {
            Buckets = new NodeBucket[_discoveryConfig.BucketsCount];
            MasterNode = new Node(masterNodeKey, _networkConfig.ExternalIp, _networkConfig.DiscoveryPort);
            if (_logger.IsTrace) _logger.Trace($"Created MasterNode: {MasterNode}");

            for (int i = 0; i < Buckets.Length; i++)
            {
                Buckets[i] = new NodeBucket(i, _discoveryConfig.BucketSize);
            }
        }
    }
}
