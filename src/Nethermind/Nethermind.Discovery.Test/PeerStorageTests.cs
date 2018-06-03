using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Discovery.Stats;
using Nethermind.Network;
using NUnit.Framework;

namespace Nethermind.Discovery.Test
{
    [TestFixture]
    public class PeerStorageTests
    {
        private IPeerStorage _peerStorage;
        private INodeFactory _nodeFactory;
        private IDiscoveryConfigurationProvider _configurationProvider;

        [SetUp]
        public void Initialize()
        {
            var logger = new SimpleConsoleLogger();
            _configurationProvider = new DiscoveryConfigurationProvider(new NetworkHelper(logger));
            _configurationProvider.DbBasePath = Path.Combine(Path.GetTempPath(), "PeerStorageTests");

            var dbPath = Path.Combine(_configurationProvider.DbBasePath, FullDbOnTheRocks.PeersDbPath);
            if (Directory.Exists(dbPath))
            {
                Directory.GetFiles(dbPath).ToList().ForEach(File.Delete);
            }

            _nodeFactory = new NodeFactory();
            _peerStorage = new PeerStorage(_configurationProvider, _nodeFactory, logger);
        }

        [Test]
        public void PeersReadWriteTest()
        {
            var persistedPeers = _peerStorage.GetPersistedPeers();
            Assert.AreEqual(0, persistedPeers.Length);

            var nodes = new[]
            {
                _nodeFactory.CreateNode("192.1.1.1", 3441),
                _nodeFactory.CreateNode("192.1.1.2", 3442),
                _nodeFactory.CreateNode("192.1.1.3", 3443),
                _nodeFactory.CreateNode("192.1.1.4", 3444),
                _nodeFactory.CreateNode("192.1.1.5", 3445),
            };
            nodes[0].Description = "Test desc";
            nodes[4].Description = "Test desc 2";

            var peers = nodes.Select(x => new Peer(x, new NodeStats(_configurationProvider))).ToArray();

            _peerStorage.StartBatch();
            _peerStorage.UpdatePeers(peers);
            _peerStorage.Commit();

            persistedPeers = _peerStorage.GetPersistedPeers();
            foreach (var peer in peers)
            {
                var persistedNode = persistedPeers.FirstOrDefault(x => x.Node.Id.Equals(peer.Node.Id));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(peer.Node.Port, persistedNode.Node.Port);
                Assert.AreEqual(peer.Node.Host, persistedNode.Node.Host);
                Assert.AreEqual(peer.Node.Description, persistedNode.Node.Description);
                Assert.AreEqual(peer.NodeStats.CurrentNodeReputation, persistedNode.PersistedReputation);
            }

            _peerStorage.StartBatch();
            _peerStorage.RemovePeers(peers.Take(1).ToArray());
            _peerStorage.Commit();

            persistedPeers = _peerStorage.GetPersistedPeers();
            foreach (var peer in peers.Take(1))
            {
                var persistedNode = persistedPeers.FirstOrDefault(x => x.Node.Id.Equals(peer.Node.Id));
                Assert.IsNull(persistedNode.Node);
            }

            foreach (var peer in peers.Skip(1))
            {
                var persistedNode = persistedPeers.FirstOrDefault(x => x.Node.Id.Equals(peer.Node.Id));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(peer.Node.Port, persistedNode.Node.Port);
                Assert.AreEqual(peer.Node.Host, persistedNode.Node.Host);
                Assert.AreEqual(peer.Node.Description, persistedNode.Node.Description);
                Assert.AreEqual(peer.NodeStats.CurrentNodeReputation, persistedNode.PersistedReputation);
            }
        }
    }
}