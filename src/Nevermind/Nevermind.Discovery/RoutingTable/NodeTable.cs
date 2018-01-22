using System;
using System.Collections.Generic;
using System.Linq;
using Nevermind.Core;
using Nevermind.KeyStore;
using Nevermind.Utils.Model;

namespace Nevermind.Discovery.RoutingTable
{
    public class NodeTable : INodeTable
    {
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly INodeFactory _nodeFactory;
        private readonly IKeyStore _keyStore;
        private readonly ILogger _logger;
        private readonly INodeDistanceCalculator _nodeDistanceCalculator;

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
            var distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash, node.IdHash);
            var bucket = Buckets[distanceFromMaster];
            return bucket.AddNode(node);
        }

        public void DeleteNode(Node node)
        {
            var distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode.IdHash, node.IdHash);
            var bucket = Buckets[distanceFromMaster];
            bucket.RemoveNode(node);
        }

        public Node[] GetClosestNodes()
        {
            var nodes = new List<NodeBucketItem>();
            var bucketSize = _configurationProvider.BucketSize;
            foreach (var nodeBucket in Buckets)
            {
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
            Buckets = new NodeBucket[_configurationProvider.BucketSize];
            var key = _keyStore.GenerateKey("Test");
            if (key.Item2.ResultType == ResultType.Failure)
            {
                var msg = $"Cannot create key, error: {key.Item2.Error}";
                _logger.Error(msg);
                throw new Exception(msg);
            }

            MasterNode = _nodeFactory.CreateNode(key.Item1.PublicKey.PrefixedBytes, _configurationProvider.MasterHost, _configurationProvider.MasterPort);

            for (var i = 0; i < Buckets.Length; i++)
            {
                Buckets[i] = new NodeBucket(i, _configurationProvider.BucketSize);
            }
        }
    }
}