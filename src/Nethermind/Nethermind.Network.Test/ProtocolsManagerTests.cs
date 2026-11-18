// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V70;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.Test;

[NonParallelizable]
public class ProtocolsManagerTests
{
    [SetUp]
    public void SetUp()
    {
    }

    public static Context When => new();

    [Test]
    public void Pre_hello_capability_cannot_start_eth_or_admit_a_sync_peer()
        => When.CreateIncomingSession().ActivateChannel().Handshake().Init()
            .ReceiveAddCapability().VerifyNoEthStartup();

    [Test]
    public void Hello_accepts_valid_ports_for_all_peer_types([Values(0, 65535)] int port,
        [Values(0, 1, 2)] int privilege)
    {
        Node node = new(TestItem.PublicKeyB, "35.0.0.1", 30000)
        {
            IsStatic = privilege == 1,
            IsTrusted = privilege == 2
        };
        Context context = When.CreateOutgoingSession(node).Handshake().Init().ReceiveHelloThroughPipeline(port);
        context.VerifyInitialized();
    }

    [Test]
    public void Non_listening_inbound_peer_is_not_discovered_or_persisted()
        => When.CreateIncomingSession().ActivateChannel().Handshake().Init().ReceiveHelloThroughPipeline(0)
            .ReceiveStatus().VerifyEthInitialized().VerifyNoListeningEndpointRetained();

    [Test]
    public void Uses_host_disconnected_event_when_unsubscribing_on_disconnect()
    {
        IRlpxHost rlpxHost = Substitute.For<IRlpxHost>();
        TrackingSession session = new();
        _ = new ProtocolsManager(
            Substitute.For<ISyncPeerPool>(),
            Substitute.For<ITxPool>(),
            Substitute.For<IDiscoveryApp>(),
            rlpxHost,
            Substitute.For<INodeStatsManager>(),
            Substitute.For<IProtocolValidator>(),
            Substitute.For<IPeerManager>(),
            Substitute.For<INetworkStorage>(),
            [],
            [],
            LimboLogs.Instance);

        rlpxHost.SessionCreated += Raise.EventWith(new object(), new SessionEventArgs(session));

        EventHandler<EventArgs>? initializedHandler = session.AddedInitializedHandler;

        rlpxHost.SessionDisconnected += Raise.Event<SessionDisconnectedEventHandler>(
            new object(),
            session,
            new DisconnectEventArgs(DisconnectReason.Other, DisconnectType.Remote, "test"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initializedHandler, Is.Not.Null);
            Assert.That(session.AddedDisconnectedHandler, Is.Null);
            Assert.That(session.RemovedInitializedHandler, Is.SameAs(initializedHandler));
            Assert.That(session.RemovedDisconnectedHandler, Is.Null);
        }
    }

    [TestCase(DisconnectReason.TooManyPeers, DisconnectType.Remote)]
    [TestCase(DisconnectReason.ConnectionClosed, DisconnectType.Remote)]
    [TestCase(DisconnectReason.ConnectionReset, DisconnectType.Local)]
    [TestCase(DisconnectReason.OutgoingConnectionFailed, DisconnectType.Local)]
    [TestCase(DisconnectReason.Exception, DisconnectType.Local)]
    [TestCase(DisconnectReason.ClientQuitting, DisconnectType.Remote)]
    [TestCase(DisconnectReason.Other, DisconnectType.Remote)]
    [TestCase(DisconnectReason.BreachOfProtocol, DisconnectType.Remote)]
    public void Initialized_session_disconnects_are_not_logged_at_debug(
        DisconnectReason reason,
        DisconnectType type)
    {
        TestLogger logger = new() { IsTrace = false };
        IRlpxHost rlpxHost = Substitute.For<IRlpxHost>();
        ITxPool txPool = Substitute.For<ITxPool>();
        ISession session = Substitute.For<ISession>();
        session.BestStateReached.Returns(SessionState.Initialized);
        session.RemoteNodeId.Returns(TestItem.PublicKeyA);
        session.Node.Returns(new Node(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30303));
        _ = new ProtocolsManager(
            Substitute.For<ISyncPeerPool>(),
            txPool,
            Substitute.For<IDiscoveryApp>(),
            rlpxHost,
            Substitute.For<INodeStatsManager>(),
            Substitute.For<IProtocolValidator>(),
            Substitute.For<IPeerManager>(),
            Substitute.For<INetworkStorage>(),
            [],
            [],
            new OneLoggerLogManager(new ILogger(logger)));

        rlpxHost.SessionDisconnected += Raise.Event<SessionDisconnectedEventHandler>(
            new object(),
            session,
            new DisconnectEventArgs(reason, type, "test"));

        Assert.That(logger.LogList, Is.Empty);
        txPool.DidNotReceive().RemovePeer(Arg.Any<PublicKey>());
    }

