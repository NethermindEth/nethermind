// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
        public void Teardown()
        {
            _discv4Adapter?.DisposeAsync();
            _discoveryDb.Dispose();
        }

        [Test]
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Ping_Each_Valid_Node(CancellationToken cancellationToken)
        {
            var networkNodes = new[]
            {
                new NetworkNode(TestItem.PublicKeyA, "192.168.1.1", 30303, 0),
                new NetworkNode(TestItem.PublicKeyB, "192.168.1.2", 30303, 0)
            };

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
            var networkNodes = new[]
            {
                new NetworkNode(TestItem.PublicKeyA, "192.168.1.1", 30303, 0),
                new NetworkNode(TestItem.PublicKeyB, "192.168.1.2", 30303, 0)
            };

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
        public async Task RunDiscoveryPersistenceCommit_Should_Update_Nodes_In_Storage()
        {
            var nodes = new[]
            {
                new Node(TestItem.PublicKeyA, "192.168.1.1", 30303),
                new Node(TestItem.PublicKeyB, "192.168.1.2", 30303)
            };

            var cls = new CancellationTokenSource().ThatCancelAfter(TimeSpan.FromMilliseconds(5000));

            _kademlia.IterateNodes().Returns(nodes);

            _ = _persistenceManager.RunDiscoveryPersistenceCommit(cls.Token);

            // Wait a bit to allow at least one persistence cycle to complete
            await Task.Delay(_discoveryConfig.DiscoveryPersistenceInterval * 2, cls.Token);

            await cls.CancelAsync();

            _discoveryDb.Count.Should().Be(2);
        }
    }
}
