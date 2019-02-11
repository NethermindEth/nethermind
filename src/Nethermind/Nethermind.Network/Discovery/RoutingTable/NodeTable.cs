/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.KeyStore;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class NodeTable : INodeTable
    {
        private readonly INetworkConfig _networkConfig;
        private readonly IKeyStore _keyStore;
        private readonly ILogger _logger;
        private readonly INodeDistanceCalculator _nodeDistanceCalculator;

        private readonly ConcurrentDictionary<Keccak, Node> _nodes = new ConcurrentDictionary<Keccak, Node>(); 

        public NodeTable(IKeyStore keyStore, INodeDistanceCalculator nodeDistanceCalculator, INetworkConfig networkConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _networkConfig = networkConfig;
            _keyStore = keyStore;
            _nodeDistanceCalculator = nodeDistanceCalculator; 
        }

        public Node MasterNode { get; private set; }    
        public NodeBucket[] Buckets { get; private set; }

        public NodeAddResult AddNode(Node node)
        {
            if (_logger.IsTrace) _logger.Trace($"Adding node to NodeTable: {node}");
            var distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, node.IdHash.Bytes);
            var bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            _nodes.AddOrUpdate(node.IdHash, node, (x, y) => node);
            return bucket.AddNode(node);
        }

        public void ReplaceNode(Node nodeToRemove, Node nodeToAdd)
        {
            var distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, nodeToAdd.IdHash.Bytes);
            var bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            _nodes.AddOrUpdate(nodeToAdd.IdHash, nodeToAdd, (x, y) => nodeToAdd);
            _nodes.TryRemove(nodeToRemove.IdHash, out _);
            bucket.ReplaceNode(nodeToRemove, nodeToAdd);
        }

        public void RefreshNode(Node node)
        {
            var distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, node.IdHash.Bytes);
            var bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            bucket.RefreshNode(node);
        }

        public Node[] GetClosestNodes()
        {
            var nodes = new List<NodeBucketItem>();
            var bucketSize = _networkConfig.BucketSize;
            for (var i = 0; i < Buckets.Length; i++)
            {
                var nodeBucket = Buckets[i];
                var bucketItems = nodeBucket.Items;
                if (!bucketItems.Any())
                {
                    continue;
                }

                var availableCount = bucketSize - nodes.Count;
                if (bucketItems.Count >= availableCount)
                {
                    nodes.AddRange(bucketItems.Take(availableCount).ToArray());
                    break;
                }

                nodes.AddRange(bucketItems.ToArray());
            }

            return nodes.Select(x => x.Node).ToArray();
        }

        public Node[] GetClosestNodes(byte[] nodeId)
        {
            var idHash = Keccak.Compute(nodeId);
            var allNodes = Buckets.SelectMany(x => x.Items).Where(x => x.Node.IdHash != idHash)
                .Select(x => new {x.Node, Distance = _nodeDistanceCalculator.CalculateDistance(x.Node.Id.Bytes, nodeId)})
                .OrderBy(x => x.Distance)
                .Take(_networkConfig.BucketSize)
                .Select(x => x.Node).ToArray();
            return allNodes;
        }

        public void Initialize(PublicKey masterNodeKey)
        {
            Buckets = new NodeBucket[_networkConfig.BucketsCount];
            var pass = new SecureString();
            var rawPass = _networkConfig.KeyPass;
            for (var i = 0; i < rawPass.Length; i++)
            {
                pass.AppendChar(rawPass[i]);
            }
            
            pass.MakeReadOnly();

            MasterNode = new Node(masterNodeKey, _networkConfig.MasterHost, _networkConfig.MasterPort);
            if (_logger.IsTrace) _logger.Trace($"Created MasterNode: {MasterNode}");

            for (var i = 0; i < Buckets.Length; i++)
            {
                Buckets[i] = new NodeBucket(i, _networkConfig.BucketSize);
            }
        }
    }
}