    [Test]
    public void Advertised_capabilities_apply_resolver_additions_and_removals()
    {
        ProtocolsManager manager = BuildManagerWithResolvers(
            new FakeCapabilityResolver(caps => caps.Add(new Capability(Protocol.Eth, 69))),
            new FakeCapabilityResolver(caps => caps.Remove(new Capability(Protocol.Eth, 68))));

        // Default eth/68 removed, eth/69 added by the resolvers.
        Assert.That(manager.GetHighestProtocolVersion(Protocol.Eth), Is.EqualTo(69));
    }

    [Test]
    public void Advertised_capabilities_are_cached_and_rebuilt_on_resolver_change()
    {
        FakeCapabilityResolver resolver = new(caps => caps.Add(new Capability(Protocol.Snap, SnapVersions.Snap1)));
        ProtocolsManager manager = BuildManagerWithResolvers(resolver);

        Assert.That(manager.GetHighestProtocolVersion(Protocol.Snap), Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.GetHighestProtocolVersion(Protocol.Snap), Is.EqualTo(1));
            Assert.That(resolver.ResolveCount, Is.EqualTo(1), "advertised capabilities should be cached across calls");
        }

        resolver.RaiseChanged();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.GetHighestProtocolVersion(Protocol.Snap), Is.EqualTo(1));
            Assert.That(resolver.ResolveCount, Is.EqualTo(2), "cache should rebuild after a resolver signals a change");
        }
    }

    private static ProtocolsManager BuildManagerWithResolvers(params IP2PCapabilityResolver[] resolvers) =>
        new(
            Substitute.For<ISyncPeerPool>(),
            Substitute.For<ITxPool>(),
            Substitute.For<IDiscoveryApp>(),
            Substitute.For<IRlpxHost>(),
            Substitute.For<INodeStatsManager>(),
            Substitute.For<IProtocolValidator>(),
            Substitute.For<IPeerManager>(),
            Substitute.For<INetworkStorage>(),
            [],
            [new DefaultP2PCapabilityResolver(), .. resolvers],
            LimboLogs.Instance);

    private sealed class FakeCapabilityResolver(Action<ISet<Capability>> resolve) : IP2PCapabilityResolver
    {
        public int ResolveCount { get; private set; }

        public void Resolve(ISet<Capability> capabilities)
        {
            ResolveCount++;
            resolve(capabilities);
        }

        public event Action? Changed;

        public void RaiseChanged() => Changed?.Invoke();
    }

    public class Context
    {
        internal Session CurrentSession => (Session)_currentSession;

        private readonly int _localPort = 30312;
        private readonly int _remotePort = 30000;
        private readonly string _remoteHost = "35.0.0.1";
        private ISession _currentSession;
        private readonly IDiscoveryApp _discoveryApp;
        private readonly IRlpxHost _rlpxHost;
        private readonly ProtocolsManager _manager;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly INetworkStorage _peerStorage;
        private readonly IProtocolValidator _protocolValidator;
        private readonly IMessageSerializationService _serializer;
        private readonly ISyncServer _syncServer;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly ITxPool _txPool;
        private readonly IChannelHandlerContext _channelHandlerContext;
        private readonly IChannel _channel;
        private readonly IChannelPipeline _pipeline;
        private readonly IPacketSender _packetSender;
        private readonly IBlockTree _blockTree;
        private readonly IGossipPolicy _gossipPolicy;
        private readonly IPeerManager _peerManager;
        private readonly INetworkConfig _networkConfig;
        private readonly ITxPoolConfig _txPoolConfig;
        private readonly IChainHeadSpecProvider _specProvider;
        private readonly IForkInfo _forkInfo;
        private readonly List<(int Reason, bool SnappyEnabled)> _sentDisconnects = [];

        public Context(ILogManager? logManager = null, string? clientIdMatcher = null)
        {
            _channel = Substitute.For<IChannel>();
            _channelHandlerContext = Substitute.For<IChannelHandlerContext>();
            _pipeline = Substitute.For<IChannelPipeline>();
            _channelHandlerContext.Channel.Returns(_channel);
            _channel.Pipeline.Returns(_pipeline);
            _pipeline.Get<ZeroPacketSplitter>().Returns(new ZeroPacketSplitter());
            _packetSender = Substitute.For<IPacketSender>();
            _packetSender.When(static sender => sender.Enqueue(Arg.Any<DisconnectMessage>()))
                .Do(call => _sentDisconnects.Add((call.Arg<DisconnectMessage>().Reason, _pipeline.Get<ZeroNettyP2PHandler>().SnappyEnabled)));
            _syncServer = Substitute.For<ISyncServer>();
            _syncServer = Substitute.For<ISyncServer>();
            _syncServer.Genesis.Returns(Build.A.Block.Genesis.TestObject.Header);
            _syncServer.Head.Returns(Build.A.BlockHeader.TestObject);
            _txPool = Substitute.For<ITxPool>();
            _discoveryApp = Substitute.For<IDiscoveryApp>();

            _serializer = new MessageSerializationService(
                SerializerInfo.Create(new HelloMessageSerializer()),
                SerializerInfo.Create(new AddCapabilityMessageSerializer()),
                SerializerInfo.Create(new StatusMessageSerializer()),
                SerializerInfo.Create(new DisconnectMessageSerializer())
                );

            _rlpxHost = Substitute.For<IRlpxHost>();
            _rlpxHost.LocalPort.Returns(_localPort);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _nodeStatsManager = new NodeStatsManager(timerFactory, LimboLogs.Instance);
            _blockTree = Substitute.For<IBlockTree>();
            _blockTree.NetworkId.Returns(TestBlockchainIds.NetworkId);
            _blockTree.ChainId.Returns(TestBlockchainIds.ChainId);
            _blockTree.Genesis.Returns(Build.A.Block.Genesis.TestObject.Header);
            _forkInfo = new ForkInfo(MainnetSpecProvider.Instance, _syncServer);
            _peerManager = Substitute.For<IPeerManager>();
            _networkConfig = new NetworkConfig { ClientIdMatcher = clientIdMatcher };
            _protocolValidator = new ProtocolValidator(_nodeStatsManager, _blockTree, _forkInfo, _networkConfig, LimboLogs.Instance);
            _peerStorage = Substitute.For<INetworkStorage>();
            _syncPeerPool = Substitute.For<ISyncPeerPool>();
            _gossipPolicy = Substitute.For<IGossipPolicy>();
            _txPoolConfig = Substitute.For<ITxPoolConfig>();
            _specProvider = Substitute.For<IChainHeadSpecProvider>();
            _manager = new ProtocolsManager(
                _syncPeerPool,
                _txPool,
                _discoveryApp,
                _rlpxHost,
                _nodeStatsManager,
                _protocolValidator,
                _peerManager,
                _peerStorage,
                BuildProtocolHandlerFactories(),
                [new DefaultP2PCapabilityResolver()],
                logManager ?? LimboLogs.Instance);
        }

        private IProtocolHandlerFactory[] BuildProtocolHandlerFactories() => [
                new ReusableProtocolHandlerFactory<P2PProtocolHandler>(
                    session => new P2PProtocolHandler(session, new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), _nodeStatsManager, _serializer, RunImmediatelyScheduler.Instance, LimboLogs.Instance, _networkConfig),
                    Protocol.P2P),
                new ReusableProtocolHandlerFactory<Eth66ProtocolHandler>(
                    session => new Eth66ProtocolHandler(session, _serializer, _nodeStatsManager, _syncServer, RunImmediatelyScheduler.Instance, _txPool, _gossipPolicy, _forkInfo, LimboLogs.Instance),
                    Protocol.Eth,
                    66),
                new ReusableProtocolHandlerFactory<Eth67ProtocolHandler>(
                    session => new Eth67ProtocolHandler(session, _serializer, _nodeStatsManager, _syncServer, RunImmediatelyScheduler.Instance, _txPool, _gossipPolicy, _forkInfo, LimboLogs.Instance),
                    Protocol.Eth,
                    67),
                new ReusableProtocolHandlerFactory<Eth68ProtocolHandler>(
                    session => new Eth68ProtocolHandler(session, _serializer, _nodeStatsManager, _syncServer, RunImmediatelyScheduler.Instance, _txPool, _gossipPolicy, _forkInfo, LimboLogs.Instance, _txPoolConfig, _specProvider),
                    Protocol.Eth,
                    68),
                new ReusableProtocolHandlerFactory<Eth69ProtocolHandler>(
                    session => new Eth69ProtocolHandler(session, _serializer, _nodeStatsManager, _syncServer, RunImmediatelyScheduler.Instance, _txPool, _gossipPolicy, _forkInfo, LimboLogs.Instance, _txPoolConfig, _specProvider),
                    Protocol.Eth,
                    69),
                new ReusableProtocolHandlerFactory<Eth70ProtocolHandler>(
                    session => new Eth70ProtocolHandler(session, _serializer, _nodeStatsManager, _syncServer, RunImmediatelyScheduler.Instance, _txPool, _gossipPolicy, _forkInfo, LimboLogs.Instance, _txPoolConfig, _specProvider),
                    Protocol.Eth,
                    70),
                new ReusableProtocolHandlerFactory<Eth71ProtocolHandler>(
                    session => new Eth71ProtocolHandler(session, _serializer, _nodeStatsManager, _syncServer, RunImmediatelyScheduler.Instance, _txPool, _gossipPolicy, _forkInfo, LimboLogs.Instance, _txPoolConfig, _specProvider),
                    Protocol.Eth,
                    71)
            ];

        public Context CreateIncomingSession()
        {
            IChannel channel = Substitute.For<IChannel>();
            _currentSession = new Session(_localPort, channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            _pipeline.Get<ZeroNettyP2PHandler>().Returns(new ZeroNettyP2PHandler(_currentSession, LimboLogs.Instance));
            _rlpxHost.SessionCreated += Raise.EventWith(new object(), new SessionEventArgs(_currentSession));
            return this;
        }

        public Context CreateOutgoingSession()
            => CreateOutgoingSession(new Node(TestItem.PublicKeyB, _remoteHost, _remotePort));

        public Context CreateOutgoingSession(Node node)
        {
            IChannel channel = Substitute.For<IChannel>();
            _currentSession = new Session(_localPort, node, channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            _pipeline.Get<ZeroNettyP2PHandler>().Returns(new ZeroNettyP2PHandler(_currentSession, LimboLogs.Instance));
            _rlpxHost.SessionCreated += Raise.EventWith(new object(), new SessionEventArgs(_currentSession));
            return this;
        }

        public Context Handshake()
        {
            _currentSession.Handshake(TestItem.PublicKeyB);
            return this;
        }

        public Context Init()
        {
            _currentSession.Init(5, _channelHandlerContext, _packetSender);
            return this;
        }

        public Context ActivateChannel()
        {
            _currentSession.RemoteHost = _remoteHost;
            _currentSession.RemotePort = _remotePort;
            return this;
        }

        public Context RejectInPeerManager(DisconnectReason reason)
        {
            _peerManager.When(static manager => manager.OnP2PProtocolInitialized(Arg.Any<ISession>()))
                .Do(call => call.Arg<ISession>().InitiateDisconnect(reason, "test"));
            return this;
        }

        public Context VerifyDisconnectSentWithSnappy(EthDisconnectReason reason)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_currentSession.IsClosing, Is.True);
                Assert.That(_sentDisconnects, Is.EqualTo(new[] { ((int)reason, true) }));
            }
            return this;
        }

        public Context VerifyPingSenderSet()
        {
            Assert.That(_currentSession.PingSender, Is.Not.Null);
            return this;
        }

        public Context VerifyDisconnected()
        {
            Assert.That(_currentSession.State, Is.EqualTo(SessionState.Disconnected));
            return this;
        }

        public Context ReceiveDisconnect()
        {
            using DisconnectMessage message = new(EthDisconnectReason.Other);
            using DisposableByteBuffer disconnectPacket = _serializer.ZeroSerialize(message).AsDisposable();

            // to account for AdaptivePacketType byte
            disconnectPacket.ReadByte();
            _currentSession.ReceiveMessage(new NettyBufferPacket(disconnectPacket) { PacketType = P2PMessageCode.Disconnect });
            return this;
        }

        public Context ReceiveAddCapability()
        {
            _currentSession.ReceiveMessage(new NettyBufferPacket(DotNetty.Buffers.Unpooled.Empty) { PacketType = P2PMessageCode.AddCapability });
            return this;
        }

        public Context ReceiveHelloThroughPipeline(int port)
        {
            using PooledBufferLeakDetector detector = new();
            EmbeddedChannel channel = new(new ZeroNettyP2PHandler(_currentSession, LimboLogs.Instance));
            using HelloMessage hello = new()
            {
                P2PVersion = 5,
                ClientId = "test",
                ListenPort = port,
                NodeId = TestItem.PublicKeyB,
                Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) }
            };
            try
            {
                DotNetty.Buffers.IByteBuffer buffer = detector.Allocator.Buffer();
                buffer.WriteBytes(new HelloMessageSerializer().SerializeToArray(hello));
                channel.WriteInbound(new NettyBufferPacket(buffer) { PacketType = P2PMessageCode.Hello });
            }
            finally
            {
                channel.FinishAndReleaseAll();
            }
            return this;
        }

        public Context VerifyNoEthStartup(bool closing = true)
        {
            Assert.That(_currentSession.IsClosing, Is.EqualTo(closing));
            Session concreteSession = (Session)_currentSession;
            concreteSession.MarkUsefulSyncResponse();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(concreteSession.HasUsefulSyncResponse, Is.False);
                Assert.That(_currentSession.TryGetProtocolHandler(Protocol.Eth, out _), Is.False);
            }
            _packetSender.DidNotReceive().Enqueue(Arg.Any<StatusMessage>());
            _syncPeerPool.DidNotReceive().AddPeer(Arg.Any<ISyncPeer>());
            _txPool.DidNotReceive().AddPeer(Arg.Any<ITxPoolPeer>());
            return this;
        }

        public Context VerifyNoListeningEndpointRetained()
        {
            _peerManager.Received(1).OnP2PProtocolInitialized(Arg.Is<ISession>(session => session.Node.Port == 0));
            _discoveryApp.DidNotReceive().AddNodeToDiscovery(Arg.Any<Node>());
            _peerStorage.DidNotReceive().UpdateNode(Arg.Any<NetworkNode>());
            _syncPeerPool.Received(1).AddPeer(Arg.Any<ISyncPeer>());
            return this;
        }

        public Context VerifyInitialized()
        {
            Assert.That(_currentSession.State, Is.EqualTo(SessionState.Initialized));
            return this;
        }

        public Context VerifyProtocolVersion(string protocol, int version)
        {
            Assert.That(_manager.GetHighestProtocolVersion(protocol), Is.EqualTo(version));
            return this;
        }

        public Context VerifyCompatibilityValidationType(CompatibilityValidationType expectedType)
        {
            Assert.That(_nodeStatsManager.GetOrAdd(_currentSession.Node).FailedCompatibilityValidation, Is.EqualTo(expectedType));
            return this;
        }

        public Context Disconnect()
        {
            _currentSession.MarkDisconnected(DisconnectReason.TooManyPeers, DisconnectType.Local, "test");
            _rlpxHost.SessionDisconnected += Raise.Event<SessionDisconnectedEventHandler>(
                new object(),
                _currentSession,
                new DisconnectEventArgs(DisconnectReason.TooManyPeers, DisconnectType.Local, "test"));
            return this;
        }

        public Context ReceiveStatus()
        {
            using StatusMessage msg = new();
            msg.TotalDifficulty = 1;
            msg.NetworkId = TestBlockchainIds.NetworkId;
            msg.GenesisHash = _blockTree.Genesis.Hash;
            msg.BestHash = _blockTree.Genesis.Hash;
            msg.ProtocolVersion = 68;
            msg.ForkId = new ForkId(0, 0);

            return ReceiveStatus(msg);
        }

        private Context ReceiveStatus(StatusMessage msg)
        {
            using DisposableByteBuffer statusPacket = _serializer.ZeroSerialize(msg).AsDisposable();
            statusPacket.ReadByte();

            _currentSession.ReceiveMessage(new NettyBufferPacket(statusPacket) { PacketType = Eth62MessageCode.Status + 16 });
            return this;
        }

        public Context VerifyEthInitialized()
        {
            INodeStats stats = _nodeStatsManager.GetOrAdd(_currentSession.Node);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.EthNodeDetails.NetworkId, Is.EqualTo(TestBlockchainIds.NetworkId));
                Assert.That(stats.EthNodeDetails.GenesisHash, Is.EqualTo(_blockTree.Genesis.Hash));
                Assert.That(stats.EthNodeDetails.ProtocolVersion, Is.EqualTo(68));
                Assert.That(stats.EthNodeDetails.TotalDifficulty, Is.EqualTo(BigInteger.One));
            }
            return this;
        }

        public Context VerifyNotPersisted()
        {
            _peerStorage.DidNotReceive().UpdateNode(Arg.Any<NetworkNode>());
            return this;
        }

        public Context VerifySyncPeersRemoved()
        {
            _txPool.Received().RemovePeer(Arg.Any<PublicKey>());
            _syncPeerPool.Received().RemovePeer(Arg.Any<ISyncPeer>());
            return this;
        }

        private Context ReceiveHello(HelloMessage msg)
        {
            using DisposableByteBuffer helloPacket = _serializer.ZeroSerialize(msg).AsDisposable();
            // to account for AdaptivePacketType byte
            helloPacket.ReadByte();

            _currentSession.ReceiveMessage(new NettyBufferPacket(helloPacket) { PacketType = P2PMessageCode.Hello });
            return this;
        }


        public Context ReceiveHello(byte p2pVersion = 5)
        {
            using HelloMessage msg = new();
            msg.Capabilities = new ArrayPoolList<Capability>(1) { new("eth", 68) };
            msg.NodeId = TestItem.PublicKeyB;
            msg.ClientId = "other client v1";
            msg.P2PVersion = p2pVersion;
            msg.ListenPort = 30314;

            return ReceiveHello(msg);
        }

        public Context ReceiveHelloNoEth()
        {
            using HelloMessage msg = new();
            msg.Capabilities = ArrayPoolList<Capability>.Empty();
            msg.NodeId = TestItem.PublicKeyB;
            msg.ClientId = "other client v1";
            msg.P2PVersion = 5;
            msg.ListenPort = 30314;
            return ReceiveHello(msg);
        }

        public Context ReceiveHelloEth(int protocolVersion)
        {
            using HelloMessage msg = new();
            msg.Capabilities = new ArrayPoolList<Capability>(1) { new("eth", protocolVersion) };
            msg.NodeId = TestItem.PublicKeyB;
            msg.ClientId = "other client v1";
            msg.P2PVersion = 5;
            msg.ListenPort = 30314;
            return ReceiveHello(msg);
        }


        public Context ReceiveHelloWrongEth() => ReceiveHelloEth(65);

        public Context ReceiveStatusWrongChain(ulong networkId)
        {
            using StatusMessage msg = new();
            msg.TotalDifficulty = 1;
            msg.NetworkId = networkId;
            msg.GenesisHash = TestItem.KeccakA;
            msg.BestHash = TestItem.KeccakA;
            msg.ProtocolVersion = 68;

            return ReceiveStatus(msg);
        }

        public Context ReceiveStatusWrongGenesis()
        {
            using StatusMessage msg = new();
            msg.TotalDifficulty = 1;
            msg.NetworkId = TestBlockchainIds.NetworkId;
            msg.GenesisHash = TestItem.KeccakB;
            msg.BestHash = TestItem.KeccakB;
            msg.ProtocolVersion = 68;

            return ReceiveStatus(msg);
        }
    }

    [Test]
    public void Sets_ping_sender_after_receiving_hello() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .ReceiveHello()
            .VerifyPingSenderSet();

    [Test]
    public void Compresses_peer_manager_disconnect_after_v5_hello() => When
            .RejectInPeerManager(DisconnectReason.TooManyPeers)
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .ReceiveHello()
            .VerifyDisconnectSentWithSnappy(EthDisconnectReason.TooManyPeers);

    [Test]
    public void Compresses_validator_disconnect_after_v5_hello() => new Context(clientIdMatcher: "^Geth/")
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .ReceiveHello()
            .VerifyDisconnectSentWithSnappy(DisconnectReason.ClientFiltered.ToEthDisconnectReason());

    [Test]
    public void Disconnects_on_p2p_before_version_4() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .ReceiveHello(3)
            .VerifyDisconnected();

    [Test]
    public void Disconnects_on_receiving_disconnect() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .ReceiveHello()
            .ReceiveDisconnect()
            .VerifyDisconnected();

    [Test]
    public void Runs_ok_when_initializing_protocol_on_a_closing_session() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .Disconnect()
            .ReceiveHello();

    [Test]
    public void Can_initialize_a_session() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .VerifyInitialized();

    [Test]
    public void Can_initialize_eth_protocol([Values] bool maskSensitiveData, [Values] bool outbound)
    {
        bool previous = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = maskSensitiveData;
        try
        {
            TestLogger logger = new();
            Context context = new(new OneLoggerLogManager(new(logger)));
            if (outbound) context.CreateOutgoingSession();
            else context.CreateIncomingSession();
            context.ActivateChannel().Handshake().Init().VerifyInitialized()
                .ReceiveHello().ReceiveStatus().VerifyEthInitialized();

            string diagnostic = logger.LogList.Single(line => line.Contains(" sync peer ") && line.EndsWith(" created."));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(diagnostic, Does.Contain(maskSensitiveData ? "[redacted]" : "other client v1"));
                if (maskSensitiveData) Assert.That(diagnostic, Does.Not.Contain("other client v1"));
            }
        }
        finally
        {
            SensitiveLogMasking.Enabled = previous;
        }
    }

    [Test]
    public void Status_alone_does_not_persist_a_peer([Values] bool outbound)
    {
        Context context = outbound ? When.CreateOutgoingSession() : When.CreateIncomingSession().ActivateChannel();
        context.Handshake().Init().ReceiveHello().ReceiveStatus().VerifyEthInitialized().VerifyNotPersisted();
    }

    [Test]
    public void Removes_sync_peers_on_disconnect() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .VerifyInitialized()
            .ReceiveHello()
            .ReceiveStatus()
            .VerifyEthInitialized()
            .Disconnect()
            .VerifySyncPeersRemoved();

    [Test]
    public void Disconnects_on_missing_eth() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .VerifyInitialized()
            .ReceiveHelloNoEth()
            .VerifyDisconnected();

    [Test]
    public void Disconnects_on_wrong_eth() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .VerifyInitialized()
            .ReceiveHelloWrongEth()
            .VerifyDisconnected();

    [Test]
    public void Disconnects_on_wrong_network_id([Values(TestBlockchainIds.NetworkId + 1, TestBlockchainIds.ChainId)] ulong networkId) => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .VerifyInitialized()
            .ReceiveHello()
            .ReceiveStatusWrongChain(networkId)
            .VerifyCompatibilityValidationType(CompatibilityValidationType.NetworkId)
            .VerifyDisconnected();

    [Test]
    public void Disconnects_on_wrong_genesis_hash() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .VerifyInitialized()
            .ReceiveHello()
            .ReceiveStatusWrongGenesis()
            .VerifyDisconnected();

    [Test]
    public void Initialized_with_eth68_only() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .VerifyInitialized()
            .ReceiveHelloEth(68)
            .VerifyInitialized();

    [Test]
    public void Has_correct_highest_eth_protocol_version() => When
            .CreateIncomingSession()
            .ActivateChannel()
            .Handshake()
            .Init()
            .VerifyProtocolVersion(Protocol.Eth, 68);

    [Test]
    public void default_receipt_sessions_remain_closed_with_independent_clocks()
    {
        ISession first = new TrackingSession();
        ISession second = new TrackingSession();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => first.InboundReceipts.BindClock(new Nethermind.Core.ManualTimestamper()), Throws.Nothing);
            Assert.That(() => second.InboundReceipts.BindClock(new Nethermind.Core.ManualTimestamper()), Throws.Nothing);
        }
        first.InboundReceipts.BindExecutorLifetime(Task.CompletedTask);
        second.InboundReceipts.BindExecutorLifetime(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        first.InboundReceipts.Activate();
        second.InboundReceipts.Activate();
        NettyBufferPacket packet = new(DotNetty.Buffers.Unpooled.Buffer(0));
        try
        {
            first.InboundReceipts.Stamp(packet);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.InboundReceipts, Is.SameAs(second.InboundReceipts));
                Assert.That(first.InboundReceipts.IsClosed, Is.True);
                Assert.That(second.InboundReceipts.IsClosed, Is.True);
                Assert.That(first.InboundReceipts.IsActive, Is.False);
                Assert.That(packet.ReceiptSequence, Is.Zero);
                Assert.That(first.IsChannelWritable, Is.False);
            }
        }
        finally
        {
            packet.Release();
        }
    }

    private sealed class TrackingSession : ISession
    {
        public EventHandler<EventArgs>? AddedInitializedHandler { get; private set; }
        public EventHandler<EventArgs>? RemovedInitializedHandler { get; private set; }
        public EventHandler<DisconnectEventArgs>? AddedDisconnectedHandler { get; private set; }
        public EventHandler<DisconnectEventArgs>? RemovedDisconnectedHandler { get; private set; }

        public byte P2PVersion => 5;
        public SessionState State => SessionState.Disconnected;
        public SessionState BestStateReached => SessionState.Disconnected;
        public bool IsClosing => true;
        public PublicKey RemoteNodeId => null!;
        public PublicKey ObsoleteRemoteNodeId => null!;
        public string RemoteHost { get; set; } = string.Empty;
        public int RemotePort { get; set; }
        public int LocalPort => 0;
        public bool IsNetworkIdMatched { get; set; }
        public ConnectionDirection Direction => ConnectionDirection.In;
        public Guid SessionId { get; } = Guid.NewGuid();
        public Node Node => null!;
        public DateTime LastPingUtc { get; set; }
        public DateTime LastPongUtc { get; set; }
        public IPingSender PingSender { get; set; } = null!;

        public event EventHandler<DisconnectEventArgs> Disconnecting { add { } remove { } }
        public event EventHandler<DisconnectEventArgs> Disconnected
        {
            add => AddedDisconnectedHandler = value;
            remove => RemovedDisconnectedHandler = value;
        }
        public event EventHandler<EventArgs> Initialized
        {
            add => AddedInitializedHandler = value;
            remove => RemovedInitializedHandler = value;
        }
        public event EventHandler<EventArgs> HandshakeComplete { add { } remove { } }
        public event EventHandler<PeerEventArgs> MsgReceived { add { } remove { } }
        public event EventHandler<PeerEventArgs> MsgDelivered { add { } remove { } }

        public void RaiseDisconnected() => AddedDisconnectedHandler?.Invoke(
            this,
            new DisconnectEventArgs(DisconnectReason.Other, DisconnectType.Local, "test"));

        public void Dispose() { }
        public void ReceiveMessage(ZeroPacket zeroPacket) => throw new NotSupportedException();
        public int DeliverMessage<T>(T message) where T : P2PMessage => throw new NotSupportedException();
        public void EnableSnappy() => throw new NotSupportedException();
        public void AddSupportedCapability(Capability capability) => throw new NotSupportedException();
        public bool HasAvailableCapability(Capability capability) => throw new NotSupportedException();
        public bool HasAgreedCapability(Capability capability) => throw new NotSupportedException();
        public void AddProtocolHandler(IProtocolHandler handler) => throw new NotSupportedException();
        public bool TryGetProtocolHandler(string protocolCode, out IProtocolHandler handler) => throw new NotSupportedException();
        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender) => throw new NotSupportedException();
        public void InitiateDisconnect(DisconnectReason disconnectReason, string details) => throw new NotSupportedException();
        public void MarkDisconnected(DisconnectReason disconnectReason, DisconnectType disconnectType, string details) => throw new NotSupportedException();
        public void Handshake(PublicKey handshakeRemoteNodeId) => throw new NotSupportedException();
        public void StartTrackingSession() => throw new NotSupportedException();
    }

    [Test]
    public void Satellite_registration_rechecks_a_concurrently_initialized_sync_peer()
    {
        IRlpxHost host = Substitute.For<IRlpxHost>();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
        IProtocolValidator validator = Substitute.For<IProtocolValidator>();
        ISyncPeerPool syncPool = Substitute.For<ISyncPeerPool>();
        validator.ValidateOrDisconnect(Arg.Any<string>(), Arg.Any<ISession>(), Arg.Any<ProtocolInitializedEventArgs>()).Returns(true);
        ProtocolsManager manager = CreateManager(host, stats, validator, syncPool);
        ISession session = Substitute.For<ISession>();
        session.SessionId.Returns(Guid.NewGuid());
        session.Node.Returns(new Node(TestItem.PublicKeyA, "1.2.3.4", 30303));
        using SyncPeerProtocolHandlerBase syncHandler = Substitute.For<SyncPeerProtocolHandlerBase>(session, serializer,
            stats, Substitute.For<ISyncServer>(), RunImmediatelyScheduler.Instance, LimboLogs.Instance, TimeProvider.System);
        syncHandler.ProtocolCode.Returns("eth");
        using ProtocolHandlerBase satelliteHandler = Substitute.For<ProtocolHandlerBase>(session, stats, serializer,
            RunImmediatelyScheduler.Instance, LimboLogs.Instance, TimeProvider.System);
        satelliteHandler.ProtocolCode.Returns("snap");
        satelliteHandler.IsPriority = true;
        syncPool.GetPeer(session.Node).Returns(_ =>
        {
            ((IProtocolRegistrar)manager).OnProtocolInitialized(session, syncHandler,
                new SyncPeerProtocolInitializedEventArgs(syncHandler) { BestHash = TestItem.KeccakA, GenesisHash = TestItem.KeccakB });
            return null;
        });

        ((IProtocolRegistrar)manager).OnProtocolInitialized(session, satelliteHandler, new ProtocolInitializedEventArgs(satelliteHandler));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(syncHandler.TryGetSatelliteProtocol("snap", out ProtocolHandlerBase registered), Is.True);
            Assert.That(registered, Is.SameAs(satelliteHandler));
            Assert.That(syncHandler.IsPriority, Is.True);
        }
    }

    [Test]
    public async Task Last_removal_does_not_lose_a_concurrent_registration([Values] bool concurrent)
    {
        IRlpxHost host = Substitute.For<IRlpxHost>();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
        IProtocolValidator validator = Substitute.For<IProtocolValidator>();
        validator.ValidateOrDisconnect(Arg.Any<string>(), Arg.Any<ISession>(), Arg.Any<ProtocolInitializedEventArgs>()).Returns(true);
        ProtocolsManager manager = CreateManager(host, stats, validator, Substitute.For<ISyncPeerPool>());
        Node node = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30303);
        IDictionary registrations = (IDictionary)typeof(ProtocolsManager)
            .GetField("_hangingSatelliteProtocols", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;

        for (int i = 0; i < 32; i++)
        {
            ISession oldSession = CreateSession();
            ISession newSession = CreateSession();
            using ProtocolHandlerBase oldHandler = CreateHandler(oldSession);
            using ProtocolHandlerBase newHandler = CreateHandler(newSession);
            Register(oldSession, oldHandler);
            if (concurrent)
            {
                using Barrier barrier = new(2);
                await Task.WhenAll(
                    Task.Run(() => { barrier.SignalAndWait(); Remove(oldSession); }),
                    Task.Run(() => { barrier.SignalAndWait(); Register(newSession, newHandler); }));
            }
            else
            {
                Remove(oldSession);
                Register(newSession, newHandler);
            }

            Assert.That(registrations.Count, Is.EqualTo(1));
            IDictionary bySession = (IDictionary)registrations[node]!;
            Assert.That(bySession[newSession.SessionId], Is.SameAs(newHandler));
            Remove(newSession);
            Assert.That(registrations, Is.Empty);

            validator.ValidateOrDisconnect(Arg.Any<string>(), Arg.Any<ISession>(), Arg.Any<ProtocolInitializedEventArgs>()).Returns(_ =>
            {
                newSession.IsClosing.Returns(true);
                Remove(newSession);
                return true;
            });
            Register(newSession, newHandler);
            Assert.That(registrations, Is.Empty);
            validator.ValidateOrDisconnect(Arg.Any<string>(), Arg.Any<ISession>(), Arg.Any<ProtocolInitializedEventArgs>()).Returns(true);
        }

        ISession CreateSession()
        {
            ISession session = Substitute.For<ISession>();
            session.SessionId.Returns(Guid.NewGuid());
            session.RemoteNodeId.Returns(node.Id);
            session.Node.Returns(node);
            return session;
        }

        ProtocolHandlerBase CreateHandler(ISession session)
        {
            ProtocolHandlerBase handler = Substitute.For<ProtocolHandlerBase>(session, stats, serializer,
                RunImmediatelyScheduler.Instance, LimboLogs.Instance, TimeProvider.System);
            handler.ProtocolCode.Returns("snap");
            return handler;
        }

        void Register(ISession session, ProtocolHandlerBase handler) =>
            ((IProtocolRegistrar)manager).OnProtocolInitialized(session, handler, new ProtocolInitializedEventArgs(handler));

        void Remove(ISession session) => host.SessionDisconnected += Raise.Event<SessionDisconnectedEventHandler>(host,
            session, new DisconnectEventArgs(DisconnectReason.ClientQuitting, DisconnectType.Local, "test"));
    }

    private static ProtocolsManager CreateManager(IRlpxHost host, INodeStatsManager stats, IProtocolValidator validator, ISyncPeerPool syncPool) =>
        new(syncPool, Substitute.For<ITxPool>(), Substitute.For<IDiscoveryApp>(), host, stats, validator,
            Substitute.For<IPeerManager>(), Substitute.For<INetworkStorage>(), [], [], LimboLogs.Instance);
}
