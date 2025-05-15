// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
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
            _discoveryConfig = Substitute.For<IDiscoveryConfig>();
            _logManager = LimboLogs.Instance;
            _kademlia = Substitute.For<IKademlia<PublicKey, Node>>();

            _discoveryConfig.DiscoveryPersistenceInterval.Returns(1000);

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

        /*
        [Test]
        public async Task AddPersistedNodes_Should_Ping_Each_Valid_Node()
        {
            // Arrange
            var networkNodes = new[]
            {
                new NetworkNode(TestItem.PublicKeyA, "192.168.1.1", 30303, 0),
                new NetworkNode(TestItem.PublicKeyB, "192.168.1.2", 30303, 0)
            };

            _networkStorage.GetPersistedNodes().Returns(networkNodes);

            // Act
            await _persistenceManager.AddPersistedNodes(CancellationToken.None);

            // Assert
            await _discv4Adapter.Received(networkNodes.Length).Ping(
                Arg.Is<Node>(n => networkNodes.Any(nn => nn.NodeId.Equals(n.Id) && nn.Host == n.Host && nn.Port == n.Port)),
                Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task AddPersistedNodes_Should_Skip_Invalid_Nodes()
        {
            // Arrange
            var validNode = new NetworkNode(TestItem.PublicKeyA, "192.168.1.1", 30303, 0);
            // An invalid node with null NodeId
            var invalidNode = new NetworkNode(null, "192.168.1.2", 30303, 0);

            var networkNodes = new[] { validNode, invalidNode };

            _networkStorage.GetPersistedNodes().Returns(networkNodes);

            // Act
            await _persistenceManager.AddPersistedNodes(CancellationToken.None);

            // Assert - only one ping should be attempted
            await _discv4Adapter.Received(1).Ping(
                Arg.Is<Node>(n => n.Id.Equals(validNode.NodeId) && n.Host == validNode.Host && n.Port == validNode.Port),
                Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task AddPersistedNodes_Should_Handle_Ping_Exceptions()
        {
            // Arrange
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

            // Act & Assert - should not throw
            await _persistenceManager.AddPersistedNodes(CancellationToken.None);
        }

        [Test]
        public async Task RunDiscoveryPersistenceCommit_Should_Update_Nodes_In_Storage()
        {
            // Arrange
            var nodes = new[]
            {
                new Node(TestItem.PublicKeyA, "192.168.1.1", 30303),
                new Node(TestItem.PublicKeyB, "192.168.1.2", 30303)
            };

            var cancellationSource = new CancellationTokenSource();

            _kademlia.IterateNodes().Returns(nodes.ToAsyncEnumerable());

            // Act - start the persistence process
            var persistenceTask = _persistenceManager.RunDiscoveryPersistenceCommit(cancellationSource.Token);

            // Wait a bit to allow at least one persistence cycle to complete
            await Task.Delay(50);

            // Cancel the task so we can complete the test
            cancellationSource.Cancel();

            try
            {
                await persistenceTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            // Assert
            _networkStorage.Received().StartBatch();
            _networkStorage.Received().UpdateNodes(Arg.Is<IEnumerable<NetworkNode>>(nn =>
                nn.Count() == nodes.Length &&
                nn.All(n => nodes.Any(node => node.Id.Equals(n.NodeId) && node.Host == n.Host && node.Port == n.Port))));
            _networkStorage.Received().Commit();
        }

        [Test]
        public async Task RunDiscoveryPersistenceCommit_Should_Handle_Exceptions()
        {
            // Arrange
            var nodes = new[]
            {
                new Node(TestItem.PublicKeyA, "192.168.1.1", 30303),
                new Node(TestItem.PublicKeyB, "192.168.1.2", 30303)
            };

            var cancellationSource = new CancellationTokenSource();

            _kademlia.IterateNodes().Returns(nodes.ToAsyncEnumerable());
            _networkStorage.When(x => x.StartBatch()).Throw(new Exception("Test exception"));

            // Act - start the persistence process
            var persistenceTask = _persistenceManager.RunDiscoveryPersistenceCommit(cancellationSource.Token);

            // Wait a bit to allow at least one persistence cycle to complete
            await Task.Delay(50);

            // Cancel the task so we can complete the test
            cancellationSource.Cancel();

            try
            {
                await persistenceTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            // If we got here without other exceptions, the error was properly handled
            Assert.Pass();
        }
        */
    }
}
