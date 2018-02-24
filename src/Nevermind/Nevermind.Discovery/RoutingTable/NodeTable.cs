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
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Model;
using Nevermind.KeyStore;

namespace Nevermind.Discovery.RoutingTable
{
    public class NodeTable : INodeTable
    {
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly INodeFactory _nodeFactory;
        private readonly IKeyStore _keyStore;
        private readonly ILogger _logger;
        private readonly INodeDistanceCalculator _nodeDistanceCalculator;

        private readonly ConcurrentDictionary<string, Node> _nodes = new ConcurrentDictionary<string, Node>(); 

        public NodeTable(IDiscoveryConfigurationProvider configurationProvider, INodeFactory nodeFactory, IKeyStore keyStore, ILogger logger, INodeDistanceCalculator nodeDistanceCalculator)
        {
            _configurationProvider = configurationProvider;
            _nodeFactory = nodeFactory;
            _keyStore = keyStore;
            _logger = logger;
            _nodeDistanceCalculator = nodeDistanceCalculator;

            Initialize();   
        }

        public Node MasterNode { get; private set; }
        public NodeBucket[] Buckets { get; private set; }

        public NodeAddResult AddNode(Node node)
        {
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
                if (!nodeBucket.Items.Any())
                {
                    continue;
                }

                var availibleCount = bucketSize - nodes.Count;
                if (nodeBucket.Items.Count >= availibleCount)
                {
                    nodes.AddRange(nodeBucket.Items.Take(availibleCount).ToArray());
                    break;
                }

                nodes.AddRange(nodeBucket.Items.ToArray());
            }

            return nodes.Select(x => x.Node).ToArray();
        }

        private void Initialize()
        {
            Buckets = new NodeBucket[_configurationProvider.BucketsCount];
            var pass = new SecureString();
            var rawPass = _configurationProvider.KeyPass;
            for (var i = 0; i < rawPass.Length; i++)
            {
                pass.AppendChar(rawPass[i]);
            }
            pass.MakeReadOnly();

            var key = _keyStore.GenerateKey(pass);
            if (key.Item2.ResultType == ResultType.Failure)
            {
                var msg = $"Cannot create key, error: {key.Item2.Error}";
                _logger.Error(msg);
                throw new Exception(msg);
            }

            MasterNode = _nodeFactory.CreateNode(key.Item1.PublicKey, _configurationProvider.MasterHost, _configurationProvider.MasterPort);

            for (var i = 0; i < Buckets.Length; i++)
            {
                Buckets[i] = new NodeBucket(i, _configurationProvider.BucketSize);
            }
        }
    }
}