using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Db;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Store;

namespace Nethermind.Discovery
{
    public class PeerStorage : IPeerStorage
    {
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly INodeFactory _nodeFactory;
        private readonly IPerfService _perfService;
        private readonly IFullDb _db;
        private readonly ILogger _logger;
        private long _updateCounter = 0;
        private long _removeCounter = 0;

        public PeerStorage(IDiscoveryConfigurationProvider configurationProvider, INodeFactory nodeFactory, ILogger logger, IPerfService perfService)
        {
            _configurationProvider = configurationProvider;
            _nodeFactory = nodeFactory;
            _logger = logger;
            _perfService = perfService;
            _db = new FullDbOnTheRocks(Path.Combine(_configurationProvider.DbBasePath, FullDbOnTheRocks.PeersDbPath));
        }

        public (Node Node, long PersistedReputation)[] GetPersistedPeers()
        {
            return _db.Values.Select(GetNode).ToArray();
        }

        public void UpdatePeers(Peer[] peers)
        {
            for (var i = 0; i < peers.Length; i++)
            {
                var peer = peers[i];
                var node = peer.Node;
                var networkNode = new NetworkNode(node.Id.Bytes, node.Host, node.Port, node.Description, peer.NodeStats?.NewPersistedNodeReputation ?? 0);
                _db[networkNode.PublicKey.Bytes] = Rlp.Encode(networkNode).Bytes;
                _updateCounter++;
            }
        }

        public void RemovePeers(Peer[] nodes)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                _db.Remove(nodes[i].Node.Id.Bytes);
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
            var key = _perfService.StartPerfCalc();
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Commiting peers, updates: {_updateCounter}, removes: {_removeCounter}");
            }
            _db.CommitBatch();
            _perfService.EndPerfCalc(key, "PeerStorage commit");
        }

        public bool AnyPendingChange()
        {
            return _updateCounter > 0 || _removeCounter > 0;
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