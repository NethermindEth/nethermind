// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.Self)]
    public class DiscoveryPersistenceManagerTests
    {
        private MemDb _discoveryDb = null!;
        private INetworkStorage _networkStorage = null!;
        private INodeStatsManager _nodeStatsManager = null!;
        private IKademliaDiscv4Adapter _discv4Adapter = null!;
        private IDiscoveryConfig _discoveryConfig = null!;
        private ILogManager _logManager = null!;
        private IKademlia<PublicKey, Node> _kademlia = null!;
        private DiscoveryPersistenceManager _persistenceManager = null!;

        [SetUp]
        public void Setup()
        {
            NetworkNodeDecoder.Init();

            _discoveryDb = new MemDb();
            _networkStorage = new NetworkStorage(_discoveryDb, LimboLogs.Instance);
            _nodeStatsManager = Substitute.For<INodeStatsManager>();
            _discv4Adapter = Substitute.For<IKademliaDiscv4Adapter>();
            _discoveryConfig = new DiscoveryConfig()
            {
                DiscoveryPersistenceInterval = 100,
            };
            _logManager = LimboLogs.Instance;
            _kademlia = Substitute.For<IKademlia<PublicKey, Node>>();

            _persistenceManager = new DiscoveryPersistenceManager(
                _networkStorage,
                _nodeStatsManager,
                _discv4Adapter,
                _kademlia,
                _discoveryConfig,
                _logManager);
        }

        [TearDown]
        public async Task Teardown()
        {
            await _discv4Adapter.DisposeAsync();
            _discoveryDb.Dispose();
        }

        [Test]
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Ping_Each_Valid_Node(CancellationToken cancellationToken)
        {
            NetworkNode[] networkNodes =
            [
                new NetworkNode(TestItem.PublicKeyA, "192.168.1.1", 30303, 0),
                new NetworkNode(TestItem.PublicKeyB, "192.168.1.2", 30303, 0)
            ];

            _networkStorage.UpdateNodes(networkNodes);

            await _persistenceManager.LoadPersistedNodes(cancellationToken);

            await _discv4Adapter.Received(networkNodes.Length).Ping(
                Arg.Is<Node>(n => networkNodes.Any(nn => nn.NodeId.Equals(n.Id) && nn.Host == n.Host && nn.Port == n.Port)),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Handle_Ping_Exceptions(CancellationToken cancellationToken)
        {
            NetworkNode[] networkNodes =
            [
                new NetworkNode(TestItem.PublicKeyA, "192.168.1.1", 30303, 0),
                new NetworkNode(TestItem.PublicKeyB, "192.168.1.2", 30303, 0)
            ];

            _networkStorage.UpdateNodes(networkNodes);

            // First ping succeeds, second one throws
            _discv4Adapter.Ping(
                    Arg.Is<Node>(n => n.Id.Equals(networkNodes[0].NodeId)),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            _discv4Adapter.Ping(
                    Arg.Is<Node>(n => n.Id.Equals(networkNodes[1].NodeId)),
                    Arg.Any<CancellationToken>())
                .Returns(x => throw new Exception("Test exception"));

            await _persistenceManager.LoadPersistedNodes(cancellationToken);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Restore_Reputation(CancellationToken cancellationToken)
        {
            const int reputation = 123;
            NetworkNode networkNode = new(TestItem.PublicKeyA, "192.168.1.1", 30303, reputation);
            INodeStats nodeStats = Substitute.For<INodeStats>();

            _networkStorage.UpdateNodes([networkNode]);
            _nodeStatsManager.GetOrAdd(Arg.Any<Node>()).Returns(nodeStats);

            await _persistenceManager.LoadPersistedNodes(cancellationToken);

            _nodeStatsManager.Received(1).GetOrAdd(Arg.Is<Node>(n =>
                n.Id.Equals(networkNode.NodeId) &&
                n.Host == networkNode.Host &&
                n.Port == networkNode.Port));
            nodeStats.Received(1).CurrentPersistedNodeReputation = reputation;
        }

        [Test]
        public async Task RunDiscoveryPersistenceCommit_Should_Update_Nodes_In_Storage()
        {
            Node[] nodes =
            [
                new Node(TestItem.PublicKeyA, "192.168.1.1", 30303),
                new Node(TestItem.PublicKeyB, "192.168.1.2", 30303)
            ];

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

            _kademlia.IterateNodes().Returns(nodes);

            _ = _persistenceManager.RunDiscoveryPersistenceCommit(cts.Token);

            while (_discoveryDb.Count < nodes.Length)
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, cts.Token);
            }

            await cts.CancelAsync();

            Assert.That(_discoveryDb.Count, Is.EqualTo(nodes.Length));
        }
    }
}
