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
        private readonly IFullDb _discoveryDb;

        public DiscoveryStorage(IDiscoveryConfigurationProvider configurationProvider, INodeFactory nodeFactory)
        {
            _configurationProvider = configurationProvider;
            _nodeFactory = nodeFactory;
            _discoveryDb = new FullDbOnTheRocks(Path.Combine(_configurationProvider.DbBasePath, DbOnTheRocks.PeersDbPath));
        }

        public (Node Node, long PersistedReputation)[] GetPersistedNodes()
        {
            return _discoveryDb.Values.Select(GetNode).ToArray();
        }

        public async Task PersistNodesAsync(INodeLifecycleManager[] nodes)
        {
            await Task.Run(() => PersistNodes(nodes));
        }

        public void PersistNodes(INodeLifecycleManager[] nodes)
        {
            var existingKeys = _discoveryDb.Keys;

            //add / update valid nodes
            for (var i = 0; i < nodes.Length; i++)
            {
                var manager = nodes[i];
                var node = manager.ManagedNode;
                var networkNode = new NetworkNode(node.Id.Bytes, node.Host, node.Port, node.Description, manager.NodeStats.NewPersistedNodeReputation);
                _discoveryDb[networkNode.PublicKey.Bytes] = Rlp.Encode(networkNode).Bytes;
            }

            //delete removed nodes
            var nodesToRemove = existingKeys.Where(x => nodes.All(y => !Bytes.UnsafeCompare(x, y.ManagedNode.Id.Bytes))).ToArray();
            if (!nodesToRemove.Any())
            {
                return;
            }

            for (var i = 0; i < nodesToRemove.Length; i++)
            {
                _discoveryDb.Remove(nodesToRemove[i]);
            }
        }

        private (Node, long) GetNode(byte[] networkNodeRaw)
        {
            var persistedNode = Rlp.Decode<NetworkNode>(networkNodeRaw);
            var node = _nodeFactory.CreateNode(persistedNode.PublicKey, persistedNode.Host, persistedNode.Port);
            node.Description = persistedNode.Description;
            return (node, persistedNode.Reputation);
        }
    }
}