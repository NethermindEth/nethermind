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
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.Self)]
    public class DiscoveryPersistenceManagerTests
    {
        private static readonly NetworkNode NodeA = new(TestItem.PublicKeyA, "192.168.1.1", 30303, 0);
        private static readonly NetworkNode NodeB = new(TestItem.PublicKeyB, "192.168.1.2", 30303, 0);

        private MemDb _discoveryDb = null!;
        private INetworkStorage _networkStorage = null!;
        private INodeStatsManager _nodeStatsManager = null!;
        private IKademliaAdapter _discv4Adapter = null!;
        private IDiscoveryConfig _discoveryConfig = null!;
        private ILogManager _logManager = null!;
        private IKademlia<PublicKey, Node> _kademlia = null!;
        private DiscoveryPersistenceManager _persistenceManager = null!;

        [SetUp]
        public void Setup()
        {
            _discoveryDb = new MemDb();
            _networkStorage = new NetworkStorage(_discoveryDb, LimboLogs.Instance);
            _nodeStatsManager = Substitute.For<INodeStatsManager>();
            _discv4Adapter = Substitute.For<IKademliaAdapter>();
            _discoveryConfig = new DiscoveryConfig()
            {
                DiscoveryPersistenceInterval = 100,
            };
            _logManager = LimboLogs.Instance;
            _kademlia = Substitute.For<IKademlia<PublicKey, Node>>();

            _persistenceManager = CreateManager(_networkStorage);
        }

        [TearDown]
        public async Task Teardown()
        {
            await _discv4Adapter.DisposeAsync();
            _discoveryDb.Dispose();
        }

        private DiscoveryPersistenceManager CreateManager(INetworkStorage storage) => new(
            storage,
            _nodeStatsManager,
            _discv4Adapter,
            _kademlia,
            _discoveryConfig,
            _logManager);

        private static Task PingReceived(IKademliaAdapter adapter, NetworkNode node, int times = 1) =>
            adapter.Received(times).Ping(
                Arg.Is<Node>(n => n.Id.Equals(node.NodeId)),
                Arg.Any<CancellationToken>());

        [Test]
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Ping_Each_Valid_Node(CancellationToken cancellationToken)
        {
            NetworkNode[] nodes = [NodeA, NodeB];
            _networkStorage.UpdateNodes(nodes);

            await _persistenceManager.LoadPersistedNodes(cancellationToken);

            await _discv4Adapter.Received(nodes.Length).Ping(
                Arg.Is<Node>(n => nodes.Any(nn => nn.NodeId.Equals(n.Id) && nn.Host == n.Host && nn.Port == n.Port)),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Skip_Nodes_That_Fail_Node_Construction(CancellationToken cancellationToken)
        {
            INetworkStorage storageMock = Substitute.For<INetworkStorage>();
            NetworkNode badNode = new(TestItem.PublicKeyB, "192.168.1.2", -1, 0);
            storageMock.GetPersistedNodes().Returns([badNode, NodeA]);

            await CreateManager(storageMock).LoadPersistedNodes(cancellationToken);

            await PingReceived(_discv4Adapter, NodeA);
            await _discv4Adapter.DidNotReceive().Ping(
                Arg.Is<Node>(n => n.Id.Equals(badNode.NodeId)),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Handle_Ping_Exceptions(CancellationToken cancellationToken)
        {
            _networkStorage.UpdateNodes([NodeA, NodeB]);

            _discv4Adapter.Ping(Arg.Is<Node>(n => n.Id.Equals(NodeA.NodeId)), Arg.Any<CancellationToken>())
                .Returns(true);
            _discv4Adapter.Ping(Arg.Is<Node>(n => n.Id.Equals(NodeB.NodeId)), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<bool>(new Exception("Test exception")));

            await _persistenceManager.LoadPersistedNodes(cancellationToken);
        }

        [Test]
        [CancelAfter(10000)]
        public void AddPersistedNodes_Should_Propagate_Cancellation(CancellationToken cancellationToken)
        {
            // A non-cancellation exception is swallowed (above), but a cancelled ping is lifecycle
            // shutdown and must stop the load promptly rather than be swallowed.
            _networkStorage.UpdateNodes([NodeA]);
            _discv4Adapter.Ping(Arg.Is<Node>(n => n.Id.Equals(NodeA.NodeId)), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<bool>(new OperationCanceledException()));

            Assert.ThrowsAsync<OperationCanceledException>(
                async () => await _persistenceManager.LoadPersistedNodes(cancellationToken));
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
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Preserve_Tcp_And_Discovery_Ports(CancellationToken cancellationToken)
        {
            NetworkNode networkNode = new(new Enode(TestItem.PublicKeyA, IPAddress.Parse("192.168.1.1"), 30303, 30304));
            _networkStorage.UpdateNodes([networkNode]);

            await _persistenceManager.LoadPersistedNodes(cancellationToken);

            await _discv4Adapter.Received(1).Ping(
                Arg.Is<Node>(n =>
                    n.Id.Equals(networkNode.NodeId) &&
                    n.Host == networkNode.Host &&
                    n.Port == 30303 &&
                    n.DiscoveryPort == 30304),
                Arg.Any<CancellationToken>());
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

        [TestCase(30303, 30304)]
        [TestCase(0, 30304)]
        [CancelAfter(10000)]
        public async Task RunDiscoveryPersistenceCommit_Should_Preserve_Tcp_And_Discovery_Ports(
            int tcpPort,
            int discoveryPort,
            CancellationToken cancellationToken)
        {
            Node node = tcpPort == 0
                ? Node.FromDiscoveryEndpoint(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Parse("192.168.1.1"), discoveryPort))
                : new Node(TestItem.PublicKeyA, "192.168.1.1", tcpPort, discoveryPort);
            NetworkNode[] persistedNodes = await RunSinglePersistenceCommit(node, cancellationToken);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(persistedNodes, Has.Length.EqualTo(1));
                Assert.That(persistedNodes[0].Port, Is.EqualTo(tcpPort));
                Assert.That(persistedNodes[0].DiscoveryPort, Is.EqualTo(discoveryPort));
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task RunDiscoveryPersistenceCommit_Should_Preserve_Enr_In_Common_Storage(CancellationToken cancellationToken)
        {
            NodeRecord enr = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: 30304);
            Node node = new(TestItem.PrivateKeyA.PublicKey, "8.8.8.8", 30304)
            {
                Enr = enr
            };

            NetworkNode[] persistedNodes = await RunSinglePersistenceCommit(node, cancellationToken);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(persistedNodes, Has.Length.EqualTo(1));
                NetworkNode persistedNode = persistedNodes[0];
                NodeRecord? persistedEnr = persistedNode.Enr;
                Assert.That(persistedNode.IsEnr, Is.True);
                Assert.That(persistedEnr, Is.Not.Null);
                Assert.That(persistedEnr!.ToString(), Is.EqualTo(enr.ToString()));
                Assert.That(persistedNode.NodeId, Is.EqualTo(TestItem.PrivateKeyA.PublicKey));
                Assert.That(persistedNode.Host, Is.EqualTo("8.8.8.8"));
                Assert.That(persistedNode.Port, Is.EqualTo(30303));
                Assert.That(persistedNode.DiscoveryPort, Is.EqualTo(30304));
            }
        }

        private async Task<NetworkNode[]> RunSinglePersistenceCommit(Node node, CancellationToken cancellationToken)
        {
            TaskCompletionSource commitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            INetworkStorage storage = new SignallingNetworkStorage(_networkStorage, commitSource);
            using CancellationTokenSource cts = new();
            DiscoveryPersistenceManager persistenceManager = CreateManager(storage);

            _kademlia.IterateNodes().Returns([node]);

            Task persistenceTask = persistenceManager.RunDiscoveryPersistenceCommit(cts.Token);
            await commitSource.Task.WaitAsync(cancellationToken);
            await StopPersistenceCommit(persistenceTask, cts);

            NetworkStorage reloadedStorage = new(_discoveryDb, LimboLogs.Instance);
            return reloadedStorage.GetPersistedNodes();
        }

        private static async Task StopPersistenceCommit(Task persistenceTask, CancellationTokenSource cts)
        {
            await cts.CancelAsync();
            try
            {
                await persistenceTask;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
        }

        private sealed class SignallingNetworkStorage(INetworkStorage storage, TaskCompletionSource commitSource) : INetworkStorage
        {
            public NetworkNode[] GetPersistedNodes() => storage.GetPersistedNodes();

            public int PersistedNodesCount => storage.PersistedNodesCount;

            public void UpdateNode(NetworkNode node) => storage.UpdateNode(node);

            public void UpdateNodes(IEnumerable<NetworkNode> nodes) => storage.UpdateNodes(nodes);

            public void RemoveNode(PublicKey nodeId) => storage.RemoveNode(nodeId);

            public void StartBatch() => storage.StartBatch();

            public void Commit()
            {
                storage.Commit();
                commitSource.TrySetResult();
            }

            public bool AnyPendingChange() => storage.AnyPendingChange();
        }
    }
}
