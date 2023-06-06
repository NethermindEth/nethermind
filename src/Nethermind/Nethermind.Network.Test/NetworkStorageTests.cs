// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
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
            ConfigProvider configSource = new();
            _tempDir = TempPath.GetTempDirectory();

            var db = new SimpleFilePublicKeyDb("Test", _tempDir.Path, logManager);
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
            Assert.That(persistedNodes.Length, Is.EqualTo(0));

            var nodes = new[]
            {
                new Node(TestItem.PublicKeyA, "192.1.1.1", 3441),
                new Node(TestItem.PublicKeyB, "192.1.1.2", 3442),
                new Node(TestItem.PublicKeyC, "192.1.1.3", 3443),
                new Node(TestItem.PublicKeyD, "192.1.1.4", 3444),
                new Node(TestItem.PublicKeyE, "192.1.1.5", 3445)
            };

            var managers = nodes.Select(CreateLifecycleManager).ToArray();
            DateTime utcNow = DateTime.UtcNow;
            var networkNodes = managers.Select(x => new NetworkNode(x.ManagedNode.Id, x.ManagedNode.Host, x.ManagedNode.Port, x.NodeStats.NewPersistedNodeReputation(utcNow))).ToArray();


            _storage.StartBatch();
            _storage.UpdateNodes(networkNodes);
            _storage.Commit();

            persistedNodes = _storage.GetPersistedNodes();
            foreach (INodeLifecycleManager manager in managers)
            {
                NetworkNode persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.ManagedNode.Id));
                Assert.IsNotNull(persistedNode);
                Assert.That(persistedNode.Port, Is.EqualTo(manager.ManagedNode.Port));
                Assert.That(persistedNode.Host, Is.EqualTo(manager.ManagedNode.Host));
                Assert.That(persistedNode.Reputation, Is.EqualTo(manager.NodeStats.CurrentNodeReputation()));
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

            utcNow = DateTime.UtcNow;
            foreach (INodeLifecycleManager manager in managers.Skip(1))
            {
                NetworkNode persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.ManagedNode.Id));
                Assert.IsNotNull(persistedNode);
                Assert.That(persistedNode.Port, Is.EqualTo(manager.ManagedNode.Port));
                Assert.That(persistedNode.Host, Is.EqualTo(manager.ManagedNode.Host));
                Assert.That(persistedNode.Reputation, Is.EqualTo(manager.NodeStats.CurrentNodeReputation(utcNow)));
            }
        }

        [Test]
        public void Can_store_peers()
        {
            var persistedPeers = _storage.GetPersistedNodes();
            Assert.That(persistedPeers.Length, Is.EqualTo(0));

            var nodes = new[]
            {
                new Node(TestItem.PublicKeyA, "192.1.1.1", 3441),
                new Node(TestItem.PublicKeyB, "192.1.1.2", 3442),
                new Node(TestItem.PublicKeyC, "192.1.1.3", 3443),
                new Node(TestItem.PublicKeyD, "192.1.1.4", 3444),
                new Node(TestItem.PublicKeyE, "192.1.1.5", 3445)
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
                Assert.That(persistedNode.Port, Is.EqualTo(peer.Port));
                Assert.That(persistedNode.Host, Is.EqualTo(peer.Host));
                Assert.That(persistedNode.Reputation, Is.EqualTo(peer.Reputation));
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
                Assert.That(persistedNode.Port, Is.EqualTo(peer.Port));
                Assert.That(persistedNode.Host, Is.EqualTo(peer.Host));
                Assert.That(persistedNode.Reputation, Is.EqualTo(peer.Reputation));
            }
        }
    }
}
