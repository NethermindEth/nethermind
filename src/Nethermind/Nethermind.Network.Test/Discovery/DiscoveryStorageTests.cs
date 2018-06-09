using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Discovery.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [TestFixture]
    public class DiscoveryStorageTests
    {
        private IDiscoveryStorage _discoveryStorage;
        private INodeFactory _nodeFactory;
        private IDiscoveryConfigurationProvider _configurationProvider;

        [SetUp]
        public void Initialize()
        {
            var logger = new SimpleConsoleLogger();
            _configurationProvider = new DiscoveryConfigurationProvider(new NetworkHelper(logger));
            _configurationProvider.DbBasePath = Path.Combine(Path.GetTempPath(), "DiscoveryStorageTests");

            var dbPath = Path.Combine(_configurationProvider.DbBasePath, FullDbOnTheRocks.DiscoveryNodesDbPath);
            if (Directory.Exists(dbPath))
            {
                Directory.GetFiles(dbPath).ToList().ForEach(File.Delete);
            }

            _nodeFactory = new NodeFactory();
            _discoveryStorage = new DiscoveryStorage(_configurationProvider, _nodeFactory, logger, new PerfService(logger));
        }

        [Test]
        public void NodesReadWriteTest()
        {
            var persistedNodes = _discoveryStorage.GetPersistedNodes();
            Assert.AreEqual(0, persistedNodes.Length);

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

            var managers = nodes.Select(CreateLifecycleManager).ToArray();
            
            _discoveryStorage.StartBatch();
            _discoveryStorage.UpdateNodes(managers);     
            _discoveryStorage.Commit();

            persistedNodes = _discoveryStorage.GetPersistedNodes();
            foreach (var manager in managers)
            {
                var persistedNode = persistedNodes.FirstOrDefault(x => x.Node.Id.Equals(manager.ManagedNode.Id));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(manager.ManagedNode.Port, persistedNode.Node.Port);
                Assert.AreEqual(manager.ManagedNode.Host, persistedNode.Node.Host);
                Assert.AreEqual(manager.ManagedNode.Description, persistedNode.Node.Description);
                Assert.AreEqual(manager.NodeStats.CurrentNodeReputation, persistedNode.PersistedReputation);
            }

            _discoveryStorage.StartBatch();
            _discoveryStorage.RemoveNodes(new[] { managers.First() });
            _discoveryStorage.Commit();

            persistedNodes = _discoveryStorage.GetPersistedNodes();
            foreach (var manager in managers.Take(1))
            {
                var persistedNode = persistedNodes.FirstOrDefault(x => x.Node.Id.Equals(manager.ManagedNode.Id));
                Assert.IsNull(persistedNode.Node);
            }

            foreach (var manager in managers.Skip(1))
            {
                var persistedNode = persistedNodes.FirstOrDefault(x => x.Node.Id.Equals(manager.ManagedNode.Id));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(manager.ManagedNode.Port, persistedNode.Node.Port);
                Assert.AreEqual(manager.ManagedNode.Host, persistedNode.Node.Host);
                Assert.AreEqual(manager.ManagedNode.Description, persistedNode.Node.Description);
                Assert.AreEqual(manager.NodeStats.CurrentNodeReputation, persistedNode.PersistedReputation);
            }
        }

        private INodeLifecycleManager CreateLifecycleManager(Node node)
        {
            var manager = Substitute.For<INodeLifecycleManager>();
            manager.ManagedNode.Returns(node);
            manager.NodeStats.Returns(new NodeStats(_configurationProvider)
            {
                CurrentPersistedNodeReputation = node.Port
            });
            return manager;
        }
    }
}