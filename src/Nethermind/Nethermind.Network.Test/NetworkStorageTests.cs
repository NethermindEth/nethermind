//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NetworkStorageTests
    {
        [SetUp]
        public void SetUp()
        {
            NetworkNodeDecoder.Init();
            ILogManager logManager = LimboLogs.Instance;
            ConfigProvider configSource = new ConfigProvider();
            _tempDir = TempPath.GetTempDirectory();

            var db = new SimpleFilePublicKeyDb("Test",_tempDir.Path, logManager);
            _storage = new NetworkStorage(db, logManager);
        }
        
        [TearDown]
        public void TearDown()
        {
            _tempDir.Dispose();
        }

        private TempPath _tempDir;
        private INetworkStorage _storage;

        private INodeLifecycleManager CreateLifecycleManager(Node node)
        {
            INodeLifecycleManager manager = Substitute.For<INodeLifecycleManager>();
            manager.ManagedNode.Returns(node);
            manager.NodeStats.Returns(new NodeStatsLight(node)
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
                new Node("192.1.1.1", 3441),
                new Node("192.1.1.2", 3442),
                new Node("192.1.1.3", 3443),
                new Node("192.1.1.4", 3444),
                new Node("192.1.1.5", 3445)
            };

            var managers = nodes.Select(CreateLifecycleManager).ToArray();
            var networkNodes = managers.Select(x => new NetworkNode(x.ManagedNode.Id, x.ManagedNode.Host, x.ManagedNode.Port, x.NodeStats.NewPersistedNodeReputation)).ToArray();


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
                Assert.AreEqual(manager.NodeStats.CurrentNodeReputation, persistedNode.Reputation);
            }

            _storage.StartBatch();
            _storage.RemoveNode(networkNodes.First().NodeId);
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
                new Node("192.1.1.1", 3441),
                new Node("192.1.1.2", 3442),
                new Node("192.1.1.3", 3443),
                new Node("192.1.1.4", 3444),
                new Node("192.1.1.5", 3445)
            };

            var peers = nodes.Select(x => new NetworkNode(x.Id, x.Host, x.Port, 0L)).ToArray();

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
                Assert.AreEqual(peer.Reputation, persistedNode.Reputation);
            }

            _storage.StartBatch();
            _storage.RemoveNode(peers.First().NodeId);
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
                Assert.AreEqual(peer.Reputation, persistedNode.Reputation);
            }
        }
    }
}
