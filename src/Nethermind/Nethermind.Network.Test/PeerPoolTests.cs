// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Test.Threading;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Test.Stats;
using Nethermind.Network.StaticNodes;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

using Nethermind.Core.Timers;

namespace Nethermind.Network.Test;

public class PeerPoolTests
{
    private readonly List<PeerAddressPolicy> _ownedPolicies = [];

    private PeerAddressPolicy OwnPolicy(PeerAddressPolicy policy)
    {
        _ownedPolicies.Add(policy);
        return policy;
    }

    [TearDown]
    public void DisposePolicies()
    {
        foreach (PeerAddressPolicy policy in _ownedPolicies) policy.Dispose();
        _ownedPolicies.Clear();
    }

    [Test]
    public async Task Decorated_stats_publish_only_owned_entries_and_keep_penalties_coherent([Values] bool forwarding)
    {
        using NodeStatsManager retained = new(TimerFactory.Default, LimboLogs.Instance, 3);
        INodeStatsManager manager = forwarding
            ? new NodeStatsManagerTests.ForwardingManager(retained)
            : new NodeStatsManagerTests.LegacyManager(retained);
        await using PersistenceHarness context = new(stats: manager);
        Node node = new(TestItem.PublicKeyB, "9.9.9.9", 30303);
        Peer peer = context.Pool.GetOrAdd(node);
        INodeStats original = retained.GetOrAdd(node);
        for (int i = 0; i < 10; i++) retained.ReportEvent(new Node(TestItem.PublicKeys[i + 10], "8.8.8.8", 30303), NodeStatsEventType.DiscoveryPongIn);
        retained.ReportFailedValidation(node, CompatibilityValidationType.NetworkId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Pool.Peers.ContainsKey(node.Id), Is.EqualTo(forwarding));
            Assert.That(retained.GetOrAdd(node), forwarding ? Is.SameAs(original) : Is.Not.SameAs(original));
            Assert.That(manager.FindCompatibilityValidationResult(node), Is.EqualTo(CompatibilityValidationType.NetworkId));
            if (forwarding) Assert.That(peer.Stats!.FailedCompatibilityValidation, Is.EqualTo(CompatibilityValidationType.NetworkId));
        }
    }

    [Test]
    public async Task Decorated_stats_promotion_and_release_restore_ordinary_and_total_capacity()
    {
        using NodeStatsManager retained = new(TimerFactory.Default, LimboLogs.Instance, 4);
        INodeStatsManager manager = new NodeStatsManagerTests.ForwardingManager(retained);
        await using PersistenceHarness context = new(stats: manager);
        Node first = new(TestItem.PublicKeyB, "9.9.9.9", 30303);
        Peer peer = context.Pool.GetOrAdd(first);
        IDisposable? second;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Pool.Peers.ContainsKey(first.Id), Is.True);
            Assert.That(manager.TryAcquire(new Node(TestItem.PublicKeyC, "8.8.8.8", 30303), false, out _, out second), Is.True);
        }
        using IDisposable? secondLease = second;
        Assert.That(manager.TryAcquire(new Node(TestItem.PublicKeyD, "8.8.8.8", 30303), false, out _, out IDisposable? third), Is.True);
        using IDisposable? thirdLease = third;
        Node next = new(TestItem.PublicKeyE, "8.8.8.8", 30303);
        IDisposable? fourth;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.TryAcquire(next, false, out _, out _), Is.False);
            Assert.That(context.Pool.GetOrAdd(new Node(first.Id, "9.9.9.9", 30303, true)), Is.SameAs(peer));
            Assert.That(manager.TryAcquire(next, false, out _, out fourth), Is.True);
        }
        using IDisposable? fourthLease = fourth;
        Node replacement = new(TestItem.PublicKeyF, "8.8.8.8", 30303);
        IDisposable? recovered;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.TryAcquire(replacement, true, out _, out _), Is.False);
            Assert.That(context.Pool.TryRemove(first.Id, out _), Is.True);
            Assert.That(manager.TryAcquire(replacement, true, out _, out recovered), Is.True);
        }
        using IDisposable? recoveredLease = recovered;
        Assert.That(manager.GetStatsOrNull(replacement), Is.Not.Null);
    }

    [Test]
    public async Task Legacy_nullable_lookup_at_pinned_capacity_returns_null_without_publication()
    {
        using NodeStatsManager retained = new(TimerFactory.Default, LimboLogs.Instance, 1);
        Assert.That(retained.TryAcquire(new Node(TestItem.PublicKeyA, "8.8.8.8", 30303), true, out _, out IDisposable? held), Is.True);
        using IDisposable? heldLease = held;
        INodeStatsManager legacy = new NodeStatsManagerTests.LegacyManager(retained);
        await using PersistenceHarness context = new(stats: legacy);
        Node incoming = new(TestItem.PublicKeyB, "9.9.9.9", 30303);
        INodeStats? stats = null;
        Assert.DoesNotThrow(() => stats = legacy.GetStatsOrNull(incoming));
        INodeStats? acquired;
        IDisposable? lease;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats, Is.Null);
            Assert.That(legacy.TryAcquire(incoming, true, out acquired, out lease), Is.False);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(acquired, Is.Null);
            Assert.That(lease, Is.Null);
        }
        Peer peer = context.Pool.GetOrAdd(incoming);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Node.Id, Is.EqualTo(incoming.Id));
            Assert.That(context.Pool.Peers, Is.Empty);
        }
    }

    [Test]
    public async Task Public_get_or_add_at_stats_capacity_returns_an_unpooled_session_peer([Values] bool networkNode)
    {
        using NodeStatsManager stats = new(Substitute.For<Nethermind.Core.Timers.ITimerFactory>(), LimboLogs.Instance, 1);
        Node held = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        Assert.That(stats.TryAcquire(held, true, out _, out IDisposable? lease), Is.True);
        using IDisposable? heldLease = lease;
        await using PersistenceHarness context = new(stats: stats);
        Node incoming = new(TestItem.PublicKeyB, "9.9.9.9", 30303);

        Peer peer = networkNode
            ? context.Pool.GetOrAdd(new NetworkNode(new Enode(incoming.Id, incoming.Address.Address, incoming.Port)))
            : context.Pool.GetOrAdd(incoming);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Node.Id, Is.EqualTo(incoming.Id));
            Assert.That(peer.Stats, Is.InstanceOf<NodeStatsLight>());
            Assert.That(context.Pool.Peers, Is.Empty);

        }
    }

    [Test]
    public async Task Losing_concurrent_peer_insertions_should_release_their_stats_leases()
    {
        using NodeStatsManager stats = new(Substitute.For<Nethermind.Core.Timers.ITimerFactory>(), LimboLogs.Instance, 1);
        await using PersistenceHarness test = new(stats: stats);
        Node first = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        Peer[] peers = await Task.WhenAll(Enumerable.Range(0, 128).Select(_ => Task.Run(() => test.Pool.GetOrAdd(first))));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers.All(peer => ReferenceEquals(peer, peers[0])), Is.True);

            Assert.That(test.Pool.TryRemove(first.Id, out _), Is.True);
            Assert.DoesNotThrow(() => test.Pool.GetOrAdd(new Node(TestItem.PublicKeyB, "8.8.4.4", 30303)));
        }

    }

    [Test]
    public async Task Feed_should_continue_after_stats_refusal_and_preserve_priority_reserve()
    {
        using NodeStatsManager stats = new(Substitute.For<Nethermind.Core.Timers.ITimerFactory>(), LimboLogs.Instance, 4);
        await using PersistenceHarness test = new(stats: stats);
        foreach (PublicKey key in new[] { TestItem.PublicKeyA, TestItem.PublicKeyB, TestItem.PublicKeyC }) test.Pool.GetOrAdd(new Node(key, "8.8.8.8", 30303)).IsAwaitingConnection = true;
        Node refused = new(TestItem.PublicKeyD, "8.8.4.4", 30303);
        Node priority = new(TestItem.PublicKeyE, "8.8.4.4", 30303) { IsStatic = true };
        test.Source.AddNode(refused);
        test.Source.AddNode(priority);
        Assert.That(() => test.Pool.TryGet(priority.Id, out _), Is.True.After(2000, 10));
        Assert.That(test.Pool.TryGet(refused.Id, out _), Is.False);

        Node full = new(TestItem.PublicKeyF, "8.8.4.4", 30303) { IsTrusted = true };
        test.Source.AddNode(full);
        test.Source.AddNode(new Node(TestItem.PublicKeyB, "8.8.8.8", 30303));
        Assert.That(() => test.Source.BufferedNodeCount, Is.Zero.After(2000, 10));
        Assert.That(test.Pool.TryGet(full.Id, out _), Is.False);
        test.Pool.TryRemove(TestItem.PublicKeyA, out _);
        Node recovered = new(TestItem.PublicKeys.ElementAt(6), "8.8.4.4", 30303) { IsTrusted = true };
        test.Source.AddNode(recovered);
        Assert.That(() => test.Pool.TryGet(recovered.Id, out _), Is.True.After(2000, 10));

    }

    [Test]
    public async Task Pooled_stats_should_remain_authoritative_during_capacity_pressure_and_cleanup()
    {
        Nethermind.Core.Timers.ITimerFactory timers = Substitute.For<Nethermind.Core.Timers.ITimerFactory>();
        Nethermind.Core.Timers.ITimer timer = Substitute.For<Nethermind.Core.Timers.ITimer>();
        timers.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
        using NodeStatsManager stats = new(timers, LimboLogs.Instance, 3);
        await using PersistenceHarness test = new(stats: stats);
        Node node = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        Peer peer = test.Pool.GetOrAdd(node);
        peer.Stats!.CurrentPersistedNodeReputation = -1000;
        test.Storage.UpdateNode(new NetworkNode(node.Id, node.Host, node.Port, -1000));
        foreach (PublicKey key in TestItem.PublicKeys.Skip(1).Take(10))
            stats.GetOrAdd(new Node(key, "9.9.9.9", 30303));
        timer.Elapsed += Raise.Event();

        stats.ReportFailedValidation(new Node(node.Id, "8.8.4.4", 30303), CompatibilityValidationType.NetworkId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.GetOrAdd(node), Is.SameAs(peer.Stats));
            Assert.That(peer.Stats.FailedCompatibilityValidation, Is.EqualTo(CompatibilityValidationType.NetworkId));
            Assert.That(stats.GetNewPersistedReputation(node), Is.EqualTo(peer.Stats.NewPersistedNodeReputation(DateTime.UtcNow)));
        }


        stats.ReportDisconnect(node, DisconnectType.Remote, DisconnectReason.TooManyPeers);
        await test.Tick();
        Assert.That(test.Storage.GetPersistedNodes().Single().Reputation, Is.EqualTo(-650));
    }

    [Test]
    public async Task Persistence_does_not_insert_stored_outsiders_into_a_full_pool()
    {
        await using PersistenceHarness test = new(new NetworkConfig { MaxCandidatePeerCount = 1 });
        test.Pool.GetOrAdd(new Node(TestItem.PublicKeyA, "8.8.8.8", 30303));
        test.Storage.UpdateNode(new NetworkNode(TestItem.PublicKeyB, "9.9.9.9", 30303));

        await test.Tick();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.Pool.PeerCount, Is.EqualTo(1));
            Assert.That(test.Pool.TryGet(TestItem.PublicKeyB, out _), Is.False);
        }
    }

    [Test]
    public async Task Removed_peer_stays_absent_after_persistence_and_reload([Values] bool inPool, [Values] bool persistenceTick)
    {
        await using PersistenceHarness test = new();
        NetworkNode stored = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        test.Storage.UpdateNode(stored);
        if (inPool) test.Pool.GetOrAdd(stored);

        test.Pool.TryRemove(stored.NodeId, out _);
        if (persistenceTick) await test.Tick();
        else await test.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.Pool.TryGet(stored.NodeId, out _), Is.False);
            Assert.That(test.Storage.GetPersistedNodes(), Is.Empty);
        }
        NetworkStorage reloaded = new(test.Database, LimboLogs.Instance);
        Assert.That(reloaded.GetPersistedNodes(), Is.Empty);
    }

    [Test]
    public async Task New_persistence_requires_outbound_session_and_verified_sync_response([Values] bool outbound, [Values] bool useful)
    {
        await using PersistenceHarness test = new();
        Node node = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        test.AddSessionPeer(node, outbound, useful);

        await test.Tick();

        Assert.That(test.Storage.PersistedNodesCount, Is.EqualTo(outbound && useful ? 1 : 0));
    }

    [Test]
    public async Task Persistence_rejects_outbound_session_for_another_identity()
    {
        await using PersistenceHarness test = new();
        Peer peer = test.AddSessionPeer(new Node(TestItem.PublicKeyA, "8.8.8.8", 30303));
        ((Session)peer.OutSession!).RemoteNodeId = TestItem.PublicKeyB;

        await test.Tick();

        Assert.That(test.Storage.GetPersistedNodes(), Is.Empty);
    }

    [Test]
    public async Task Prior_sync_history_does_not_authorize_a_replacement_session()
    {
        await using PersistenceHarness test = new();
        Node node = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        Peer peer = test.AddSessionPeer(node);
        ((Session)peer.OutSession!).MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "replacement");
        test.AddSessionPeer(node, useful: false);
        Assert.That(test.Stats.GetOrAdd(node).DidEventHappen(NodeStatsEventType.SyncInitCompleted), Is.True);

        await test.Tick();

        Assert.That(test.Storage.GetPersistedNodes(), Is.Empty);
    }

    [TestCase(4, 4)]
    [TestCase(64, 16)]
    public async Task Persistence_limits_include_active_peers_and_mapped_addresses(int totalLimit, int expected)
    {
        await using PersistenceHarness test = new(new NetworkConfig { MaxPersistedPeerCount = totalLimit, PersistedPeerCountCleanupThreshold = totalLimit });
        for (int i = 1; i <= 20; i++)
        {
            using PrivateKey key = new(i.ToString("x64"));
            NetworkNode stored = new(key.PublicKey, i % 2 == 0 ? "8.8.8.8" : "::ffff:8.8.8.8", 30303, i);
            test.Storage.UpdateNode(stored);
            Peer peer = test.Pool.GetOrAdd(stored);
            test.Pool.ActivePeers[stored.NodeId] = peer;
        }

        await test.Tick();

        Assert.That(test.Storage.PersistedNodesCount, Is.EqualTo(expected));
    }

    [Test]
    public async Task Persisted_reputation_averages_loaded_base_once_per_restart([Values(800, 1200)] long initial)
    {
        TestMemDb database = new();
        NetworkStorage seed = new(database, LimboLogs.Instance);
        seed.UpdateNode(new NetworkNode(TestItem.PublicKeyA, "8.8.8.8", 30303, initial));
        long expected = initial;
        for (int cycle = 0; cycle < 2; cycle++)
        {
            await using PersistenceHarness test = new(database: database);
            NodesLoader loader = new(new NetworkConfig(), test.Stats, test.Storage,
                new Enode(TestItem.PublicKeyB, IPAddress.Loopback, 30303), LimboLogs.Instance, new NodesLoaderOptions(), test.AddressPolicy);
            Node node = loader.DiscoverNodes(default).ToBlockingEnumerable().Single();
            test.Pool.GetOrAdd(node);
            test.Stats.GetOrAdd(node).AddNodeStatsDisconnectEvent(DisconnectType.Remote, DisconnectReason.TooManyPeers);
            expected = (expected + StatsParameters.Instance.RemoteDisconnectParams[DisconnectReason.TooManyPeers].ReputationScore) / 2;

            await test.Tick();
            Assert.That(test.Storage.GetPersistedNodes().Single().Reputation, Is.EqualTo(expected));
            await test.Tick();
            Assert.That(test.Storage.GetPersistedNodes().Single().Reputation, Is.EqualTo(expected));
        }
    }

    [TestCase("8.8.8.8", 30303, true, true)]
    [TestCase("8.8.8.8", 30304, true, false)]
    [TestCase("8.8.4.4", 30303, true, false)]
    [TestCase("8.8.8.8", 30303, false, false)]
    [TestCase("2606:4700:4700::1111", 30303, true, false)]
    public async Task Useful_outbound_persistence_preserves_only_verified_matching_enrs(string host, int port, bool verified, bool expectEnr)
    {
        await using PersistenceHarness test = new();
        NodeRecord record = CreateSignedEnr(7, includeIpv6: true);
        Node node = new(TestItem.PublicKeyA, host, port);
        if (verified) Assert.That(node.SetVerifiedEnr(record), Is.True);
        else node.Enr = record;
        test.AddSessionPeer(node);

        await test.Tick();

        NetworkNode stored = test.Storage.GetPersistedNodes().Single();
        Assert.That(stored.IsEnr, Is.EqualTo(expectEnr));
        if (expectEnr) Assert.That(stored.Enr!.ToString(), Is.EqualTo(record.ToString()));
        else Assert.That(stored.Host, Is.EqualTo(host));
    }

    [Test]
    public async Task Hello_cannot_change_the_persisted_outbound_port([Values(0, 30000, 40000)] int advertisedPort)
    {
        await using PersistenceHarness test = new();
        Node node = new(TestItem.PublicKeyB, "35.0.0.1", 30000);
        ProtocolsManagerTests.Context context = ProtocolsManagerTests.When.CreateOutgoingSession(node)
            .Handshake().Init().ReceiveHelloThroughPipeline(advertisedPort).ReceiveStatus().VerifyEthInitialized();
        using Session session = context.CurrentSession;
        try
        {
            session.MarkAuthenticated();
            session.MarkUsefulSyncResponse();
            test.Pool.GetOrAdd(node).OutSession = session;

            await test.Tick();

            NetworkNode stored = test.Storage.GetPersistedNodes().Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stored.Port, Is.EqualTo(30000));
                Assert.That(stored.Host, Is.EqualTo("35.0.0.1"));
            }
        }
        finally
        {
            session.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Remote, "test cleanup");
        }
    }

    internal sealed class PersistenceHarness : IAsyncDisposable
    {
        private readonly ManualTimeProvider _time = new();
        private readonly ObservedStorage _observedStorage;
        private TaskCompletionSource _tick = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _stopped;
        private readonly List<Session> _sessions = [];
        public TestMemDb Database { get; }
        public NetworkStorage Storage { get; }
        public INodeStatsManager Stats { get; }
        public PeerPool Pool { get; }
        public PeerAddressPolicy AddressPolicy { get; }
        public TestNodeSource Source { get; } = new();

        public PersistenceHarness(NetworkConfig? config = null, TestMemDb? database = null, INodeStatsManager? stats = null, IEnode? enode = null)
        {
            Stats = stats ?? new NodeStatsManager(Substitute.For<Nethermind.Core.Timers.ITimerFactory>(), LimboLogs.Instance);
            Database = database ?? new TestMemDb();
            Storage = new NetworkStorage(Database, LimboLogs.Instance);
            _observedStorage = new ObservedStorage(Storage, () => _tick.TrySetResult());
            AddressPolicy = new(new IIPResolver.NethermindIp(IPAddress.Any, enode?.HostIp ?? IPAddress.Loopback),
                new LocalAddressSource(LimboLogs.Instance, () => []));
            Pool = new PeerPool(Source, Stats, _observedStorage, config ?? new NetworkConfig(),
                LimboLogs.Instance, Substitute.For<ITrustedNodesManager>(), _time,
                AddressPolicy);
            Pool.Start();
        }

        public async Task Tick()
        {
            _tick = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _time.AdvanceAndFireTimer(TimeSpan.FromMinutes(1));
            await _tick.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public Peer AddSessionPeer(Node node, bool outbound = true, bool useful = true)
        {
            Peer peer = Pool.GetOrAdd(node);
            Session session = outbound
                ? new Session(30312, node, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance)
                : new Session(30312, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance)
                {
                    RemoteHost = node.Host,
                    RemotePort = node.Port,
                };
            session.Handshake(node.Id);
            session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
            _sessions.Add(session);
            if (outbound) peer.OutSession = session;
            else peer.InSession = session;
            if (useful)
            {
                session.MarkAuthenticated();
                session.MarkUsefulSyncResponse();
                Stats.GetOrAdd(node).AddNodeStatsSyncEvent(NodeStatsEventType.SyncInitCompleted);
            }
            return peer;
        }

        public async ValueTask DisposeAsync()
        {
            await Stop();
            foreach (Session session in _sessions)
            {
                if (!session.IsClosing) session.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Remote, "test cleanup");
                session.Dispose();
            }
            (Stats as IDisposable)?.Dispose();
            AddressPolicy.Dispose();
        }

        public async Task Stop()
        {
            if (_stopped) return;
            _stopped = true;
            await Pool.StopAsync();
        }

        internal sealed class ObservedStorage(INetworkStorage storage, Action tickCompleted) : INetworkStorage
        {
            public NetworkNode[] GetPersistedNodes() => storage.GetPersistedNodes();
            public int PersistedNodesCount => storage.PersistedNodesCount;
            public void UpdateNode(NetworkNode node) => storage.UpdateNode(node);
            public void UpdateNodes(IEnumerable<NetworkNode> nodes) => storage.UpdateNodes(nodes);
            public void RemoveNode(PublicKey nodeId) => storage.RemoveNode(nodeId);
            public void Commit() => storage.Commit();
            public void StartBatch()
            {
                storage.StartBatch();
                tickCompleted();
            }

            public bool AnyPendingChange()
            {
                bool pending = storage.AnyPendingChange();
                if (!pending) tickCompleted();
                return pending;
            }
        }
    }

    [Test]
    public async Task Pooled_peer_keeps_its_stats_until_removal()
    {
        using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 1);
        await using PersistenceHarness context = new(stats: stats);
        PeerPool pool = context.Pool;
        Node first = new(TestItem.PublicKeyA, "192.0.2.1", 30303);
        Node second = new(TestItem.PublicKeyB, "192.0.2.2", 30303);
        try
        {
            Peer peer = pool.GetOrAdd(first);
            peer.IsAwaitingConnection = true;
            pool.GetOrAdd(second);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.TryGet(second.Id, out _), Is.False);
                Assert.That(stats.GetOrAdd(first), Is.SameAs(peer.Stats));
            }
            peer.IsAwaitingConnection = false;
            Peer replacement = pool.GetOrAdd(second);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.TryGet(first.Id, out _), Is.False);
                Assert.That(pool.Peers[second.Id], Is.SameAs(replacement));
            }
            Peer readmitted = pool.GetOrAdd(first);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.TryRemoveInactive(peer, ordinaryCandidateOnly: false), Is.False);
                Assert.That(pool.Peers[first.Id], Is.SameAs(readmitted));
            }
        }
        finally { await pool.StopAsync(); }
    }

    [Test]
    public async Task PeerPool_ShouldThrottleSource_WhenFull()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();

        TestNodeSource nodeSource = new();
        TestLogger logger = new();
        FirstTwoDelaysImmediateTimeProvider timeProvider = new();
        using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance);
        PeerPool pool = new(
            nodeSource,
            stats,
            new NetworkStorage(new TestMemDb(), LimboLogs.Instance),
            new NetworkConfig()
            {
                MaxActivePeers = 5,
                MaxCandidatePeerCount = 10
            },
            new OneLoggerLogManager(new ILogger(logger)),
            trustedNodesManager,
            timeProvider, OwnPolicy(new PeerAddressPolicy(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Loopback), new LocalAddressSource(LimboLogs.Instance, () => []))));

        Random rand = new(0);
        PrivateKeyGenerator keyGen = new(new TestRandom((m) => rand.Next(m), (s) =>
        {
            byte[] buffer = new byte[s];
            rand.NextBytes(buffer);
            return buffer;
        }));

        for (int i = 0; i < 5; i++)
        {
            PublicKey key = keyGen.Generate().PublicKey;
            Node node = new(key, "1.2.3.4", 1234);
            Peer peer = pool.GetOrAdd(node);
            pool.ActivePeers[key] = peer;
        }

        pool.Start();

        for (int i = 0; i < 10; i++)
        {
            PublicKey key = keyGen.Generate().PublicKey;
            Node node = new(key, "1.2.3.4", 1234);
            nodeSource.AddNode(node);
        }

        try
        {
            Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(10).After(100, 10));
            await timeProvider.SecondDelayRequested.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(logger.LogList, Has.Exactly(1).EqualTo("Peer cleanup threshold reached. Throttling discovery."));
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_RunPeerCommit_ShouldContinueAfterNoPendingChange()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        TestNetworkStorage storage = new();
        NetworkConfig networkConfig = new()
        {
            PeersPersistenceInterval = 50,
            MaxActivePeers = 0,
            MaxCandidatePeerCount = 0
        };

        PeerPool pool = new(nodeSource, stats, storage, networkConfig, LimboLogs.Instance, trustedNodesManager,
            TimeProvider.System, OwnPolicy(new PeerAddressPolicy(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Loopback), new LocalAddressSource(LimboLogs.Instance, () => []))));

        storage.Pending = false;
        pool.Start();

        try
        {
            // allow a couple of ticks with no pending changes
            await Task.Delay(200);
            Assert.That(storage.CommitCount, Is.EqualTo(0));

            // now flip to pending and expect a commit soon
            storage.Pending = true;
            Assert.That(() => storage.CommitCount, Is.AtLeast(1).After(2000, 10));

            // StartBatch should be called once in ctor and once after commit
            Assert.That(() => storage.StartBatchCount, Is.AtLeast(2).After(2000, 10));
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_ShouldIgnoreNodeRemoved_AfterStop()
    {
        ConfigProvider configProvider = new();
        ChainSpec spec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("chainspec/foundation.json");
        spec.Bootnodes = [];

        TestNodeSource nodeSource = new();
        await using IContainer container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(spec, configProvider, new TestLogManager()))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, nameof(PeerPoolTests)))
            .AddSingleton(nodeSource)
            .Bind<INodeSource, TestNodeSource>()
            .Build();

        IPeerPool pool = container.Resolve<IPeerPool>();
        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 1234);

        pool.Start();
        pool.GetOrAdd(node);
        await pool.StopAsync();

        nodeSource.RemoveNode(node);

        Assert.That(pool.TryGet(node.Id, out _), Is.True);
    }

    [Test]
    public async Task PeerPool_ShouldThrottleSource_WhenCandidatePoolIsFull()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 1);

        pool.GetOrAdd(new Node(TestItem.PublicKeyA, "1.2.3.4", 1234));
        pool.Start();
        nodeSource.AddNode(new Node(TestItem.PublicKeyB, "1.2.3.5", 1234));

        try
        {
            Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(1).After(100, 10));
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_ShouldThrottleSource_WhenActivePeerPoolIsFull()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 1, maxCandidatePeerCount: 10);

        Peer activePeer = pool.GetOrAdd(new Node(TestItem.PublicKeyA, "1.2.3.4", 1234));
        pool.ActivePeers[TestItem.PublicKeyA] = activePeer;
        pool.Start();
        nodeSource.AddNode(new Node(TestItem.PublicKeyB, "1.2.3.5", 1234));

        try
        {
            Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(1).After(100, 10));
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_ShouldNotThrottleStaticNode_WhenFull()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 1, maxCandidatePeerCount: 1);

        Peer activePeer = pool.GetOrAdd(new Node(TestItem.PublicKeyA, "1.2.3.4", 1234));
        pool.ActivePeers[TestItem.PublicKeyA] = activePeer;
        pool.Start();

        Node staticNode = new(TestItem.PublicKeyB, "1.2.3.5", 1234) { IsStatic = true };
        nodeSource.AddNode(staticNode);

        try
        {
            // Static node bypasses throttling: consumed and registered even though the pool is full.
            Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(0).After(100, 10));
            Assert.That(pool.TryGet(staticNode.Id, out _), Is.True);
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task Static_and_trusted_nodes_reach_the_pool_once_with_their_flags()
    {
        using TempPath staticFile = TempPath.GetTempFile();
        using TempPath trustedFile = TempPath.GetTempFile();
        await File.WriteAllTextAsync(staticFile.Path, $"[\"{EnodeAt(0)}\"]");
        await File.WriteAllTextAsync(trustedFile.Path, $"[\"{EnodeAt(1)}\"]");
        StaticNodesManager staticNodes = new(staticFile.Path, LimboLogs.Instance);
        TrustedNodesManager trustedNodes = new(trustedFile.Path, LimboLogs.Instance);
        await staticNodes.InitAsync();
        await trustedNodes.InitAsync();
        using CompositeNodeSource nodeSource = new(staticNodes, trustedNodes);
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodes, maxActivePeers: 10, maxCandidatePeerCount: 10);
        ConcurrentQueue<PublicKey> peersAdded = new();
        pool.PeerAdded += (_, e) => peersAdded.Enqueue(e.Peer.Node.Id);

        pool.Start();
        try
        {
            await staticNodes.AddAsync(new NetworkNode(EnodeAt(2)), updateFile: false);
            await trustedNodes.AddAsync(EnodeAt(3), updateFile: false);

            Assert.That(() => pool.PeerCount, Is.EqualTo(4).After(5000, 10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peersAdded, Is.EquivalentTo(new[] { TestItem.PublicKeys[0], TestItem.PublicKeys[1], TestItem.PublicKeys[2], TestItem.PublicKeys[3] }), "each file node and each runtime add joins the pool once");
                Assert.That(pool.StaticPeers.Select(static p => p.Node.Id), Is.EquivalentTo(new[] { TestItem.PublicKeys[0], TestItem.PublicKeys[2] }));
                Assert.That(pool.Peers.Where(static kvp => kvp.Value.Node.IsTrusted).Select(static kvp => kvp.Value.Node.Id), Is.EquivalentTo(new[] { TestItem.PublicKeys[1], TestItem.PublicKeys[3] }));
            }
        }
        finally
        {
            await pool.StopAsync();
        }

        static Enode EnodeAt(int i) => new(TestItem.PublicKeys[i], IPAddress.Loopback, 30303 + i);
    }

    [Test]
    public void PeerPool_DiscoveryEviction([Values] bool hasSession, [Values(0, 1, 2)] int privilege, [Values] bool explicitRemoval)
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        INodeSource nodeSource = Substitute.For<INodeSource>();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 1234) { IsStatic = privilege == 1, IsTrusted = privilege == 2 };
        Peer peer = pool.GetOrAdd(node);
        if (hasSession) peer.InSession = Substitute.For<ISession>();

        NodeEventArgs removal = explicitRemoval ? new ExplicitNodeRemovalEventArgs(node) : new NodeEventArgs(node);
        nodeSource.NodeRemoved += Raise.EventWith(nodeSource, removal);

        bool expectPresent = !explicitRemoval && (hasSession || privilege != 0);
        Assert.That(pool.TryGet(node.Id, out Peer retained), Is.EqualTo(expectPresent));
        if (expectPresent)
        {
            Assert.That(retained, Is.SameAs(peer));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retained.Node.IsStatic, Is.EqualTo(privilege == 1));
                Assert.That(retained.Node.IsTrusted, Is.EqualTo(privilege == 2));
            }
        }
    }

    [Test]
    public void PeerPool_Replace_DoesNotInheritStaticFlag()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        Node oldNode = new(TestItem.PublicKeyA, "1.2.3.4", 1234) { IsStatic = true };
        Peer oldPeer = pool.GetOrAdd(oldNode);
        ISession session = Substitute.For<ISession>();
        session.Direction.Returns(ConnectionDirection.Out);
        session.ObsoleteRemoteNodeId.Returns(TestItem.PublicKeyA);
        session.Node.Returns(new Node(TestItem.PublicKeyB, "1.2.3.5", 1234));
        oldPeer.OutSession = session;

        Peer replacedPeer = pool.Replace(session);

        Assert.That(replacedPeer.Node.IsStatic, Is.False);
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public void GetOrAdd_PromotesElevatedFlagsOntoAnAlreadyPooledPeer(bool isStatic, bool isTrusted, bool isBootnode)
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        Node persistedNode = new(TestItem.PublicKeyA, "1.2.3.4", 1234);
        Peer pooled = pool.GetOrAdd(persistedNode);
        Assert.That(pool.StaticPeers, Is.Empty);

        Node configuredNode = new(TestItem.PublicKeyA, "1.2.3.4", 1234)
        {
            IsStatic = isStatic,
            IsTrusted = isTrusted,
            IsBootnode = isBootnode
        };
        Peer resolved = pool.GetOrAdd(configuredNode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.SameAs(pooled));
            Assert.That(pooled.Node.IsStatic, Is.EqualTo(isStatic),
                "a peer that was already pooled from the persisted peers db must gain the elevated status of a later arrival, or it is never treated as such for the whole session");
            Assert.That(pooled.Node.IsTrusted, Is.EqualTo(isTrusted));
            Assert.That(pooled.Node.IsBootnode, Is.EqualTo(isBootnode));
            Assert.That(pool.StaticPeers, isStatic ? Has.Exactly(1).Items : Is.Empty);
        }
    }

    [Test]
    public void GetOrAdd_DoesNotClearElevatedFlagsWhenAPlainArrivalFollows()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        Node staticNode = new(TestItem.PublicKeyA, "1.2.3.4", 1234) { IsStatic = true };
        Peer pooled = pool.GetOrAdd(staticNode);

        Node plainNode = new(TestItem.PublicKeyA, "1.2.3.4", 1234);
        Peer resolved = pool.GetOrAdd(plainNode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.SameAs(pooled));
            Assert.That(pooled.Node.IsStatic, Is.True,
                "promotion is monotonic: a later plain arrival of the same node id must never demote the pooled peer");
        }
    }

    [Test]
    public void GetOrAdd_MergesVerifiedEnrStateIntoPersistedPeer()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);
        Node persistedNode = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30303);
        Peer pooled = pool.GetOrAdd(persistedNode);
        NodeRecord dualStackRecord = CreateSignedEnr(enrSequence: 0, includeIpv6: true);
