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

using System.IO;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Db;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [TestFixture]
    public class DiscoveryStorageTests
    {
        private IDiscoveryStorage _discoveryStorage;
        private INodeFactory _nodeFactory;
        private IConfigProvider _configurationProvider;

        [SetUp]
        public void Initialize()
        {
            var logManager = NullLogManager.Instance;
            _configurationProvider = new JsonConfigProvider();
            ((NetworkConfig)_configurationProvider.GetConfig<NetworkConfig>()).DbBasePath = Path.Combine(Path.GetTempPath(), "DiscoveryStorageTests");

            var dbPath = Path.Combine(_configurationProvider.GetConfig<NetworkConfig>().DbBasePath, FullDbOnTheRocks.DiscoveryNodesDbPath);
            if (Directory.Exists(dbPath))
            {
                Directory.GetFiles(dbPath).ToList().ForEach(File.Delete);
            }

            _nodeFactory = new NodeFactory();
            _discoveryStorage = new DiscoveryStorage("test", _configurationProvider, logManager, new PerfService(logManager));
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
            var networkNodes = managers.Select(x => new NetworkNode(x.ManagedNode.Id.PublicKey, x.ManagedNode.Host, x.ManagedNode.Port, x.ManagedNode.Description, x.NodeStats.NewPersistedNodeReputation)).ToArray();


            _discoveryStorage.StartBatch();
            _discoveryStorage.UpdateNodes(networkNodes);     
            _discoveryStorage.Commit();

            persistedNodes = _discoveryStorage.GetPersistedNodes();
            foreach (var manager in managers)
            {
                var persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.ManagedNode.Id));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(manager.ManagedNode.Port, persistedNode.Port);
                Assert.AreEqual(manager.ManagedNode.Host, persistedNode.Host);
                Assert.AreEqual(manager.ManagedNode.Description, persistedNode.Description);
                Assert.AreEqual(manager.NodeStats.CurrentNodeReputation, persistedNode.Reputation);
            }

            _discoveryStorage.StartBatch();
            _discoveryStorage.RemoveNodes(new[] { networkNodes.First() });
            _discoveryStorage.Commit();

            persistedNodes = _discoveryStorage.GetPersistedNodes();
            foreach (var manager in managers.Take(1))
            {
                var persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.ManagedNode.Id));
                Assert.IsNull(persistedNode);
            }

            foreach (var manager in managers.Skip(1))
            {
                var persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.ManagedNode.Id));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(manager.ManagedNode.Port, persistedNode.Port);
                Assert.AreEqual(manager.ManagedNode.Host, persistedNode.Host);
                Assert.AreEqual(manager.ManagedNode.Description, persistedNode.Description);
                Assert.AreEqual(manager.NodeStats.CurrentNodeReputation, persistedNode.Reputation);
            }
        }

        private INodeLifecycleManager CreateLifecycleManager(Node node)
        {
            var manager = Substitute.For<INodeLifecycleManager>();
            manager.ManagedNode.Returns(node);
            manager.NodeStats.Returns(new NodeStats(node, _configurationProvider)
            {
                CurrentPersistedNodeReputation = node.Port
            });
            
            return manager;
        }
    }
}