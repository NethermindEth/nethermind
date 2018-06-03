using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Store;

namespace Nethermind.Discovery
{
    public class DiscoveryStorage : IDiscoveryStorage
    {
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly INodeFactory _nodeFactory;
        private readonly IFullDb _db;
        private readonly ILogger _logger;
        private long _updateCounter = 0;
        private long _removeCounter = 0;

        public DiscoveryStorage(IDiscoveryConfigurationProvider configurationProvider, INodeFactory nodeFactory, ILogger logger)
        {
            _configurationProvider = configurationProvider;
            _nodeFactory = nodeFactory;
            _logger = logger;
            _db = new FullDbOnTheRocks(Path.Combine(_configurationProvider.DbBasePath, FullDbOnTheRocks.DiscoveryNodesDbPath));
        }

        public (Node Node, long PersistedReputation)[] GetPersistedNodes()
        {
            return _db.Values.Select(GetNode).ToArray();
        }

        public void UpdateNodes(INodeLifecycleManager[] nodes)
        {
            //add / update valid nodes
            for (var i = 0; i < nodes.Length; i++)
            {
                var manager = nodes[i];
                var node = manager.ManagedNode;
                var networkNode = new NetworkNode(node.Id.Bytes, node.Host, node.Port, node.Description, manager.NodeStats.NewPersistedNodeReputation);
                _db[networkNode.PublicKey.Bytes] = Rlp.Encode(networkNode).Bytes;
                _updateCounter++;
            }
        }

        public void RemoveNodes(INodeLifecycleManager[] nodes)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                _db.Remove(nodes[i].ManagedNode.Id.Bytes);
                _removeCounter++;
            }
        }

        public void StartBatch()
        {
            _db.StartBatch();
            _updateCounter = 0;
            _removeCounter = 0;
        }

        public void Commit()
        {
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Commiting discovery nodes, updates: {_updateCounter}, removes: {_removeCounter}");
            }
            _db.CommitBatch();
        }

        //public async Task PersistNodesAsync(INodeLifecycleManager[] nodes)
        //{
        //    await Task.Run(() => PersistNodes(nodes));
        //}

        //public void PersistNodes(INodeLifecycleManager[] nodes)
        //{
        //    var existingKeys = _db.Keys;

        //    //add / update valid nodes
        //    for (var i = 0; i < nodes.Length; i++)
        //    {
        //        var manager = nodes[i];
        //        var node = manager.ManagedNode;
        //        var networkNode = new NetworkNode(node.Id.Bytes, node.Host, node.Port, node.Description, manager.NodeStats.NewPersistedNodeReputation);
        //        _db[networkNode.PublicKey.Bytes] = Rlp.Encode(networkNode).Bytes;
        //    }

        //    //delete removed nodes
        //    var nodesToRemove = existingKeys.Where(x => nodes.All(y => !Bytes.UnsafeCompare(x, y.ManagedNode.Id.Bytes))).ToArray();

        //    if (_logger.IsInfoEnabled)
        //    {
        //        _logger.Info($"Updated notes: {nodes.Length}\n{string.Join('\n', nodes.Select(x => x.ManagedNode.ToString()))}");
        //        _logger.Info($"Removed notes: {nodesToRemove.Length}");
        //    }

        //    if (!nodesToRemove.Any())
        //    {
        //        return;
        //    }

        //    for (var i = 0; i < nodesToRemove.Length; i++)
        //    {
        //        _db.Remove(nodesToRemove[i]);
        //    }
        //}

        private (Node, long) GetNode(byte[] networkNodeRaw)
        {
            var persistedNode = Rlp.Decode<NetworkNode>(networkNodeRaw);
            var node = _nodeFactory.CreateNode(persistedNode.PublicKey, persistedNode.Host, persistedNode.Port);
            node.Description = persistedNode.Description;
            return (node, persistedNode.Reputation);
        }
    }
}