#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(Node.TryFromEnr(dualStackRecord, out Node? discoveredNode), Is.True);
#pragma warning restore NUnit2045
        Assert.That(discoveredNode!.SetVerifiedEnr(dualStackRecord), Is.True);

        Peer resolved = pool.GetOrAdd(discoveredNode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.SameAs(pooled));
            Assert.That(pooled.Node.Address, Is.EqualTo(persistedNode.Address), "merging ENR state must not replace the pooled dial endpoint");
            Assert.That(pooled.Node.Enr, Is.SameAs(dualStackRecord));
            Assert.That(pooled.Node.IsVerifiedEnr(dualStackRecord), Is.True);
            Assert.That(dualStackRecord.TryGetTcpEndpoint(AddressFamily.InterNetworkV6, out _), Is.True);
        }
    }

    [Test]
    public void GetOrAdd_UsesCanonicalVerifiedSequenceAndWithdrawalRules()
    {
        PeerPool pool = CreatePeerPool(
            new TestNodeSource(),
            Substitute.For<ITrustedNodesManager>(),
            maxActivePeers: 10,
            maxCandidatePeerCount: 10);
        Node persistedNode = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30303);
        Node originalAlias = pool.GetOrAdd(persistedNode).Node;
        NodeRecord sequenceZero = CreateSignedEnr(enrSequence: 0, includeIpv6: true);
        pool.GetOrAdd(CreateEnrNode(sequenceZero, verified: true));

        NodeRecord withdrawal = CreateSignedEnr(enrSequence: 1, includeIpv6: false);
        pool.GetOrAdd(CreateEnrNode(withdrawal, verified: true));
        pool.GetOrAdd(CreateEnrNode(sequenceZero, verified: true));
        NodeRecord sameSequenceReplay = CreateSignedEnr(enrSequence: 1, includeIpv6: true);
        pool.GetOrAdd(CreateEnrNode(sameSequenceReplay, verified: true));
        NodeRecord unverifiedNewer = CreateSignedEnr(enrSequence: 2, includeIpv6: true);
        pool.GetOrAdd(CreateEnrNode(unverifiedNewer, verified: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(originalAlias.Address, Is.EqualTo(persistedNode.Address));
            Assert.That(originalAlias.Enr, Is.SameAs(withdrawal));
            Assert.That(originalAlias.IsVerifiedEnr(withdrawal), Is.True);
            Assert.That(originalAlias.HighestObservedEnrSequence, Is.EqualTo(1));
            Assert.That(withdrawal.TryGetTcpEndpoint(AddressFamily.InterNetworkV6, out _), Is.False,
                "a newer verified withdrawal must not be undone by same/older or merely decoded records");
        }
    }

    [TestCase(null, false, 4)]
    [TestCase(2, true, 4)]
    [TestCase(8, false, 4)]
    [TestCase(4, true, 4)]
    [TestCase(5, true, 5)]
    [TestCase(2, true, 4, 8)]
    public void GetOrAdd_RestoresPersistedEnrAndMergesItIntoExistingPeer(int? currentSequence, bool verified, int expectedSequence, int observedSequence = 0)
    {
        PeerPool pool = CreatePeerPool(
            new TestNodeSource(),
            Substitute.For<ITrustedNodesManager>(),
            maxActivePeers: 10,
            maxCandidatePeerCount: 10);
        NodeRecord persistedRecord = CreateSignedEnr(enrSequence: 4, includeIpv6: true);
        Peer existing = pool.GetOrAdd(new Node(TestItem.PublicKeyA, "8.8.8.8", 30303));
        if (currentSequence is int sequence)
        {
            pool.GetOrAdd(CreateEnrNode(CreateSignedEnr((ulong)sequence, includeIpv6: false), verified));
        }
        existing.Node.ObserveEnrSequence((ulong)observedSequence);

        Peer restored = pool.GetOrAdd(new NetworkNode(persistedRecord.ToString()));

        Assert.That(existing.Node.Enr, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored, Is.SameAs(existing));
            Assert.That(existing.Node.Enr!.EnrSequence, Is.EqualTo(expectedSequence));
            Assert.That(existing.Node.IsVerifiedEnr(existing.Node.Enr), Is.True);
            Assert.That(existing.Node.HighestObservedEnrSequence, Is.EqualTo(Math.Max(expectedSequence, observedSequence)));
        }
    }

    [Test]
    public void GetOrAdd_ExistingNetworkNodeDoesNotAllocate([Values] bool enr)
    {
        PeerPool pool = CreatePeerPool(
            new TestNodeSource(),
            new TrustedNodesManager("trusted-nodes.json", LimboLogs.Instance),
            maxActivePeers: 10,
            maxCandidatePeerCount: 10);
        NetworkNode networkNode = enr
            ? new NetworkNode(CreateSignedEnr(4, includeIpv6: true).ToString())
            : new NetworkNode(TestItem.PublicKeyA, "8.8.8.8", 30303);
        Peer existing = pool.GetOrAdd(networkNode);
        for (int i = 0; i < 1000; i++) pool.GetOrAdd(networkNode);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) pool.GetOrAdd(networkNode);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocated, Is.Zero);
            Assert.That(pool.GetOrAdd(networkNode), Is.SameAs(existing));
        }
    }

    [Test]
    public async Task GetOrAdd_ConcurrentAliasesConvergeOnNewestVerifiedEnr()
    {
        PeerPool pool = CreatePeerPool(
            new TestNodeSource(),
            Substitute.For<ITrustedNodesManager>(),
            maxActivePeers: 10,
            maxCandidatePeerCount: 10);
        Node[] aliases = new Node[16];
        for (int i = 0; i < aliases.Length; i++)
        {
            aliases[i] = CreateEnrNode(CreateSignedEnr((ulong)i, includeIpv6: i % 2 == 1), verified: true);
        }

        await Task.WhenAll(Array.ConvertAll(aliases, node => Task.Run(() => pool.GetOrAdd(node))));
        Node pooled = pool.Peers[TestItem.PublicKeyA].Node;

        Assert.That(pooled.Enr, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pooled.Enr!.EnrSequence, Is.EqualTo(15));
            Assert.That(pooled.HighestObservedEnrSequence, Is.EqualTo(15));
            Assert.That(pooled.IsVerifiedEnr(pooled.Enr), Is.True);
            Assert.That(aliases, Has.All.Matches<Node>(node => ReferenceEquals(node.Enr, pooled.Enr)));
        }
    }

    [Test]
    public void GetOrAdd_NetworkNode_sets_trusted_flag_from_manager()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        trustedNodesManager.IsTrusted(Arg.Any<Enode>()).Returns(true);
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        string enode = new Enode(TestItem.PublicKeyA, IPAddress.Parse("1.2.3.4"), 30303).ToString();
        Peer peer = pool.GetOrAdd(new NetworkNode(enode));

        Assert.That(peer.Node.IsTrusted, Is.True, "GetOrAdd(NetworkNode) marks trusted via the manager");
    }

    [Test]
    public void GetOrAdd_NetworkNode_refreshes_the_trusted_flag_of_an_already_pooled_peer()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        trustedNodesManager.IsTrusted(Arg.Any<Enode>()).Returns(false);
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        string enode = new Enode(TestItem.PublicKeyA, IPAddress.Parse("1.2.3.4"), 30303).ToString();
        Peer pooled = pool.GetOrAdd(new NetworkNode(enode));
        Assert.That(pooled.Node.IsTrusted, Is.False);

        trustedNodesManager.IsTrusted(Arg.Any<Enode>()).Returns(true);
        Peer resolved = pool.GetOrAdd(new NetworkNode(enode));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.SameAs(pooled));
            Assert.That(pooled.Node.IsTrusted, Is.True,
                "a peer trusted after it was pooled must gain the flag, or admin_addTrustedPeer never takes effect for an already known peer");
        }
    }

    [Test]
    public void GetOrAdd_NetworkNode_refreshes_the_trusted_flag_of_an_enr_backed_pooled_peer()
    {
        ITrustedNodesManager trustedNodesManager = new TrustedNodesManager("trusted-nodes.json", LimboLogs.Instance);
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        NetworkNode enrNode = new(TestEnrString);
        Peer pooled = pool.GetOrAdd(new Node(enrNode.NodeId, "1.2.3.4", 1234));

        Peer resolved = pool.GetOrAdd(enrNode);

        Assert.That(resolved, Is.SameAs(pooled),
            "an ENR has no enode representation, so the trusted refresh must not dereference it - a throw here aborts the whole persistence tick and skips the pending commit");
    }

    private const string TestEnrString =
        "enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOo" +
        "nrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPK" +
        "Y0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8";

    private static NodeRecord CreateSignedEnr(ulong enrSequence, bool includeIpv6)
    {
        NodeRecord record = new() { EnrSequence = enrSequence };
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new IpEntry(IPAddress.Parse("8.8.8.8")));
        record.SetEntry(new TcpEntry(30303));
        record.SetEntry(new UdpEntry(30303));
        if (includeIpv6)
        {
            record.SetEntry(new Ip6Entry(IPAddress.Parse("2606:4700:4700::1111")));
            record.SetEntry(new Tcp6Entry(30304));
            record.SetEntry(new Udp6Entry(30304));
        }

        new NodeRecordSigner(new EthereumEcdsa(0), TestItem.PrivateKeyA).Sign(record);
        return record;
    }

    private static Node CreateEnrNode(NodeRecord record, bool verified)
    {
        Assert.That(Node.TryFromEnr(record, out Node? node), Is.True);
        if (verified)
        {
            Assert.That(node!.SetVerifiedEnr(record), Is.True);
        }

        return node!;
    }

    private sealed class FirstTwoDelaysImmediateTimeProvider : TimeProvider
    {
        private readonly TaskCompletionSource _secondDelayRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _delayCount;

        public Task SecondDelayRequested => _secondDelayRequested.Task;

        public override System.Threading.ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            int delayCount = Interlocked.Increment(ref _delayCount);
            if (delayCount == 2) _secondDelayRequested.TrySetResult();
            return delayCount <= 2
                ? new ImmediateTimer(callback, state, dueTime, period)
                : TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }

        private sealed class ImmediateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : System.Threading.ITimer
        {
            private readonly System.Threading.ITimer _timer =
                TimeProvider.System.CreateTimer(callback, state, GetDueTime(dueTime), period);

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                _timer.Change(GetDueTime(dueTime), period);

            public void Dispose() => _timer.Dispose();

            public ValueTask DisposeAsync() => _timer.DisposeAsync();

            private static TimeSpan GetDueTime(TimeSpan dueTime) =>
                dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero;
        }
    }

    private PeerPool CreatePeerPool(INodeSource nodeSource, ITrustedNodesManager trustedNodesManager, int maxActivePeers, int maxCandidatePeerCount) => new(
            nodeSource,
            new NodeStatsManager(TimerFactory.Default, LimboLogs.Instance),
            new NetworkStorage(new TestMemDb(), LimboLogs.Instance),
            new NetworkConfig
            {
                MaxActivePeers = maxActivePeers,
                MaxCandidatePeerCount = maxCandidatePeerCount
            },
            LimboLogs.Instance,
            trustedNodesManager, TimeProvider.System, OwnPolicy(new PeerAddressPolicy(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Loopback), new LocalAddressSource(LimboLogs.Instance, () => []))));

    private sealed class TestNetworkStorage : INetworkStorage
    {
        public volatile bool Pending;
        public int CommitCount { get; private set; }
        public int StartBatchCount { get; private set; }

        public NetworkNode[] GetPersistedNodes() => Array.Empty<NetworkNode>();
        public int PersistedNodesCount => 0;
        public void UpdateNode(NetworkNode node) => Pending = true;
        public void UpdateNodes(IEnumerable<NetworkNode> nodes) => Pending = true;
        public void RemoveNode(PublicKey nodeId) => Pending = true;
        public void StartBatch() { Interlocked.Increment(ref _startBatchCountBacking); StartBatchCount = _startBatchCountBacking; }
        public void Commit() { Interlocked.Increment(ref _commitCountBacking); CommitCount = _commitCountBacking; }
        public bool AnyPendingChange() => Pending;

        private int _commitCountBacking;
        private int _startBatchCountBacking;
    }

    [Test]
    public async Task Shared_policy_pool_handles_candidate_lifecycle()
    {
        INodeSource source = Substitute.For<INodeSource>();
        using NodeStatsManager stats = new(Substitute.For<ITimerFactory>(), LimboLogs.Instance);
        INetworkStorage storage = Substitute.For<INetworkStorage>();
        using PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback),
            new LocalAddressSource(LimboLogs.Instance, () => []));
        Assert.Throws<ArgumentNullException>(() => new PeerPool(source, stats, storage, new NetworkConfig(),
            LimboLogs.Instance, Substitute.For<ITrustedNodesManager>(), TimeProvider.System, null!));
        PeerPool pool = new(source, stats, storage, new NetworkConfig(), LimboLogs.Instance,
            Substitute.For<ITrustedNodesManager>(), TimeProvider.System, policy);
        try
        {
            Node node = new(TestItem.PublicKeyB, "8.8.4.4", 30303);
            Peer added = pool.GetOrAdd(node);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.TryGet(node.Id, out Peer? retained), Is.True);
                Assert.That(retained, Is.SameAs(added));
            }
            source.NodeRemoved += Raise.EventWith(new NodeEventArgs(node));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.TryGet(node.Id, out _), Is.False);
                Assert.That(pool.GetOrAdd(node).Node.Id, Is.EqualTo(node.Id));
            }
        }
        finally
        {
            await pool.StopAsync();
        }
    }
}
