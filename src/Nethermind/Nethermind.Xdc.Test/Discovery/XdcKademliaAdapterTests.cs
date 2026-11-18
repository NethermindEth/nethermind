// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Xdc.Discovery;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Discovery;

[TestFixture, Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class XdcKademliaAdapterTests
{
    private PeerAddressPolicy _addressPolicy = null!;
    private const int RequestTimeoutMs = 10_000;

    private IKademlia<PublicKey, Node> _kademliaMessageReceiver = null!;
    private INodeHealthTracker<Node> _nodeHealthTracker = null!;
    private KademliaConfig<Node> _kademliaConfig = null!;
    private ITimestamper _timestamper = null!;
    private IMsgSender _msgSender = null!;
    private INodeStatsManager _nodeStatsManager = null!;
    private INodeRecordProvider _nodeRecordProvider = null!;
    private IMessageSerializationService _receiverSerializationManager = null!;
    private Node _receiver = null!;
    private XdcKademliaAdapter _adapter = null!;

    [SetUp]
    public void Setup()
    {
        Node currentNode = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        _kademliaMessageReceiver = Substitute.For<IKademlia<PublicKey, Node>>();
        _nodeHealthTracker = Substitute.For<INodeHealthTracker<Node>>();
        _kademliaConfig = new KademliaConfig<Node> { CurrentNodeId = currentNode };

        _timestamper = Substitute.For<ITimestamper>();
        DateTime now = new(2021, 5, 3, 0, 0, 0, DateTimeKind.Utc);
        _timestamper.UtcNow.Returns(now);
        _timestamper.UnixTime.Returns(new UnixTime(now));

        _msgSender = Substitute.For<IMsgSender>();
        _msgSender.SendMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);

        _receiver = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
        SerializationBuilder builder = new();
        builder.WithDiscovery(TestItem.PrivateKeyB);
        _receiverSerializationManager = builder.TestObject;

        _nodeRecordProvider = Substitute.For<INodeRecordProvider>();
        _nodeRecordProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<NodeRecord>(new NodeRecord()));
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Loopback)));
        NetworkListenerState listenerState = new(new NetworkConfig { LocalIp = "0.0.0.0" }, ipResolver, LimboLogs.Instance);
        listenerState.SetDiscoveryAddress(IPAddress.Any);
        listenerState.SetRlpxAddress(IPAddress.Any);
        _nodeStatsManager = Substitute.For<INodeStatsManager>();
        _nodeStatsManager.GetStatsOrNull(Arg.Any<Node>()).Returns(Substitute.For<INodeStats>());

        _addressPolicy = new(ipResolver, new LocalAddressSource(LimboLogs.Instance, () => []));
        _adapter = new XdcKademliaAdapter(
            new Lazy<IKademlia<PublicKey, Node>>(() => _kademliaMessageReceiver),
            Substitute.For<IRoutingTable<Node, ValueHash256>>(),
            new Lazy<INodeHealthTracker<Node>>(() => _nodeHealthTracker),
            new DiscoveryConfig
            {
                EnrTimeout = RequestTimeoutMs,
                PingTimeout = RequestTimeoutMs,
                SendNodeTimeout = RequestTimeoutMs,
                BondWaitTime = 1,
            },
            _kademliaConfig,
            _nodeRecordProvider,
            ipResolver,
            _nodeStatsManager,
            _timestamper,
            Substitute.For<IProcessExitSource>(),
            new Ecdsa(),
            LimboLogs.Instance,
            listenerState, _addressPolicy)
        {
            MsgSender = _msgSender,
        };
    }

    [TearDown]
    public async Task TearDown()
    {
        await _adapter.DisposeAsync();
        _addressPolicy.Dispose();
    }

    private void ConfigureBondCallback(ulong? pongEnrSequence) =>
        _msgSender
            .SendMsg(Arg.Any<PingMsg>())
            .Returns(ci =>
            {
                PingMsg sent = (PingMsg)ci[0]!;
                byte[] buffer = _receiverSerializationManager.SerializeToArray(sent);
                PingMsg msg = _receiverSerializationManager.Deserialize<PingMsg>(buffer);
                PongMsg pong = new(msg.FarPublicKey!, _timestamper.UnixTime.SecondsLong + 1, sent.Mdc!.Value, pongEnrSequence);
                pong.FarAddress = sent.FarAddress;
                return _adapter.OnIncomingMsg(pong);
            });

    [Test]
    [CancelAfter(10000)]
    public async Task Production_xdc_app_and_adapter_use_the_root_address_policy(
        [Values] bool allowPrivate, CancellationToken token)
    {
        IIPResolver policyResolver = Substitute.For<IIPResolver>();
        policyResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse(allowPrivate ? "192.168.1.1" : "8.8.8.8"))));
        PeerAddressPolicy policy = new(policyResolver, new LocalAddressSource(LimboLogs.Instance));
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse(allowPrivate ? "8.8.8.8" : "192.168.1.1"))));
        IKademlia<PublicKey, Node>? kademlia = null;
        IKademliaAdapter? capturedAdapter = null;
        using XdcTestBlockchain chain = await XdcTestBlockchain.Create(0, configurer: builder =>
        {
            builder.RegisterInstance(new XdcChainSpecEngineParameters { SwitchBlock = 1 });
            builder.RegisterInstance(resolver).As<IIPResolver>();
            builder.RegisterInstance(policy);
            builder.RegisterInstance(_timestamper);
            builder.RegisterInstance<IEnode>(new Enode(TestItem.PublicKeyA, IPAddress.Parse("8.8.8.8"), 30303));
            builder.RegisterDecorator<IKademlia<PublicKey, Node>>((_, _, inner) => kademlia = inner);
            builder.RegisterDecorator<IKademliaAdapter>((_, _, inner) => capturedAdapter = inner);
        });
        chain.Container.Resolve<NetworkListenerState>().SetDiscoveryAddress(IPAddress.Any);
        await using DiscoveryApp app = chain.Container.Resolve<DiscoveryApp>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(app, Is.TypeOf<XdcDiscoveryApp>());
            Assert.That(chain.Container.Resolve<PeerAddressPolicy>(), Is.SameAs(policy));
        }
        Node privateNode = new(TestItem.PublicKeyD, "10.1.2.3", 30303);
        Node publicNode = new(TestItem.PublicKeyE, "9.9.9.9", 30303);
        app.AddNodeToDiscovery(privateNode);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(kademlia!.IterateNodes().Any(node => node.Id == privateNode.Id), Is.EqualTo(allowPrivate));

            Assert.That(capturedAdapter, Is.TypeOf<XdcKademliaAdapter>());
        }
        XdcKademliaAdapter adapter = (XdcKademliaAdapter)capturedAdapter!;
        adapter.MsgSender = _msgSender;
        _msgSender.SendMsg(Arg.Any<PingMsg>()).Returns(call =>
        {
            PingMsg sent = call.Arg<PingMsg>();
            byte[] buffer = _receiverSerializationManager.SerializeToArray(sent);
            PingMsg ping = _receiverSerializationManager.Deserialize<PingMsg>(buffer);
            PongMsg pong = new(ping.FarPublicKey!, _timestamper.UnixTime.SecondsLong + 1, sent.Mdc!.Value)
            {
                FarAddress = sent.FarAddress
            };
            return adapter.OnIncomingMsg(pong);
        });
        _msgSender.SendMsg(Arg.Any<FindNodeMsg>()).Returns(_ => adapter.OnIncomingMsg(
            new NeighborsMsg(_receiver.Id, _timestamper.UnixTime.SecondsLong + 1, new[] { privateNode, publicNode })
            {
                FarAddress = _receiver.DiscoveryAddress
            }));
        Node[]? neighbors = await adapter.FindNeighbours(_receiver, TestItem.PublicKeyC, token);
        Assert.That(neighbors?.Select(node => node.Id), Is.EquivalentTo(allowPrivate
            ? new[] { privateNode.Id, publicNode.Id } : new[] { publicNode.Id }));
    }

    [Test]
    [CancelAfter(20000)]
    public async Task Production_xdc_answered_request_expires_offline_startup_rows([Values] bool answered, CancellationToken token)
    {
        using MemDb db = new();
        CommitObservedStorage storage = new(new NetworkStorage(db, LimboLogs.Instance));
        NetworkNode stored = new(TestItem.PublicKeyD, "8.8.4.1", 30303, 0);
        storage.UpdateNode(stored);
        storage.Commit();
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("8.8.8.8"))));
        using PeerAddressPolicy policy = new(resolver, new LocalAddressSource(LimboLogs.Instance, () => []));
        IKademliaAdapter? capturedAdapter = null;
        ForwardingAdapter? decoratedAdapter = null;
        DiscoveryPersistenceManager? capturedPersistence = null;
        using XdcTestBlockchain chain = await XdcTestBlockchain.Create(0, configurer: builder =>
        {
            builder.RegisterInstance(new XdcChainSpecEngineParameters { SwitchBlock = 1 });
            builder.RegisterInstance(resolver).As<IIPResolver>();
            builder.RegisterInstance(policy);
            builder.RegisterInstance(_timestamper);
            builder.RegisterInstance<IDiscoveryConfig>(new DiscoveryConfig { DiscoveryPersistenceInterval = 100 });
            builder.RegisterInstance<IEnode>(new Enode(TestItem.PublicKeyA, IPAddress.Parse("8.8.8.8"), 30303));
            builder.RegisterInstance(storage).Keyed<INetworkStorage>(DbNames.DiscoveryNodes).ExternallyOwned();
            builder.RegisterDecorator<IKademliaAdapter>((_, _, inner) =>
            {
                capturedAdapter = inner;
                return decoratedAdapter = new ForwardingAdapter(inner);
            });
            builder.RegisterDecorator<DiscoveryPersistenceManager>((_, _, inner) => capturedPersistence = inner);
        });
        chain.Container.Resolve<NetworkListenerState>().SetDiscoveryAddress(IPAddress.Any);
        await using DiscoveryApp app = chain.Container.Resolve<DiscoveryApp>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(app, Is.TypeOf<XdcDiscoveryApp>());
            Assert.That(capturedAdapter, Is.TypeOf<XdcKademliaAdapter>());
            Assert.That(capturedPersistence, Is.Not.Null);
            Assert.That(decoratedAdapter, Is.Not.Null);
        }
        DiscoveryPersistenceManager persistence = capturedPersistence!;
        IKademliaAdapter effectiveAdapter = decoratedAdapter!;
        effectiveAdapter.MsgSender = _msgSender;
        _msgSender.SendMsg(Arg.Any<DiscoveryMsg>()).Returns(_ =>
            Task.FromException(new KademliaRequestNotSentException()));
        await persistence.LoadPersistedNodes(token);
        storage.DrainCommits();
        using CancellationTokenSource offlineLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task offlineCommit = persistence.RunDiscoveryPersistenceCommit(offlineLifetime.Token);
        try
        {
            await storage.NextCommit(token);
            Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId), Is.EqualTo(new[] { stored.NodeId }));
        }
        finally
        {
            await offlineLifetime.CancelAsync();
            await offlineCommit.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        Node peer = new(TestItem.PublicKeyB, "9.9.9.9", 30303);
        IMessageSerializationService serialization = chain.Container.Resolve<IMessageSerializationService>();
        _msgSender.SendMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);
        _msgSender.SendMsg(Arg.Any<PingMsg>()).Returns(call =>
        {
            if (!answered) return Task.CompletedTask;
            PingMsg sent = call.Arg<PingMsg>();
            byte[] buffer = serialization.SerializeToArray(sent);
            Assert.That(buffer[97], Is.EqualTo(5));
            PongMsg pong = new(peer.Id, _timestamper.UnixTime.SecondsLong + 20, sent.Mdc!.Value)
            {
                FarAddress = peer.DiscoveryAddress
            };
            return effectiveAdapter.OnIncomingMsg(pong);
        });
        Assert.That(await effectiveAdapter.Ping(peer, token), Is.EqualTo(answered));
        storage.DrainCommits();
        using CancellationTokenSource onlineLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task onlineCommit = persistence.RunDiscoveryPersistenceCommit(onlineLifetime.Token);
        try
        {
            await storage.NextCommit(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId),
                    Is.EqualTo(new[] { answered ? peer.Id : stored.NodeId }));
                Assert.That(storage.GetPersistedNodes().Any(node => node.NodeId == stored.NodeId), Is.EqualTo(!answered));
            }
        }
        finally
        {
            await onlineLifetime.CancelAsync();
            await onlineCommit.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class ForwardingAdapter(IKademliaAdapter inner) : IKademliaAdapter
    {
        public IMsgSender? MsgSender { get => inner.MsgSender; set => inner.MsgSender = value; }
        public NodeSession GetSession(Node node) => inner.GetSession(node);
        public IAsyncEnumerable<Node> ReadPeerCandidates(CancellationToken token) => inner.ReadPeerCandidates(token);
        public Task<EnrResponseMsg?> SendEnrRequest(Node receiver, CancellationToken token) => inner.SendEnrRequest(receiver, token);
        public Task<bool> Ping(Node receiver, CancellationToken token) => inner.Ping(receiver, token);
        public Task<Node[]?> FindNeighbours(Node receiver, PublicKey target, CancellationToken token) => inner.FindNeighbours(receiver, target, token);
        public Task OnIncomingMsg(DiscoveryMsg msg) => inner.OnIncomingMsg(msg);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CommitObservedStorage(INetworkStorage inner) : INetworkStorage
    {
        private readonly Channel<bool> _commits = Channel.CreateUnbounded<bool>();
        public NetworkNode[] GetPersistedNodes() => inner.GetPersistedNodes();
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
    [CancelAfter(10000)]
    public async Task Ping_should_bond_but_never_send_enr_request_even_when_pong_advertises_newer_sequence(CancellationToken token)
    {
        ConfigureBondCallback(pongEnrSequence: 42);

        bool result = await _adapter.Ping(_receiver, token);

        Assert.That(result, Is.True);
        await _msgSender.DidNotReceive().SendMsg(Arg.Any<EnrRequestMsg>());
        _kademliaMessageReceiver.DidNotReceive().AddOrRefresh(Arg.Is<Node>(n => n.Enr != null));
    }
}
