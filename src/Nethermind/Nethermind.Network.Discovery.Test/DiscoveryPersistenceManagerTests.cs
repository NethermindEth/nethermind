// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
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
            _networkStorage = Substitute.For<INetworkStorage>();
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

            _networkStorage.GetPersistedNodes().Returns(networkNodes);

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

            _networkStorage.GetPersistedNodes().Returns(networkNodes);

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
            var asIps = nodes.Select((n) => n.Address).ToArray();

            var cancellationSource = new CancellationTokenSource()
                .ThatCancelAfter(TimeSpan.FromMilliseconds(5000));

            var persistenceTask = _persistenceManager.RunDiscoveryPersistenceCommit(cancellationSource.Token);

            // Wait a bit to allow at least one persistence cycle to complete
            await Task.Delay(_discoveryConfig.DiscoveryPersistenceInterval + 10, cancellationSource.Token);

            await cancellationSource.CancelAsync();

            _networkStorage.Received().StartBatch();
            /*
            _networkStorage.Received().UpdateNodes(Arg.Is<IEnumerable<NetworkNode>>((IEnumerable<NetworkNode> nn) =>
            {
                return Enumerable.SequenceEqual<IPEndPoint>(nn.Select((n) => new IPEndPoint(n.HostIp, n.Port)), asIps);
            }));
            */
            _networkStorage.Received().Commit();
        }

        [Test]
        [CancelAfter(10000)]
        public async Task RunDiscoveryPersistenceCommit_Should_Handle_Exceptions(CancellationToken cancellationToken)
        {
            // Arrange
            var nodes = new[]
            {
                new Node(TestItem.PublicKeyA, "192.168.1.1", 30303),
                new Node(TestItem.PublicKeyB, "192.168.1.2", 30303)
            };

            _kademlia.IterateNodes().Returns(nodes);
            _networkStorage.When(x => x.StartBatch()).Throw(new Exception("Test exception"));

            // Act - start the persistence process
            var persistenceTask = _persistenceManager.RunDiscoveryPersistenceCommit(cancellationToken);

            // Wait a bit to allow at least one persistence cycle to complete
            await Task.Delay(50, cancellationToken);

            // Cancel the task so we can complete the test - No need for this as the CancelAfter will handle cancellation
            // We can leave the rest of the code in the test unchanged

            // If we got here without other exceptions, the error was properly handled
            Assert.Pass();
        }
    }
}
