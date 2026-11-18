// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Nethermind.Core.Test.Threading;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.Self)]
    public class DiscoveryPersistenceManagerTests
    {
        private readonly List<PeerAddressPolicy> _addressPolicies = [];

        private PeerAddressPolicy OwnPolicy(PeerAddressPolicy policy)
        {
            _addressPolicies.Add(policy);
            return policy;
        }

        private void DisposePolicies()
        {
            foreach (PeerAddressPolicy policy in _addressPolicies) policy.Dispose();
            _addressPolicies.Clear();
        }

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
            _kademlia.GetAllAtDistance(0).Returns([new Node(TestItem.PublicKeyD, "192.168.1.100", 30303)]);

            _persistenceManager = CreateManager(_networkStorage);
        }

        [TearDown]
        public async Task Teardown()
        {
            await _discv4Adapter.DisposeAsync();
            _discoveryDb.Dispose();
            DisposePolicies();
        }

        private DiscoveryPersistenceManager CreateManager(INetworkStorage storage) => new(
            storage,
            _nodeStatsManager,
            _discv4Adapter,
            _kademlia,
            _discoveryConfig,
            _logManager,
            OwnPolicy(new PeerAddressPolicy(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("192.168.1.100")),
                new LocalAddressSource(_logManager, () => []))));

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
        public async Task AddPersistedNodes_Should_Skip_Nodes_That_Fail_Node_Construction(
            [Values] bool useFactory, [Values] bool discoveryPort, [Values(-1, 65536)] int invalidPort,
            CancellationToken cancellationToken)
        {
            INetworkStorage storageMock = Substitute.For<INetworkStorage>();
            NetworkNode badNode = new(new Enode(TestItem.PublicKeyB, IPAddress.Parse("192.168.1.2"),
                discoveryPort ? 30303 : invalidPort, discoveryPort ? invalidPort : 30303));
            storageMock.GetPersistedNodes().Returns([badNode, NodeA]);
            DiscoveryPersistenceManager manager = CreateManager(storageMock);

            await manager.LoadPersistedNodes(cancellationToken,
                useFactory ? (node, permissions) => DiscoveryApp.RestorePersistedNode(node, IPAddress.Any, permissions) : null);

            await PingReceived(_discv4Adapter, NodeA);
            await _discv4Adapter.DidNotReceive().Ping(
                Arg.Is<Node>(n => n.Id.Equals(badNode.NodeId)),
                Arg.Any<CancellationToken>());
            storageMock.Received().RemoveNode(badNode.NodeId);
            NetworkNode silent = new(TestItem.PublicKeyC, "192.168.1.3", 30303);
            storageMock.GetPersistedNodes().Returns([silent]);
            manager.CommitLiveNodes();
            storageMock.Received().RemoveNode(silent.NodeId);
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
        public async Task AddPersistedNodes_Should_Propagate_Cancellation(CancellationToken cancellationToken)
        {
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _networkStorage.UpdateNodes([NodeA]);
            _discv4Adapter.Ping(Arg.Is<Node>(n => n.Id.Equals(NodeA.NodeId)), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    cancellation.Cancel();
                    return Task.FromException<bool>(new OperationCanceledException(cancellation.Token));
                });

            Assert.That(async () => await _persistenceManager.LoadPersistedNodes(cancellation.Token),
                Throws.TypeOf<TaskCanceledException>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Restore_Reputation([Values] bool bonded, CancellationToken cancellationToken)
        {
            const int reputation = 123;
            NetworkNode networkNode = new(TestItem.PublicKeyA, "192.168.1.1", 30303, reputation);
            INodeStats nodeStats = Substitute.For<INodeStats>();

            _networkStorage.UpdateNodes([networkNode]);
            _nodeStatsManager.GetStatsOrNull(Arg.Any<Node>()).Returns(nodeStats);

            _nodeStatsManager.ClearReceivedCalls();
            bool statsBeforeProbe = false;
            _discv4Adapter.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                statsBeforeProbe = _nodeStatsManager.ReceivedCalls().Any();
                return bonded;
            });
            await _persistenceManager.LoadPersistedNodes(cancellationToken);

            Assert.That(statsBeforeProbe, Is.False);
            _nodeStatsManager.Received(bonded ? 1 : 0).GetStatsOrNull(Arg.Is<Node>(n =>
                n.Id.Equals(networkNode.NodeId) &&
                n.Host == networkNode.Host &&
                n.Port == networkNode.Port));
            nodeStats.Received(bonded ? 1 : 0).CurrentPersistedNodeReputation = reputation;
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
        [CancelAfter(10000)]
        public async Task AddPersistedNodes_Should_Use_Protocol_Node_Factory(CancellationToken cancellationToken)
        {
            NetworkNode persistedNode = new(new Enode(TestItem.PublicKeyA, IPAddress.Parse("192.168.1.1"), 30303, 30304));
            Node restoredNode = new(TestItem.PublicKeyA, IPAddress.Parse("fd00::1").ToString(), 30305, 30306);
            _networkStorage.UpdateNodes([persistedNode]);

            await _persistenceManager.LoadPersistedNodes(
                cancellationToken,
                (node, _) => node.NodeId.Equals(persistedNode.NodeId) ? restoredNode : null);

            await _discv4Adapter.Received(1).Ping(restoredNode, Arg.Any<CancellationToken>());
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

            foreach (Node node in nodes) DiscoveryLiveness.Mark(node);
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

            node.SetVerifiedEnr(enr);
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

        [Test]
        [CancelAfter(10000)]
        public async Task Load_should_reject_our_identity_and_our_udp_endpoint(CancellationToken token)
        {
            Node self = new(TestItem.PublicKeyC, "192.168.1.100", 30303);
            _kademlia.GetAllAtDistance(0).Returns([self]);
            _discv4Adapter.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(true);
            _networkStorage.UpdateNodes([
                new NetworkNode(TestItem.PublicKeyC, "192.168.1.3", 30303, 0),
                new NetworkNode(TestItem.PublicKeyB, "192.168.1.100", 30303, 0),
                NodeA]);

            await _persistenceManager.LoadPersistedNodes(token);

            await PingReceived(_discv4Adapter, NodeA);
            await _discv4Adapter.Received(1).Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>());
            Assert.That(_networkStorage.GetPersistedNodes(), Has.Length.EqualTo(1));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Commit_should_prune_nodes_no_longer_in_routing_table(CancellationToken token)
        {
            _networkStorage.UpdateNodes([NodeA, NodeB]);
            _discv4Adapter.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(true);
            NetworkNode[] stored = await RunSinglePersistenceCommit(new Node(NodeA), token);
            Assert.That(stored, Has.Length.EqualTo(1));
            Assert.That(stored[0].NodeId, Is.EqualTo(NodeA.NodeId));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Loading_should_use_alpha_bounded_parallel_probes_and_cancel_them(CancellationToken token)
        {
            _networkStorage.UpdateNodes([NodeA, NodeB, new NetworkNode(TestItem.PublicKeyC, "192.168.1.3", 30303, 0)]);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            int active = 0;
            TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _discv4Adapter.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                int running = Interlocked.Increment(ref active);
                Assert.That(running, Is.LessThanOrEqualTo(_discoveryConfig.Concurrency));
                if (running == _discoveryConfig.Concurrency) allStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()); }
                finally { Interlocked.Decrement(ref active); }
                return false;
            });
            Task load = _persistenceManager.LoadPersistedNodes(cancellation.Token);
            try
            {
                await Task.WhenAny(allStarted.Task, Task.Delay(200, token));
                Assert.That(active, Is.EqualTo(_discoveryConfig.Concurrency));
            }
            finally
            {
                await cancellation.CancelAsync();
                await load.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            Assert.That(active, Is.Zero);
        }

        [Test]
        public void Commit_should_keep_only_recent_proof_and_exclude_self()
        {
            Node live = new(NodeA);
            Node stale = new(NodeB);
            Node unverified = new(TestItem.PublicKeyC, "192.168.1.3", 30303);
            Node self = new(TestItem.PublicKeyD, "192.168.1.100", 30303);
            Node sameEndpoint = new(TestItem.PublicKeyE, "::ffff:192.168.1.100", 30303);
            DiscoveryLiveness.Mark(live);
            DiscoveryLiveness.Mark(stale, Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * DiscoveryLiveness.MaximumAge.TotalSeconds) - Stopwatch.Frequency);
            DiscoveryLiveness.Mark(self);
            DiscoveryLiveness.Mark(sameEndpoint);
            _kademlia.IterateNodes().Returns([live, stale, unverified, self, sameEndpoint]);

            _persistenceManager.CommitLiveNodes();

            NetworkNode[] stored = _networkStorage.GetPersistedNodes();
            Assert.That(stored, Has.Length.EqualTo(1));
            Assert.That(stored[0].NodeId, Is.EqualTo(live.Id));
        }

        [Test]
        public async Task Persisted_churn_should_remain_bounded_and_remove_previous_generation()
        {
            await _persistenceManager.LoadPersistedNodes(CancellationToken.None);
            int count = DiscoveryPersistenceManager.MaximumPersistedNodes + 10;
            Node[] first = CreateManyNodes(count, 1);
            _kademlia.IterateNodes().Returns(first);
            _persistenceManager.CommitLiveNodes();
            Assert.That(_networkStorage.PersistedNodesCount, Is.EqualTo(DiscoveryPersistenceManager.MaximumPersistedNodes));

            Node[] second = CreateManyNodes(count, count + 1);
            _kademlia.IterateNodes().Returns(second);
            _persistenceManager.CommitLiveNodes();

            NetworkNode[] stored = _networkStorage.GetPersistedNodes();
            Assert.That(stored, Has.Length.EqualTo(DiscoveryPersistenceManager.MaximumPersistedNodes));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stored.Any(node => node.NodeId.Equals(first[0].Id)), Is.False);
                Assert.That(stored.Any(node => node.NodeId.Equals(second[0].Id)), Is.True);
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Loading_should_prune_oversized_legacy_store_before_waiting_for_probes(CancellationToken token)
        {
            Node[] legacy = CreateManyNodes(DiscoveryPersistenceManager.MaximumPersistedNodes + 10, 1);
            _networkStorage.UpdateNodes(legacy.Select(node => new NetworkNode(new Enode(node.Id, node.DiscoveryAddress.Address, node.Port, node.DiscoveryPort))));
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _discv4Adapter.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return false;
            });
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task loading = _persistenceManager.LoadPersistedNodes(cancellation.Token);
            try
            {
                await started.Task.WaitAsync(token);
                Assert.That(_networkStorage.PersistedNodesCount, Is.EqualTo(DiscoveryPersistenceManager.MaximumPersistedNodes));
            }
            finally
            {
                await cancellation.CancelAsync();
                await loading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        [Test]
        public async Task Failed_legacy_probe_should_not_delete_a_newer_live_contact()
        {
            _networkStorage.UpdateNodes([NodeA]);
            Node live = new(NodeA.NodeId, "192.168.1.9", 30303);
            DiscoveryLiveness.Mark(live);
            _kademlia.IterateNodes().Returns([live]);
            _discv4Adapter.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                _persistenceManager.CommitLiveNodes();
                return false;
            });

            await _persistenceManager.LoadPersistedNodes(CancellationToken.None);

            NetworkNode[] stored = _networkStorage.GetPersistedNodes();
            Assert.That(stored, Has.Length.EqualTo(1));
            Assert.That(stored[0].Host, Is.EqualTo(live.Host));
        }

        [Test]
        public void Endpoint_mutation_should_invalidate_persistence_proof([Values] bool changeAddress)
        {
            Node node = new(NodeA);
            DiscoveryLiveness.Mark(node);
            if (changeAddress) node.Address.Address = IPAddress.Parse("9.9.9.9");
            else node.DiscoveryPort++;
            _kademlia.IterateNodes().Returns([node]);

            _persistenceManager.CommitLiveNodes();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(DiscoveryLiveness.IsRecent(node), Is.False);
                Assert.That(_networkStorage.GetPersistedNodes(), Is.Empty);
            }
        }

        [Test]
        public async Task Background_load_failure_should_be_logged_without_waiting_for_discovery_shutdown()
        {
            INetworkStorage storage = Substitute.For<INetworkStorage>();
            InvalidOperationException failure = new("Unavailable storage");
            storage.GetPersistedNodes().Returns(_ => throw failure);
            InterfaceLogger underlying = Substitute.For<InterfaceLogger>();
            underlying.IsError.Returns(true);
            ILogManager logs = Substitute.For<ILogManager>();
            ILogger logger = new(underlying);
            logs.GetClassLogger<DiscoveryPersistenceManager>().Returns(logger);
            DiscoveryPersistenceManager manager = new(storage, _nodeStatsManager, _discv4Adapter, _kademlia, _discoveryConfig, logs,
                OwnPolicy(new PeerAddressPolicy(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("192.168.1.100")),
                    new LocalAddressSource(logs, () => []))));

            await manager.LoadPersistedNodesInBackground(CancellationToken.None);

            underlying.Received(1).Error("Error loading persisted discovery nodes", failure);
        }

        private static Node[] CreateManyNodes(int count, int offset)
        {
            Node[] nodes = new Node[count];
            for (int i = 0; i < count; i++)
            {
                byte[] key = new byte[64];
                BitConverter.TryWriteBytes(key.AsSpan(), i + offset);
                nodes[i] = new Node(new PublicKey(key), "8.8.4.4", 30303);
                DiscoveryLiveness.Mark(nodes[i]);
            }
            return nodes;
        }

        private async Task<NetworkNode[]> RunSinglePersistenceCommit(Node node, CancellationToken cancellationToken)
        {
            DiscoveryLiveness.Mark(node);
            TaskCompletionSource commitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SignallingNetworkStorage storage = new(_networkStorage, commitSource);
            using CancellationTokenSource cts = new();
            DiscoveryPersistenceManager persistenceManager = CreateManager(storage);
            await persistenceManager.LoadPersistedNodes(cancellationToken);
            commitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            storage.ResetCommitSignal(commitSource);

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
            public void ResetCommitSignal(TaskCompletionSource replacement) => commitSource = replacement;

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

        [Test]
        [CancelAfter(10000)]
        public async Task Fifty_silent_nodes_use_only_three_running_probes([Values] bool offline, CancellationToken token)
        {
            using MemDb db = new();
            NetworkStorage storage = new(db, LimboLogs.Instance);
            NetworkNode[] nodes = Enumerable.Range(1, 50).Select(index =>
            {
                byte[] key = new byte[64];
                key[63] = (byte)index;
                return new NetworkNode(new PublicKey(key), $"8.8.4.{index}", 30303, 0);
            }).ToArray();
            storage.UpdateNodes(nodes);
            storage.Commit();
            IKademliaMessageSender<PublicKey, Node> sender = Substitute.For<IKademliaMessageSender<PublicKey, Node>>();
            DiscoveryPersistenceManager manager = CreateStartupPersistenceManager(storage, sender);
            Channel<TaskCompletionSource<bool>> pending = Channel.CreateUnbounded<TaskCompletionSource<bool>>();
            ConcurrentQueue<(PublicKey Id, int Running)> attempts = new();
            int running = 0;
            sender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                attempts.Enqueue((call.Arg<Node>().Id, Interlocked.Increment(ref running)));
                TaskCompletionSource<bool> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Writer.TryWrite(response);
                try
                {
                    bool result = await response.Task.WaitAsync(call.Arg<CancellationToken>());
                    if (offline) throw new KademliaRequestNotSentException();
                    return result;
                }
                finally { Interlocked.Decrement(ref running); }
            });
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task load = manager.LoadPersistedNodes(lifetime.Token);
            try
            {
                TaskCompletionSource<bool> first = await pending.Reader.ReadAsync(token);
                TaskCompletionSource<bool> second = await pending.Reader.ReadAsync(token);
                TaskCompletionSource<bool> third = await pending.Reader.ReadAsync(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(attempts.Count, Is.EqualTo(3));
                    Assert.That(Volatile.Read(ref running), Is.EqualTo(3));
                }
                first.SetResult(false);
                for (int index = 3; index < 50; index++)
                    (await pending.Reader.ReadAsync(token)).SetResult(false);
                second.SetResult(false);
                third.SetResult(false);
                await load;

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(attempts.Select(attempt => attempt.Id), Is.EquivalentTo(nodes.Select(node => node.NodeId)));
                    Assert.That(attempts.Max(attempt => attempt.Running), Is.EqualTo(3));
                    Assert.That(Volatile.Read(ref running), Is.Zero);
                    Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId),
                        Is.EquivalentTo(nodes.Select(node => node.NodeId)));
                }
            }
            finally
            {
                await lifetime.CancelAsync();
                await load.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Periodic_commit_preserves_unproven_store_until_startup_load_finishes(
            [Values("Silent", "NotSent", "LocalCancellation")] string outcome, CancellationToken token)
        {
            using MemDb db = new();
            ObservedStorage storage = new(new NetworkStorage(db, LimboLogs.Instance));
            storage.UpdateNode(new NetworkNode(TestItem.PublicKeyA, "8.8.4.1", 30303, 0));
            storage.Commit();
            TaskCompletionSource probeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IKademliaMessageSender<PublicKey, Node> sender = Substitute.For<IKademliaMessageSender<PublicKey, Node>>();
            sender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                probeStarted.TrySetResult();
                bool result = await reply.Task.WaitAsync(call.Arg<CancellationToken>());
                if (outcome == "NotSent") throw new KademliaRequestNotSentException();
                if (outcome == "LocalCancellation") throw new OperationCanceledException();
                return result;
            });
            ManualTimeProvider clock = new();
            DiscoveryPersistenceManager manager = CreateStartupPersistenceManager(storage, sender, clock);
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task load = manager.LoadPersistedNodes(lifetime.Token);
            await probeStarted.Task.WaitAsync(token);
            storage.DrainCommits();
            Task persistence = manager.RunDiscoveryPersistenceCommit(load, lifetime.Token);
            try
            {
                manager.CommitLiveNodes();
                Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId), Is.EqualTo(new[] { TestItem.PublicKeyA }));
                reply.SetResult(false);
                await load;
                await clock.TimerCreated.WaitAsync(token);
                Assert.That(clock.DueTimes, Is.EqualTo(new[] { TimeSpan.FromSeconds(180) }));
                storage.DrainCommits();
                clock.AdvanceAndFireTimer(TimeSpan.FromSeconds(180));
                await storage.NextCommit(token);
                Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId),
                    Is.EqualTo(new[] { TestItem.PublicKeyA }));
            }
            finally
            {
                await lifetime.CancelAsync();
                await Task.WhenAll(load, persistence).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }





        private DiscoveryPersistenceManager CreateStartupPersistenceManager(INetworkStorage storage,
            IKademliaMessageSender<PublicKey, Node> sender, TimeProvider? clock = null)
        {
            IKademlia<PublicKey, Node> table = Substitute.For<IKademlia<PublicKey, Node>>();
            table.GetAllAtDistance(0).Returns([new Node(TestItem.PublicKeyF, "8.8.8.8", 30303)]);
            return new(storage, Substitute.For<INodeStatsManager>(), sender, table,
                new DiscoveryConfig { Concurrency = 3 }, LimboLogs.Instance,
                OwnPolicy(new PeerAddressPolicy(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("8.8.8.8")),
                    new LocalAddressSource(LimboLogs.Instance, () => []))))
            {
                PersistenceTimeProvider = clock ?? TimeProvider.System
            };
        }

        internal sealed class ObservedStorage(INetworkStorage inner) : INetworkStorage
        {
            private readonly Channel<bool> _commits = Channel.CreateUnbounded<bool>();
            public bool FailReads { get; set; }
            public NetworkNode[] GetPersistedNodes()
                => FailReads ? throw new InvalidOperationException("Storage read failed.") : inner.GetPersistedNodes();
            public int PersistedNodesCount => inner.PersistedNodesCount;
            public void UpdateNode(NetworkNode node) => inner.UpdateNode(node);
            public void UpdateNodes(IEnumerable<NetworkNode> nodes) => inner.UpdateNodes(nodes);
            public void RemoveNode(PublicKey nodeId) => inner.RemoveNode(nodeId);
            public void StartBatch() => inner.StartBatch();
            public bool AnyPendingChange() => inner.AnyPendingChange();
            public void Commit()
            {
                inner.Commit();
                _commits.Writer.TryWrite(true);
            }
            public ValueTask<bool> NextCommit(CancellationToken token) => _commits.Reader.ReadAsync(token);
            public void DrainCommits()
            {
                while (_commits.Reader.TryRead(out _)) { }
            }
        }

        [Test]
        public async Task Neutral_retention_obeys_the_current_resolved_address_policy(
            [Values("Private", "Public", "ResolverFailure", "VerifiedReload")] string policyState, [Values] bool policyInFactory)
        {
            using MemDb db = new();
            NetworkStorage storage = new(db, LimboLogs.Instance);
            storage.UpdateNode(new NetworkNode(TestItem.PublicKeyA, "192.168.1.2", 30303, 0));
            storage.Commit();
            IKademliaMessageSender<PublicKey, Node> sender = Substitute.For<IKademliaMessageSender<PublicKey, Node>>();
            sender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(_ =>
                Task.FromException<bool>(new KademliaRequestNotSentException()));
            IPAddress external = IPAddress.Parse("192.168.1.1");
            bool resolverFailure = false;
            IIPResolver resolver = Substitute.For<IIPResolver>();
            resolver.Resolve(Arg.Any<CancellationToken>()).Returns(_ =>
                resolverFailure ? ValueTask.FromException<IIPResolver.NethermindIp>(new InvalidOperationException())
                    : new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(IPAddress.Any, external)));
            using PeerAddressPolicy policy = new(resolver, new LocalAddressSource(LimboLogs.Instance, () => Array.Empty<IPAddress>()));
            ContainerBuilder builder = new ContainerBuilder()
                .AddModule(new KademliaModule(new Node(TestItem.PublicKeyF, "192.168.1.1", 30303), []))
                .AddSingleton<IKademliaMessageSender<PublicKey, Node>>(sender)
                .AddSingleton<INodeStatsManager>(Substitute.For<INodeStatsManager>())
                .AddSingleton<IDiscoveryConfig>(new DiscoveryConfig())
                .AddSingleton<ILogManager>(LimboLogs.Instance)
                .AddSingleton<PeerAddressPolicy>(policy);
            builder.RegisterInstance(storage).Keyed<INetworkStorage>(DbNames.DiscoveryNodes).ExternallyOwned();
            using IContainer container = builder.Build();
            IKademlia<PublicKey, Node> table = container.Resolve<IKademlia<PublicKey, Node>>();
            DiscoveryPersistenceManager manager = container.Resolve<DiscoveryPersistenceManager>();
            Func<NetworkNode, PeerAddressPermissions, Node?>? factory = policyInFactory
                ? (stored, permissions) => DiscoveryApp.RestorePersistedNode(stored, IPAddress.Any, permissions)
                : null;
            await manager.LoadPersistedNodes(CancellationToken.None, factory);
            Assert.That(storage.PersistedNodesCount, Is.EqualTo(1));

            if (policyState == "ResolverFailure")
            {
                resolverFailure = true;
                manager.CommitLiveNodes();
                await manager.LoadPersistedNodes(CancellationToken.None, factory);
                Assert.That(storage.PersistedNodesCount, Is.EqualTo(1));
                resolverFailure = false;
            }
            if (policyState is "Public" or "ResolverFailure")
            {
                external = IPAddress.Parse("8.8.8.8");
                resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
                    new IIPResolver.NethermindIp(IPAddress.Any, external));
            }
            if (policyState == "VerifiedReload")
            {
                sender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    Node node = call.Arg<Node>();
                    DiscoveryLiveness.Mark(node);
                    table.AddOrRefresh(node);
                    return Task.FromResult(true);
                });
                await manager.LoadPersistedNodes(CancellationToken.None, factory);
            }
            if (policyState == "ResolverFailure") await manager.LoadPersistedNodes(CancellationToken.None, factory);
            manager.CommitLiveNodes();
            using (Assert.EnterMultipleScope())
            {
                if (policyState == "VerifiedReload")
                    Assert.That(table.IterateNodes().Where(node => node.Id == TestItem.PublicKeyA).Select(node => node.DiscoveryAddress.Address),
                        Is.EqualTo(new[] { IPAddress.Parse("192.168.1.2") }));
                Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId),
                    Is.EqualTo(new[] { TestItem.PublicKeyA }));
            }
        }

        [Test]
        public async Task Neutral_enr_retention_uses_the_loaded_address_family([Values] bool publicIpv6)
        {
            using MemDb db = new();
            NetworkStorage storage = new(db, LimboLogs.Instance);
            NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA,
                IPAddress.Parse(publicIpv6 ? "192.168.1.2" : "9.9.9.9"), configureExtras: enr =>
                {
                    enr.SetEntry(new Ip6Entry(IPAddress.Parse(publicIpv6 ? "2001:4860:4860::8888" : "fc00::2")));
                    enr.SetEntry(new Udp6Entry(30303));
                });
            storage.UpdateNode(new NetworkNode(record.ToString()));
            storage.Commit();
            IKademlia<PublicKey, Node> table = Substitute.For<IKademlia<PublicKey, Node>>();
            table.GetAllAtDistance(0).Returns([new Node(TestItem.PublicKeyF, "2001:4860:4860::8844", 30303)]);
            IKademliaMessageSender<PublicKey, Node> sender = Substitute.For<IKademliaMessageSender<PublicKey, Node>>();
            sender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(_ =>
                Task.FromException<bool>(new KademliaRequestNotSentException()));
            IPAddress external = IPAddress.Parse(publicIpv6 ? "8.8.8.8" : "192.168.1.1");
            IIPResolver resolver = Substitute.For<IIPResolver>();
            resolver.Resolve(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IIPResolver.NethermindIp>(
                new IIPResolver.NethermindIp(IPAddress.Any, external)));
            using PeerAddressPolicy policy = new(resolver, new LocalAddressSource(LimboLogs.Instance, () => []));
            DiscoveryPersistenceManager manager = new(storage, Substitute.For<INodeStatsManager>(), sender, table,
                new DiscoveryConfig(), LimboLogs.Instance, policy);
            IPAddress listener = publicIpv6 ? IPAddress.IPv6Any : IPAddress.Parse("2001:4860:4860::8844");
            await manager.LoadPersistedNodes(CancellationToken.None,
                (stored, permissions) => DiscoveryApp.RestorePersistedNode(stored, listener, permissions));
            Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId), Is.EqualTo(new[] { TestItem.PublicKeyA }));
            await sender.Received(1).Ping(Arg.Is<Node>(node => node.DiscoveryAddress.Address.AddressFamily ==
                System.Net.Sockets.AddressFamily.InterNetworkV6), Arg.Any<CancellationToken>());
            external = IPAddress.Parse("8.8.8.8");
            resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
                new IIPResolver.NethermindIp(IPAddress.Any, external));
            manager.CommitLiveNodes();
            Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId),
                Is.EqualTo(new[] { TestItem.PublicKeyA }));
        }

        [Test]
        [CancelAfter(20000)]
        public Task Neutral_and_live_rows_share_one_persistence_limit([Values] bool repeatWithNewRows, CancellationToken token)
            => CheckSharedPersistenceLimit(repeatWithNewRows, policyBlocked: false, token);

        /// <remarks>A full policy-blocked store defers new persisted identities until a retained row is removed.</remarks>
        [Test]
        [CancelAfter(20000)]
        public Task Policy_blocked_store_saturation_defers_new_persistence_accepted_residual(
            [Values] bool repeatWithNewRows, CancellationToken token)
            => CheckSharedPersistenceLimit(repeatWithNewRows, policyBlocked: true, token);

        private async Task CheckSharedPersistenceLimit(bool repeatWithNewRows, bool policyBlocked, CancellationToken token)
        {
            using MemDb db = new();
            NetworkStorage storage = new(db, LimboLogs.Instance);
            NetworkNode[] stored = CreateRows(1, policyBlocked ? "192.168.1.2" : "8.8.4.1");
            storage.UpdateNodes(stored);
            storage.Commit();
            using PersistenceTestScope scope = new(storage);
            IKademlia<PublicKey, Node> table = scope.Table;
            IKademliaMessageSender<PublicKey, Node> sender = scope.Sender;
            IIPResolver resolver = scope.Resolver;
            DiscoveryPersistenceManager manager = scope.Manager;
            sender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(_ =>
                Task.FromException<bool>(new KademliaRequestNotSentException()));
            await manager.LoadPersistedNodes(token);
            if (repeatWithNewRows)
            {
                foreach (NetworkNode row in stored) storage.RemoveNode(row.NodeId);
                stored = CreateRows(10_000, policyBlocked ? "192.168.1.2" : "8.8.4.1");
                storage.UpdateNodes(stored);
                storage.Commit();
                await manager.LoadPersistedNodes(token);
            }
            Node live = new(TestItem.PublicKeyA, "9.9.9.9", 30303) { ValidatedProtocol = false };
            DiscoveryLiveness.Mark(live);
            table.IterateNodes().Returns([live]);

            manager.CommitLiveNodes();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId), Is.EquivalentTo(stored.Select(node => node.NodeId)));
                Assert.That(storage.PersistedNodesCount, Is.EqualTo(DiscoveryPersistenceManager.MaximumPersistedNodes));
            }
            await sender.Received(policyBlocked ? 0 : DiscoveryPersistenceManager.MaximumPersistedNodes * (repeatWithNewRows ? 2 : 1))
                .Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>());
            resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
                new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("192.168.1.1")));
            sender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(false);
            await manager.LoadPersistedNodes(token);
            manager.RequestActivity.Answered(manager.RequestActivity.BeginRequest());
            manager.CommitLiveNodes();
            Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId), Is.EqualTo(new[] { live.Id }));
        }

        [Test]
        public async Task Current_policy_blocks_persisted_targets_without_erasing_them_and_reconsiders_after_refresh(
            [Values("192.168.1.2", "fc00::2", "127.0.0.1", "169.254.1.2")] string target,
            [Values] bool useFactory)
        {
            using MemDb db = new();
            NetworkStorage storage = new(db, LimboLogs.Instance);
            NetworkNode row = new(TestItem.PublicKeyA, target, 30303, 0);
            storage.UpdateNode(row);
            storage.Commit();
            using PersistenceTestScope scope = new(storage);
            IKademliaMessageSender<PublicKey, Node> sender = scope.Sender;
            IIPResolver resolver = scope.Resolver;
            DiscoveryPersistenceManager manager = scope.Manager;
            sender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(_ =>
                Task.FromException<bool>(new KademliaRequestNotSentException()));
            Func<NetworkNode, PeerAddressPermissions, Node?>? factory = useFactory
                ? (stored, permissions) => DiscoveryApp.RestorePersistedNode(stored, IPAddress.IPv6Any, permissions)
                : null;

            await manager.LoadPersistedNodes(CancellationToken.None, factory);
            manager.CommitLiveNodes();
            Assert.That(storage.GetPersistedNodes().Select(stored => stored.NodeId), Is.EqualTo(new[] { row.NodeId }));
            await sender.DidNotReceive().Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>());

            resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
                new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("192.168.1.1")));
            await manager.LoadPersistedNodes(CancellationToken.None, factory);
            manager.CommitLiveNodes();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage.GetPersistedNodes().Select(stored => stored.NodeId), Is.EqualTo(new[] { row.NodeId }));
                Assert.That(storage.PersistedNodesCount, Is.EqualTo(1));
            }
            await sender.Received(target is "192.168.1.2" or "fc00::2" ? 1 : 0)
                .Ping(Arg.Is<Node>(node => node.Id.Equals(row.NodeId) && node.DiscoveryAddress.Address.Equals(IPAddress.Parse(target))),
                    Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task Factory_rejected_rows_are_bounded_and_neutral_only_until_the_first_online_commit(
            [Values] bool recover, [Values(1, DiscoveryPersistenceManager.MaximumPersistedNodes + 10)] int count)
        {
            using MemDb db = new();
            NetworkStorage storage = new(db, LimboLogs.Instance);
            NetworkNode[] rows = CreateRows(1, count: count);
            storage.UpdateNodes(rows);
            storage.Commit();
            using PersistenceTestScope scope = new(storage);
            IKademliaMessageSender<PublicKey, Node> sender = scope.Sender;
            DiscoveryPersistenceManager manager = scope.Manager;
            bool available = false;
            int expected = Math.Min(count, DiscoveryPersistenceManager.MaximumPersistedNodes);
            NetworkNode[] retainedRows = storage.GetPersistedNodes().Take(expected).ToArray();
            await manager.LoadPersistedNodes(CancellationToken.None,
                (stored, permissions) => available ? DiscoveryApp.RestorePersistedNode(stored, IPAddress.Any, permissions) : null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage.PersistedNodesCount, Is.EqualTo(expected));
                Assert.That(storage.GetPersistedNodes().Select(stored => stored.NodeId),
                    Is.EquivalentTo(retainedRows.Select(stored => stored.NodeId)));
            }
            await sender.DidNotReceive().Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>());

            available = recover;
            manager.CommitLiveNodes();
            Assert.That(storage.PersistedNodesCount, Is.EqualTo(recover ? expected : 0));
            await sender.DidNotReceive().Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>());
        }

        private sealed class PersistenceTestScope : IDisposable
        {
            private readonly IContainer _scope;
            public IKademlia<PublicKey, Node> Table { get; } = Substitute.For<IKademlia<PublicKey, Node>>();
            public IKademliaMessageSender<PublicKey, Node> Sender { get; } = Substitute.For<IKademliaMessageSender<PublicKey, Node>>();
            public IIPResolver Resolver { get; } = Substitute.For<IIPResolver>();
            public DiscoveryPersistenceManager Manager { get; }

            public PersistenceTestScope(INetworkStorage storage)
            {
                Node self = new(TestItem.PublicKeyF, "8.8.8.8", 30303);
                Table.GetAllAtDistance(0).Returns([self]);
                Resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new IIPResolver.NethermindIp(IPAddress.Any, self.DiscoveryAddress.Address));
                ContainerBuilder builder = new ContainerBuilder()
                    .AddModule(new KademliaModule(self, []))
                    .AddSingleton<IKademlia<PublicKey, Node>>(Table)
                    .AddSingleton<IKademliaMessageSender<PublicKey, Node>>(Sender)
                    .AddSingleton<INodeStatsManager>(Substitute.For<INodeStatsManager>())
                    .AddSingleton<IDiscoveryConfig>(new DiscoveryConfig { Concurrency = 3 })
                    .AddSingleton<ILogManager>(LimboLogs.Instance)
                    .AddSingleton<IIPResolver>(Resolver)
                    .AddSingleton<LocalAddressSource>(new LocalAddressSource(LimboLogs.Instance, () => []))
                    .AddSingleton<PeerAddressPolicy>();
                builder.RegisterInstance(storage).Keyed<INetworkStorage>(DbNames.DiscoveryNodes).ExternallyOwned();
                _scope = builder.Build();
                try { Manager = _scope.Resolve<DiscoveryPersistenceManager>(); }
                catch
                {
                    _scope.Dispose();
                    throw;
                }
            }

            public void Dispose() => _scope.Dispose();
        }

        internal static NetworkNode[] CreateRows(int start, string host = "8.8.4.1", int count = DiscoveryPersistenceManager.MaximumPersistedNodes)
            => Enumerable.Range(start, count).Select(index =>
            {
                byte[] key = new byte[64];
                BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(60), index);
                return new NetworkNode(new PublicKey(key), host, 30303, 0);
            }).ToArray();
    }
}
