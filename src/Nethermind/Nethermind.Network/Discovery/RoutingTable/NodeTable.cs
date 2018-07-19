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

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class NodeTable : INodeTable
    {
        private readonly INetworkConfig _configurationProvider;
        private readonly INodeFactory _nodeFactory;
        private readonly IKeyStore _keyStore;
        private readonly ILogger _logger;
        private readonly INodeDistanceCalculator _nodeDistanceCalculator;

        private readonly ConcurrentDictionary<string, Node> _nodes = new ConcurrentDictionary<string, Node>(); 

        public NodeTable(INodeFactory nodeFactory, IKeyStore keyStore, INodeDistanceCalculator nodeDistanceCalculator, IConfigProvider configurationProvider, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _configurationProvider = configurationProvider.GetConfig<NetworkConfig>();
            _nodeFactory = nodeFactory;
            _keyStore = keyStore;
            _nodeDistanceCalculator = nodeDistanceCalculator; 
        }

        public Node MasterNode { get; private set; }
        public NodeBucket[] Buckets { get; private set; }

        public NodeAddResult AddNode(Node node)
        {
            _logger.Info($"Adding node to NodeTable: {node}");
            var distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, node.IdHash.Bytes);
            var bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            _nodes.AddOrUpdate(node.IdHashText, node, (x, y) => y);
            return bucket.AddNode(node);
        }

        public void DeleteNode(Node node)
        {
            var distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, node.IdHash.Bytes);
            var bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            _nodes.TryRemove(node.IdHashText, out _);
            bucket.RemoveNode(node);
        }

        public void ReplaceNode(Node nodeToRemove, Node nodeToAdd)
        {
            var distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, nodeToAdd.IdHash.Bytes);
            var bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            _nodes.AddOrUpdate(nodeToAdd.IdHashText, nodeToAdd, (x, y) => y);
            _nodes.TryRemove(nodeToRemove.IdHashText, out _);
            bucket.ReplaceNode(nodeToRemove, nodeToAdd);
        }

        public void RefreshNode(Node node)
        {
            var distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash.Bytes, node.IdHash.Bytes);
            var bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
            bucket.RefreshNode(node);
        }

        public Node GetNode(byte[] nodeId)
        {
            var key = Keccak.Compute(nodeId).ToString();
            return _nodes.TryGetValue(key, out var node) ? node : null;
        }

        public Node[] GetClosestNodes()
        {
            var nodes = new List<NodeBucketItem>();
            var bucketSize = _configurationProvider.BucketSize;
            for (var i = 0; i < Buckets.Length; i++)
            {
                var nodeBucket = Buckets[i];
                var bucketItems = nodeBucket.Items;
                if (!bucketItems.Any())
                {
                    continue;
                }

                var availibleCount = bucketSize - nodes.Count;
                if (bucketItems.Count >= availibleCount)
                {
                    nodes.AddRange(bucketItems.Take(availibleCount).ToArray());
                    break;
                }

                nodes.AddRange(bucketItems.ToArray());
            }

            return nodes.Select(x => x.Node).ToArray();
        }

        public Node[] GetClosestNodes(byte[] nodeId)
        {
            var idHash = Keccak.Compute(nodeId);
            var idHashText = idHash.ToString();
            var allNodes = Buckets.SelectMany(x => x.Items).Where(x => x.Node.IdHashText != idHashText)
                .Select(x => new {x.Node, Distance = _nodeDistanceCalculator.CalculateDistance(x.Node.Id.PublicKey.Bytes, nodeId)})
                .OrderBy(x => x.Distance)
                .Take(_configurationProvider.BucketSize)
                .Select(x => x.Node).ToArray();
            return allNodes;
        }

        public void Initialize(NodeId masterNodeKey = null)
        {
            Buckets = new NodeBucket[_configurationProvider.BucketsCount];
            var pass = new SecureString();
            var rawPass = _configurationProvider.KeyPass;
            for (var i = 0; i < rawPass.Length; i++)
            {
                pass.AppendChar(rawPass[i]);
            }
            pass.MakeReadOnly();

            if (masterNodeKey == null)
            {
                var key = _keyStore.GenerateKey(pass);
                if (key.Item2.ResultType == ResultType.Failure)
                {
                    var msg = $"Cannot create key, error: {key.Item2.Error}";
                    _logger.Error(msg);
                    throw new Exception(msg);
                }

                masterNodeKey = new NodeId(key.PrivateKey.PublicKey);
            } 

            MasterNode = _nodeFactory.CreateNode(masterNodeKey, _configurationProvider.MasterHost, _configurationProvider.MasterPort);
            _logger.Info($"Created MasterNode: {MasterNode}");

            for (var i = 0; i < Buckets.Length; i++)
            {
                Buckets[i] = new NodeBucket(i, _configurationProvider.BucketSize);
            }
        }
    }
}