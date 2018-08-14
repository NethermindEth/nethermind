using System.IO;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Db;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Stats;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class PeerStorageTests
    {
        private IPeerStorage _peerStorage;
        private INodeFactory _nodeFactory;
        private IConfigProvider _configurationProvider;

        [SetUp]
        public void Initialize()
        {
            var logManager = NullLogManager.Instance;
            _configurationProvider = new JsonConfigProvider();
            ((NetworkConfig)_configurationProvider.GetConfig<NetworkConfig>()).DbBasePath = Path.Combine(Path.GetTempPath(), "PeerStorageTests");

            var dbPath = Path.Combine(_configurationProvider.GetConfig<NetworkConfig>().DbBasePath, FullDbOnTheRocks.PeersDbPath);
            if (Directory.Exists(dbPath))
            {
                Directory.GetFiles(dbPath).ToList().ForEach(File.Delete);
            }

            _nodeFactory = new NodeFactory();
            _peerStorage = new PeerStorage(_configurationProvider, _nodeFactory, logManager, new PerfService(logManager));
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

            var peers = nodes.Select(x => new Peer(x, new NodeStats(x, _configurationProvider))).ToArray();

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