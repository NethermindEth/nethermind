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
using System.IO;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class NetworkStorageTests
    {
        [SetUp]
        public void SetUp()
        {
            NullLogManager logManager = NullLogManager.Instance;
            _configurationProvider = new JsonConfigProvider();
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            ((NetworkConfig) _configurationProvider.GetConfig<INetworkConfig>()).DbBasePath = _tempDir;

            _nodeFactory = new NodeFactory();
            _storage = new NetworkStorage("test", _configurationProvider, logManager, new PerfService(logManager));
        }
        
        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }

        private string _tempDir;
        private INetworkStorage _storage;
        private INodeFactory _nodeFactory;
        private IConfigProvider _configurationProvider;

        private INodeLifecycleManager CreateLifecycleManager(Node node)
        {
            INodeLifecycleManager manager = Substitute.For<INodeLifecycleManager>();
            manager.ManagedNode.Returns(node);
            manager.NodeStats.Returns(new NodeStats(node, _configurationProvider)
            {
                CurrentPersistedNodeReputation = node.Port
            });

            return manager;
        }

        [Test]
        public void Can_store_discovery_nodes()
        {
            var persistedNodes = _storage.GetPersistedNodes();
            Assert.AreEqual(0, persistedNodes.Length);

            var nodes = new[]
            {
                _nodeFactory.CreateNode("192.1.1.1", 3441),
                _nodeFactory.CreateNode("192.1.1.2", 3442),
                _nodeFactory.CreateNode("192.1.1.3", 3443),
                _nodeFactory.CreateNode("192.1.1.4", 3444),
                _nodeFactory.CreateNode("192.1.1.5", 3445)
            };
            nodes[0].Description = "Test desc";
            nodes[4].Description = "Test desc 2";

            var managers = nodes.Select(CreateLifecycleManager).ToArray();
            var networkNodes = managers.Select(x => new NetworkNode(x.ManagedNode.Id.PublicKey, x.ManagedNode.Host, x.ManagedNode.Port, x.ManagedNode.Description, x.NodeStats.NewPersistedNodeReputation)).ToArray();


            _storage.StartBatch();
            _storage.UpdateNodes(networkNodes);
            _storage.Commit();

            persistedNodes = _storage.GetPersistedNodes();
            foreach (INodeLifecycleManager manager in managers)
            {
                NetworkNode persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.ManagedNode.Id));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(manager.ManagedNode.Port, persistedNode.Port);
                Assert.AreEqual(manager.ManagedNode.Host, persistedNode.Host);
                Assert.AreEqual(manager.ManagedNode.Description, persistedNode.Description);
                Assert.AreEqual(manager.NodeStats.CurrentNodeReputation, persistedNode.Reputation);
            }

            _storage.StartBatch();
            _storage.RemoveNodes(new[] {networkNodes.First()});
            _storage.Commit();

            persistedNodes = _storage.GetPersistedNodes();
            foreach (INodeLifecycleManager manager in managers.Take(1))
            {
                NetworkNode persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.ManagedNode.Id));
                Assert.IsNull(persistedNode);
            }

            foreach (INodeLifecycleManager manager in managers.Skip(1))
            {
                NetworkNode persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.ManagedNode.Id));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(manager.ManagedNode.Port, persistedNode.Port);
                Assert.AreEqual(manager.ManagedNode.Host, persistedNode.Host);
                Assert.AreEqual(manager.ManagedNode.Description, persistedNode.Description);
                Assert.AreEqual(manager.NodeStats.CurrentNodeReputation, persistedNode.Reputation);
            }
        }

        [Test]
        public void Can_store_peers()
        {
            var persistedPeers = _storage.GetPersistedNodes();
            Assert.AreEqual(0, persistedPeers.Length);

            var nodes = new[]
            {
                _nodeFactory.CreateNode("192.1.1.1", 3441),
                _nodeFactory.CreateNode("192.1.1.2", 3442),
                _nodeFactory.CreateNode("192.1.1.3", 3443),
                _nodeFactory.CreateNode("192.1.1.4", 3444),
                _nodeFactory.CreateNode("192.1.1.5", 3445)
            };
            nodes[0].Description = "Test desc";
            nodes[4].Description = "Test desc 2";

            var peers = nodes.Select(x => new NetworkNode(x.Id.PublicKey, x.Host, x.Port, x.Description, 0L)).ToArray();

            _storage.StartBatch();
            _storage.UpdateNodes(peers);
            _storage.Commit();

            persistedPeers = _storage.GetPersistedNodes();
            foreach (NetworkNode peer in peers)
            {
                NetworkNode persistedNode = persistedPeers.FirstOrDefault(x => x.NodeId.Equals(peer.NodeId));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(peer.Port, persistedNode.Port);
                Assert.AreEqual(peer.Host, persistedNode.Host);
                Assert.AreEqual(peer.Description, persistedNode.Description);
                Assert.AreEqual(peer.Reputation, persistedNode.Reputation);
            }

            _storage.StartBatch();
            _storage.RemoveNodes(peers.Take(1).ToArray());
            _storage.Commit();

            persistedPeers = _storage.GetPersistedNodes();
            foreach (NetworkNode peer in peers.Take(1))
            {
                NetworkNode persistedNode = persistedPeers.FirstOrDefault(x => x.NodeId.Equals(peer.NodeId));
                Assert.IsNull(persistedNode);
            }

            foreach (NetworkNode peer in peers.Skip(1))
            {
                NetworkNode persistedNode = persistedPeers.FirstOrDefault(x => x.NodeId.Equals(peer.NodeId));
                Assert.IsNotNull(persistedNode);
                Assert.AreEqual(peer.Port, persistedNode.Port);
                Assert.AreEqual(peer.Host, persistedNode.Host);
                Assert.AreEqual(peer.Description, persistedNode.Description);
                Assert.AreEqual(peer.Reputation, persistedNode.Reputation);
            }
        }
    }
}