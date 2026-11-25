// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Test.Threading;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [NonParallelizable]
    [TestFixture]
    public class P2PProtocolHandlerTests
    {
        [SetUp]
        public void Setup()
        {
            _session = Substitute.For<ISession>();
            // PublicKeyA is this node's own identity; the tests treat PublicKeyB as the authenticated remote one.
            _session.RemoteNodeId.Returns(TestItem.PublicKeyB);
            _serializer = new MessageSerializationService(
                SerializerInfo.Create(new HelloMessageSerializer()),
                SerializerInfo.Create(new PingMessageSerializer()),
                SerializerInfo.Create(new AddCapabilityMessageSerializer()),
                SerializerInfo.Create(new DisconnectMessageSerializer())
            );
        }

        [TearDown]
        public void TearDown() => _session?.Dispose();

        private ISession _session;
        private IMessageSerializationService _serializer;
        private readonly Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        private INodeStatsManager _nodeStatsManager;

        private Packet CreatePacket<T>(T message) where T : P2PMessage => new(new NettyBufferPacket(_serializer.ZeroSerialize(message))
        {
            Protocol = message.Protocol,
            PacketType = (byte)message.PacketType,
        });

        [Test]
        public void Unknown_capability_hello_flood_at_default_capacity_preserves_ban_and_honest_negotiation()
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 10000, clock);
            Node banned = new(TestItem.PublicKeyC, "8.8.8.8", 30303);
            stats.ReportDisconnect(banned, DisconnectType.Local, DisconnectReason.BreachOfProtocol);
            INodeStats retained = stats.GetOrAdd(banned);
            ContainerBuilder builder = new();
            builder.RegisterModule(new TestNethermindModule());
            builder.RegisterInstance<IEnode>(new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303));
            builder.RegisterInstance<INodeStatsManager>(stats);
            builder.RegisterInstance(_serializer);
            builder.RegisterInstance(Substitute.For<IBackgroundTaskScheduler>());
            builder.RegisterInstance<ILogManager>(LimboLogs.Instance);
            builder.RegisterInstance<INetworkConfig>(new NetworkConfig());
            using IContainer container = builder.Build();
            IProtocolHandlerFactory factory = container.Resolve<IReadOnlyList<IProtocolHandlerFactory>>().Single(factory => factory.ProtocolCode == Protocol.P2P);
            for (int i = 1; i <= 10000; i++)
            {
                byte[] bytes = new byte[32];
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), i);
                using PrivateKey key = new(bytes);
                PublicKey identity = key.PublicKey;
                stats.ReportEvent(new Node(identity, "9.9.9.9", 30303), NodeStatsEventType.DiscoveryPongIn);
            }
            for (int i = 1; i <= 10000; i++) SendHello(i, false);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.ProtectedCount, Is.EqualTo(5000));
                Assert.That(stats.GetOrAdd(banned), Is.SameAs(retained));
                Assert.That(stats.IsInboundBanned(banned, clock.GetUtcNow().UtcDateTime), Is.True);
            }
            SendHello(10001, true);

            void SendHello(int index, bool supported)
            {
                byte[] bytes = new byte[32];
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), index);
                using PrivateKey key = new(bytes);
                PublicKey identity = key.PublicKey;
                using ISession session = Substitute.For<ISession>();
                Node remote = new(identity, "9.9.9.9", 30303);
                session.RemoteNodeId.Returns(identity);
                session.Node.Returns(remote);
                Assert.That(factory.TryCreate(session, 5, out IProtocolHandler? created), Is.True);
                using P2PProtocolHandler handler = (P2PProtocolHandler)created!;
                handler.AddSupportedCapability(new Capability(Protocol.Eth, 66));
                using HelloMessage hello = new()
                {
                    NodeId = identity,
                    P2PVersion = 5,
                    ClientId = "stats flood fixture",
                    ListenPort = 30303,
                    Capabilities = new ArrayPoolList<Capability>(1) { new(supported ? Protocol.Eth : "unknown", 66) }
                };
                handler.HandleMessage(CreateP2PPacket(hello));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(handler.AgreedCapabilities.Count, Is.EqualTo(supported ? 1 : 0));
                    if (supported) session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
                    else session.Received(1).InitiateDisconnect(DisconnectReason.NoCapabilityMatched, Arg.Any<string>());
                }
            }
        }

        [Test]
        public void Protocol_factory_creates_handler_without_requiring_a_clock_registration([Values] bool registerClock)
        {
            ContainerBuilder builder = new();
            builder.RegisterInstance<IEnode>(new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303));
            builder.RegisterInstance(Substitute.For<INodeStatsManager>());
            builder.RegisterInstance(_serializer);
            builder.RegisterInstance(Substitute.For<IBackgroundTaskScheduler>());
            builder.RegisterInstance<ILogManager>(LimboLogs.Instance);
            builder.RegisterInstance<INetworkConfig>(new NetworkConfig());
            if (registerClock) builder.RegisterInstance(TimeProvider.System);
            using IContainer container = builder.Build();
            AutofacProtocolHandlerFactory<P2PProtocolHandler> factory = new(container, Protocol.P2P);
            Assert.That(factory.TryCreate(_session, 5, out IProtocolHandler? handler), Is.True);
            using (handler) Assert.That(handler, Is.InstanceOf<P2PProtocolHandler>());
        }

        private const int ListenPort = 8003;

        private P2PProtocolHandler CreateSession(ILogManager? logManager = null, TimeProvider? timeProvider = null)
        {
            _session.LocalPort.Returns(ListenPort);
            _session.Node.Returns(node);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _nodeStatsManager = new NodeStatsManager(timerFactory, LimboLogs.Instance);

            return new P2PProtocolHandler(
                _session,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303),
                _nodeStatsManager,
                _serializer,
                Substitute.For<IBackgroundTaskScheduler>(),
                logManager ?? LimboLogs.Instance,
                new NetworkConfig(),
                timeProvider ?? TimeProvider.System);
        }

        [Test]
        public void On_init_sends_a_hello_message()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.Init();

            _session.Received(1).DeliverMessage(Arg.Any<HelloMessage>());
        }

        [Test]
        public void On_init_sends_a_hello_message_with_capabilities()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            string[] expectedCapabilities = ["eth66", "eth67", "eth68", "eth69", "nodedata1"];

            // These are called by ProtocolsManager.
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 66));
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 67));
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 69));
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.NodeData, 1));

            p2PProtocolHandler.Init();

            _session.Received(1).DeliverMessage(
                Arg.Is<HelloMessage>(m => m.Capabilities.Select(c => c.ToString()).SequenceEqual(expectedCapabilities)));
        }

        [Test]
        public void On_hello_with_no_matching_capability()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();

            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 63) },
                NodeId = TestItem.PublicKeyB,
            };

            p2PProtocolHandler.HandleMessage(CreateP2PPacket(message));

            Assert.That(_nodeStatsManager.GetOrAdd(node).FailedCompatibilityValidation, Is.Not.Null);
            _session.Received(1).InitiateDisconnect(DisconnectReason.NoCapabilityMatched, Arg.Any<string>());
        }

        [TestCase(DisconnectReason.IdentitySameAsSelf, (byte)5, true)]
        [TestCase(DisconnectReason.IdentitySameAsSelf, (byte)4, false)]
        [TestCase(DisconnectReason.UnexpectedIdentity, (byte)5, true)]
        [TestCase(DisconnectReason.UnexpectedIdentity, (byte)4, false)]
        [TestCase(DisconnectReason.NoCapabilityMatched, (byte)5, true)]
        [TestCase(DisconnectReason.NoCapabilityMatched, (byte)4, false)]
        public void Enables_snappy_for_v5_hello_before_rejecting_it(DisconnectReason reason, byte p2pVersion, bool snappy)
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            bool snappyEnabled = false;
            List<(DisconnectReason Reason, bool SnappyEnabled)> disconnects = [];
            _session.When(static session => session.EnableSnappy()).Do(_ => snappyEnabled = true);
            _session.When(static session => session.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
                .Do(call => disconnects.Add((call.Arg<DisconnectReason>(), snappyEnabled)));

            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) },
                NodeId = reason switch
                {
                    DisconnectReason.IdentitySameAsSelf => TestItem.PublicKeyA,
                    DisconnectReason.UnexpectedIdentity => TestItem.PublicKeyC,
                    _ => TestItem.PublicKeyB,
                },
                ClientId = "Nethermind/v1.0",
                ListenPort = 30303,
                P2PVersion = p2pVersion,
            };

            p2PProtocolHandler.HandleMessage(CreateP2PPacket(message));

            Assert.That(disconnects, Is.EqualTo(new[] { (reason, snappy) }));
        }

        [Test]
        public void Pongs_to_ping()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.HandleMessage(CreatePacket(PingMessage.Instance));
            _session.Received(1).DeliverMessage(Arg.Any<PongMessage>());
        }

        [Test]
        public void Received_disconnect_is_logged_at_trace([Values] bool maskSensitiveData, [Values] bool knownReason)
        {
            bool previous = SensitiveLogMasking.Enabled;
            SensitiveLogMasking.Enabled = maskSensitiveData;
            try
            {
                _session.RemotePort.Returns(31417);
                TestLogger logger = new() { IsDebug = false };
                P2PProtocolHandler p2PProtocolHandler = CreateSession(new OneLoggerLogManager(new(logger)));
                using DisconnectMessage message = new(knownReason ? EthDisconnectReason.BreachOfProtocol : (EthDisconnectReason)255);

                p2PProtocolHandler.HandleMessage(CreateP2PPacket(message));

                string diagnostic = logger.LogList.Single(line => line.Contains(knownReason ? "Received disconnect" : "unknown disconnect reason"));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(diagnostic, Does.Contain(maskSensitiveData ? "[redacted]" : "31417"));
                    if (maskSensitiveData) Assert.That(diagnostic, Does.Not.Contain("31417"));
                }
            }
            finally
            {
                SensitiveLogMasking.Enabled = previous;
            }
        }

        [Test]
        public async Task Pong_refreshes_liveness_only_for_an_outstanding_or_recent_ping([Values(0, 1, 2, 3)] int pingState)
        {
            ManualTimeProvider time = new();
            P2PProtocolHandler p2PProtocolHandler = CreateSession(timeProvider: time);
            _session.LastPongUtc = time.GetUtcNow().UtcDateTime;
            Packet pong = new([])
            {
                Protocol = Protocol.P2P,
                PacketType = P2PMessageCode.Pong,
            };

            Task<bool>? ping = pingState == 0 ? null : p2PProtocolHandler.SendPing();
            if (pingState == 2)
            {
                time.AdvanceAndFireTimer(Timeouts.P2PPing);
                Assert.That(await ping!.WaitAsync(TimeSpan.FromSeconds(2)), Is.False);
            }
            else if (pingState == 3)
            {
                p2PProtocolHandler.HandleMessage(pong);
                Assert.That(await ping!.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
            }

            DateTime before = _session.LastPongUtc;
            time.Advance(TimeSpan.FromSeconds(1));

            p2PProtocolHandler.HandleMessage(pong);

            Assert.That(_session.LastPongUtc, Is.EqualTo(pingState is 1 or 2 ? time.GetUtcNow().UtcDateTime : before));
            if (pingState == 1) Assert.That(await ping!.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
        }

        [Test]
        public async Task Duplicate_ping_does_not_report_a_pong_before_one_arrives()
        {
            ManualTimeProvider time = new();
            using P2PProtocolHandler handler = CreateSession(timeProvider: time);
            Task<bool> pending = handler.SendPing();

            Assert.That(await handler.SendPing(), Is.False);

            time.AdvanceAndFireTimer(Timeouts.P2PPing);
            Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(2)), Is.False);
        }

        [Test]
        public void Sets_local_node_id_from_constructor()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.Init();
            _session.Received(1).DeliverMessage(Arg.Is<HelloMessage>(m => m.NodeId == TestItem.PublicKeyA));
        }

        [Test]
        public void Sets_port_from_constructor()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            Assert.That(p2PProtocolHandler.ListenPort, Is.EqualTo(ListenPort));
        }

        [Test]
        public void On_init_sends_a_hello_message_with_public_client_id()
        {
            ProductInfo.InitializePublicClientId("{name}/{version}");
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.Init();

            _session.Received(1).DeliverMessage(
                Arg.Is<HelloMessage>(m =>
                    m.ClientId == $"{ProductInfo.Name}/v{ProductInfo.Version}"));
        }

        [Test]
        public void On_message_exceeding_max_size_disconnects()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();

            // Build a HelloMessage with a very large ClientId to exceed BaseProtocolMaxMsgSize
            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) },
                NodeId = TestItem.PublicKeyA,
                ClientId = new string('x', (int)P2PProtocolHandler.BaseProtocolMaxMsgSize),
                ListenPort = 30303,
                P2PVersion = 5,
            };

            using DisposableByteBuffer data = _serializer.ZeroSerialize(message).AsDisposable();
            data.ReadByte(); // adaptive packet type

            Packet packet = new(data.ReadAllBytesAsArray())
            {
                Protocol = message.Protocol,
                PacketType = (byte)message.PacketType,
            };

            p2PProtocolHandler.HandleMessage(packet);

            _session.Received(1).InitiateDisconnect(DisconnectReason.MessageLimitsBreached, Arg.Any<string>());
        }

        [Test]
        public void On_non_hello_message_exceeding_max_size_disconnects()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();

            // Create oversized raw packet with Ping packet type
            byte[] oversizedData = new byte[(int)P2PProtocolHandler.BaseProtocolMaxMsgSize + 1];
            Packet packet = new(oversizedData)
            {
                Protocol = Protocol.P2P,
                PacketType = P2PMessageCode.Ping,
            };

            p2PProtocolHandler.HandleMessage(packet);

            _session.Received(1).InitiateDisconnect(DisconnectReason.MessageLimitsBreached, Arg.Any<string>());
        }

        [Test]
        public void On_message_within_max_size_does_not_disconnect_for_size()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));

            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) },
                NodeId = TestItem.PublicKeyB,
                ClientId = "Nethermind/v1.0",
                ListenPort = 30303,
                P2PVersion = 5,
            };

            using DisposableByteBuffer data = _serializer.ZeroSerialize(message).AsDisposable();
            data.ReadByte(); // adaptive packet type

            Packet packet = new(data.ReadAllBytesAsArray())
            {
                Protocol = message.Protocol,
                PacketType = (byte)message.PacketType,
            };

            p2PProtocolHandler.HandleMessage(packet);

            _session.DidNotReceive().InitiateDisconnect(DisconnectReason.MessageLimitsBreached, Arg.Any<string>());
        }

        [TestCase((byte)5, true)]
        [TestCase((byte)4, false)]
        public void Enables_snappy_for_v5_hello_before_rejecting_it(byte p2pVersion, bool snappy)
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            bool snappyEnabled = false;
            bool? snappyEnabledAtDisconnect = null;
            _session.When(static session => session.EnableSnappy()).Do(_ => snappyEnabled = true);
            _session.When(static session => session.InitiateDisconnect(DisconnectReason.NoCapabilityMatched, Arg.Any<string>()))
                .Do(_ => snappyEnabledAtDisconnect = snappyEnabled);

            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) },
                NodeId = TestItem.PublicKeyB,
                ClientId = "Nethermind/v1.0",
                ListenPort = 30303,
                P2PVersion = p2pVersion,
            };

            using DisposableByteBuffer data = _serializer.ZeroSerialize(message).AsDisposable();
            data.ReadByte(); // adaptive packet type

            p2PProtocolHandler.HandleMessage(new Packet(data.ReadAllBytesAsArray())
            {
                Protocol = message.Protocol,
                PacketType = (byte)message.PacketType,
            });

            Assert.That(snappyEnabledAtDisconnect, Is.EqualTo(snappy));
        }

        [Test]
        public void On_hello_carrying_this_nodes_own_identity_disconnects()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));

            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) },
                NodeId = TestItem.PublicKeyA,
                ClientId = "Nethermind/v1.0",
                ListenPort = 30303,
                P2PVersion = 5,
            };

            p2PProtocolHandler.HandleMessage(CreateP2PPacket(message));

            _session.Received(1).InitiateDisconnect(DisconnectReason.IdentitySameAsSelf, Arg.Any<string>());
            _session.DidNotReceive().InitiateDisconnect(DisconnectReason.NoCapabilityMatched, Arg.Any<string>());
        }

        [Test]
        public void On_hello_from_a_session_authenticated_as_this_node_disconnects()
        {
            _session.RemoteNodeId.Returns(TestItem.PublicKeyA);
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));

            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) },
                NodeId = TestItem.PublicKeyB,
                ClientId = "Nethermind/v1.0",
                ListenPort = 30303,
                P2PVersion = 5,
            };

            p2PProtocolHandler.HandleMessage(CreateP2PPacket(message));

            _session.Received(1).InitiateDisconnect(DisconnectReason.IdentitySameAsSelf, Arg.Any<string>());
        }

        [Test]
        public void On_hello_claiming_another_node_id_than_authenticated_disconnects()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));
            List<ProtocolEventArgs> requestedProtocols = [];
            p2PProtocolHandler.SubprotocolRequested += (_, args) => requestedProtocols.Add(args);

            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) },
                NodeId = TestItem.PublicKeyC,
                ClientId = "Nethermind/v1.0",
                ListenPort = 30303,
                P2PVersion = 5,
            };

            p2PProtocolHandler.HandleMessage(CreateP2PPacket(message));

            _session.Received(1).InitiateDisconnect(DisconnectReason.UnexpectedIdentity, Arg.Any<string>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(requestedProtocols, Is.Empty);
                Assert.That(p2PProtocolHandler.AgreedCapabilities, Is.Empty);
                Assert.That(p2PProtocolHandler.AvailableCapabilities, Is.Empty);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Hello_diagnostics_mask_peer_identity(bool isOutbound, bool mismatchedIdentity)
        {
            bool previous = SensitiveLogMasking.Enabled;
            SensitiveLogMasking.Enabled = true;
            try
            {
                TestLogger logger = new();
                P2PProtocolHandler handler = CreateSession(new OneLoggerLogManager(new(logger)));
                handler.AddSupportedCapability(new Capability(Protocol.Eth, 68));
                if (isOutbound) handler.Init();

                using HelloMessage hello = new()
                {
                    Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) },
                    NodeId = mismatchedIdentity ? TestItem.PublicKeyC : TestItem.PublicKeyB,
                    ClientId = "remote-client-secret",
                    ListenPort = 31417,
                    P2PVersion = 5,
                };

                handler.HandleMessage(CreateP2PPacket(hello));

                string diagnostic = logger.LogList.Single(line => line.Contains(mismatchedIdentity ? "Inconsistent Node ID details" : "P2P initiating"));
                string expectedDirection = mismatchedIdentity
                    ? isOutbound ? "OUT connection" : "IN connection"
                    : isOutbound ? "outbound" : "inbound";
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(diagnostic, Does.Contain("[redacted]"));
                    Assert.That(diagnostic, Does.Contain(expectedDirection));
                    Assert.That(diagnostic, Does.Not.Contain(TestItem.PublicKeyB.ToString()).And.Not.Contain(TestItem.PublicKeyC.ToString())
                        .And.Not.Contain("remote-client-secret").And.Not.Contain("31417"));
                }
            }
            finally
            {
                SensitiveLogMasking.Enabled = previous;
            }
        }

        [Test]
        public void Add_capability_requires_an_accepted_hello([Values] bool rejectedHello)
        {
            P2PProtocolHandler handler = CreateSession();
            Capability capability = new(Protocol.Eth, 68);
            handler.AddSupportedCapability(capability);
            int requestedCount = 0;
            handler.SubprotocolRequested += (_, _) => requestedCount++;
            if (rejectedHello)
            {
                using HelloMessage hello = CreateHello();
                hello.NodeId = TestItem.PublicKeyC;
                handler.HandleMessage(CreateP2PPacket(hello));
            }

            handler.HandleMessage(CreateP2PPacket(new AddCapabilityMessage(capability)));

            _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(requestedCount, Is.Zero);
                Assert.That(handler.AgreedCapabilities, Is.Empty);
                Assert.That(handler.AvailableCapabilities, Is.Empty);
            }
        }

        [Test]
        public void Hello_rejected_during_admission_does_not_start_subprotocols()
        {
            P2PProtocolHandler handler = CreateSession();
            handler.AddSupportedCapability(new Capability(Protocol.Eth, 68));
            int requestedCount = 0;
            handler.SubprotocolRequested += (_, _) => requestedCount++;
            handler.ProtocolInitialized += (_, _) => _session.IsClosing.Returns(true);
            using HelloMessage hello = CreateHello();

            handler.HandleMessage(CreateP2PPacket(hello));

            Assert.That(requestedCount, Is.Zero);
        }

        [Test]
        public void Duplicate_add_capability_is_ignored()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            Capability capability = new(Protocol.Eth, 68);
            int requestedCount = 0;

            p2PProtocolHandler.AddSupportedCapability(capability);
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 66));
            using HelloMessage hello = CreateHello();
            hello.Capabilities.Dispose();
            hello.Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 66) };
            p2PProtocolHandler.HandleMessage(CreateP2PPacket(hello));
            p2PProtocolHandler.SubprotocolRequested += (_, _) => requestedCount++;

            p2PProtocolHandler.HandleMessage(CreateP2PPacket(new AddCapabilityMessage(capability)));
            p2PProtocolHandler.HandleMessage(CreateP2PPacket(new AddCapabilityMessage(capability)));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(requestedCount, Is.EqualTo(1));
                Assert.That(p2PProtocolHandler.AgreedCapabilities.Count(c => c.Equals(capability)), Is.EqualTo(1));
            }
        }

        [Test]
        public void Hello_starts_highest_agreed_eth_version_only()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            List<ProtocolEventArgs> requestedProtocols = [];

            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 69));
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 70));
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 71));

            p2PProtocolHandler.SubprotocolRequested += (_, args) => requestedProtocols.Add(args);

            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(4)
                {
                    new(Protocol.Eth, 68),
                    new(Protocol.Eth, 69),
                    new(Protocol.Eth, 70),
                    new(Protocol.Eth, 71),
                },
                NodeId = TestItem.PublicKeyB,
            };

            p2PProtocolHandler.HandleMessage(CreateP2PPacket(message));

            Assert.That(requestedProtocols, Has.Exactly(1).Items);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(requestedProtocols[0].ProtocolCode, Is.EqualTo(Protocol.Eth));
                Assert.That(requestedProtocols[0].Version, Is.EqualTo(71));
            }
        }

        [Test]
        public void Repeated_hello_disconnects_with_breach_of_protocol()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));
            List<ProtocolEventArgs> requestedProtocols = [];
            p2PProtocolHandler.SubprotocolRequested += (_, args) => requestedProtocols.Add(args);

            using HelloMessage firstHello = CreateHello();
            using HelloMessage secondHello = CreateHello();

            p2PProtocolHandler.HandleMessage(CreateP2PPacket(firstHello));
            p2PProtocolHandler.HandleMessage(CreateP2PPacket(secondHello));

            using (Assert.EnterMultipleScope())
            {
                _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
                Assert.That(requestedProtocols, Has.Exactly(1).Items,
                    "the repeated Hello must not re-run subprotocol negotiation");
                Assert.That(p2PProtocolHandler.AgreedCapabilities.Count(c => c.Equals(new Capability(Protocol.Eth, 68))), Is.EqualTo(1),
                    "the repeated Hello must not append a duplicate agreed capability");
            }
        }

        [Test]
        public void Second_hello_is_rejected_even_when_handling_the_first_throws()
        {
            // Pins that _receivedHello is set before HandleHello runs.
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));

            bool throwOnNextNotification = true;
            p2PProtocolHandler.ProtocolInitialized += (_, _) =>
            {
                if (throwOnNextNotification)
                {
                    throwOnNextNotification = false;
                    throw new InvalidOperationException("simulated failure while handling the first Hello");
                }
            };

            using HelloMessage firstHello = CreateHello();
            using HelloMessage secondHello = CreateHello();

            Assert.Throws<InvalidOperationException>(() => p2PProtocolHandler.HandleMessage(CreateP2PPacket(firstHello)));
            p2PProtocolHandler.HandleMessage(CreateP2PPacket(secondHello));

            using (Assert.EnterMultipleScope())
            {
                _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
                Assert.That(p2PProtocolHandler.AgreedCapabilities.Count(c => c.Equals(new Capability(Protocol.Eth, 68))), Is.EqualTo(1),
                    "the faulted first Hello must not be re-applied by the second");
            }
        }

        [Test]
        public void Malformed_repeated_hello_disconnects_without_deserializing()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));

            using HelloMessage firstHello = CreateHello();
            p2PProtocolHandler.HandleMessage(CreateP2PPacket(firstHello));

            Packet malformedSecondHello = new(CreateMalformedHelloData())
            {
                Protocol = Protocol.P2P,
                PacketType = P2PMessageCode.Hello,
            };

            Assert.DoesNotThrow(() => p2PProtocolHandler.HandleMessage(malformedSecondHello));

            _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
        }

        [Test]
        public void Second_hello_is_rejected_after_the_first_fails_to_deserialize()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));
            List<ProtocolEventArgs> requestedProtocols = [];
            p2PProtocolHandler.SubprotocolRequested += (_, args) => requestedProtocols.Add(args);

            Packet malformedFirstHello = new(CreateMalformedHelloData())
            {
                Protocol = Protocol.P2P,
                PacketType = P2PMessageCode.Hello,
            };
            using HelloMessage secondHello = CreateHello();

            Assert.Catch<RlpReaderException>(() => p2PProtocolHandler.HandleMessage(malformedFirstHello));
            Assert.DoesNotThrow(() => p2PProtocolHandler.HandleMessage(CreateP2PPacket(secondHello)));

            using (Assert.EnterMultipleScope())
            {
                _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
                Assert.That(requestedProtocols, Is.Empty,
                    "a Hello arriving after a malformed first attempt must not be negotiated");
            }
        }

        [Test]
        public void Snap_requires_a_primary_protocol([Values] bool hasEth)
        {
            using P2PProtocolHandler handler = CreateSession();
            Capability snap = new(Protocol.Snap, 1);
            Capability eth = new(Protocol.Eth, 68);
            handler.AddSupportedCapability(snap);
            handler.AddSupportedCapability(eth);
            ArrayPoolList<Capability> capabilities = [with(2), snap];
            if (hasEth) capabilities.Add(eth);
            using HelloMessage hello = new()
            {
                Capabilities = capabilities,
                NodeId = TestItem.PublicKeyB,
            };

            handler.HandleMessage(CreateP2PPacket(hello));

            _session.Received(hasEth ? 0 : 1).InitiateDisconnect(DisconnectReason.NoCapabilityMatched, Arg.Any<string>());
        }

        [Test]
        public void Too_many_add_capability_messages_disconnect()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Eth, 68));
            using HelloMessage hello = CreateHello();
            p2PProtocolHandler.HandleMessage(CreateP2PPacket(hello));

            for (int i = 0; i < 63; i++)
            {
                p2PProtocolHandler.HandleMessage(CreateP2PPacket(new AddCapabilityMessage(new Capability($"cap{i}", 1))));
            }

            p2PProtocolHandler.HandleMessage(CreateP2PPacket(new AddCapabilityMessage(new Capability("overflow", 1))));

            _session.Received(1).InitiateDisconnect(DisconnectReason.MessageLimitsBreached, Arg.Any<string>());
        }

        /// <summary>
        /// Creates a Packet from a P2P message by stripping the adaptive type byte,
        /// matching what the real network path produces for P2PProtocolHandler.HandleMessage(Packet).
        /// </summary>
        private Packet CreateP2PPacket<T>(T message) where T : P2PMessage
        {
            using DisposableByteBuffer data = _serializer.ZeroSerialize(message).AsDisposable();
            data.ReadByte();
            return new Packet(data.ReadAllBytesAsArray())
            {
                Protocol = message.Protocol,
                PacketType = (byte)message.PacketType,
            };
        }

        private static HelloMessage CreateHello() => new()
        {
            Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) },
            NodeId = TestItem.PublicKeyB,
        };

        /// <summary>
        /// A valid Hello, truncated mid-stream so its RLP list header no longer matches the payload -
        /// deserializing it throws, which is what the repeated-Hello guard must avoid triggering.
        /// </summary>
        private byte[] CreateMalformedHelloData()
        {
            using HelloMessage hello = CreateHello();
            using DisposableByteBuffer data = _serializer.ZeroSerialize(hello).AsDisposable();
            data.ReadByte(); // adaptive packet type
            byte[] full = data.ReadAllBytesAsArray();
            return full[..(full.Length / 2)];
        }
        internal static void HandleHello(P2PProtocolHandler handler, MessageSerializationService serializer, HelloMessage hello)
        {
            using DisposableByteBuffer encoded = serializer.ZeroSerialize(hello).AsDisposable();
            encoded.ReadByte();
            handler.HandleMessage(new Packet(new NettyBufferPacket(encoded) { PacketType = P2PMessageCode.Hello, Protocol = Protocol.P2P }));
        }

        [Test]
        public async Task Hello_deadline_respects_direction_and_already_validated_messages([Values] bool inbound, [Values] bool hello)
        {
            using ISession session = Substitute.For<ISession>();
            session.RemoteNodeId.Returns(TestItem.PublicKeyB);
            session.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303));
            session.Direction.Returns(inbound ? ConnectionDirection.In : ConnectionDirection.Out);
            session.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(call => call.Arg<P2PMessage>().Dispose());
            TaskCompletionSource<DisconnectReason> disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.When(s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
                .Do(call => disconnected.TrySetResult(call.Arg<DisconnectReason>()));
            ManualTimeProvider clock = new();
            MessageSerializationService serializer = new(SerializerInfo.Create(new HelloMessageSerializer()));
            using P2PProtocolHandler handler = new(session, new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303),
                Substitute.For<INodeStatsManager>(), serializer, Substitute.For<IBackgroundTaskScheduler>(), LimboLogs.Instance, new NetworkConfig(), clock);
            handler.AddSupportedCapability(new Capability(Protocol.Eth, 66));
            QueuedContext continuation = new();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(continuation);
                handler.Init();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            Assert.That(clock.DueTimes[^1], Is.EqualTo(TimeSpan.FromSeconds(inbound ? 2 : 3)));
            clock.AdvanceAndFireTimer(clock.DueTimes[^1]);
            await continuation.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (hello)
            {
                using HelloMessage message = new()
                {
                    NodeId = TestItem.PublicKeyB,
                    P2PVersion = 5,
                    ClientId = "honest peer",
                    ListenPort = 30303,
                    Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 66) }
                };
                HandleHello(handler, serializer, message);
            }
            continuation.Run();
            if (hello)
            {
                session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            else
            {
                Assert.That(await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(DisconnectReason.ProtocolInitTimeout));
            }
        }

        private sealed class QueuedContext : SynchronizationContext
        {
            private Action? _continuation;
            public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override void Post(SendOrPostCallback callback, object? state)
            {
                _continuation = () => callback(state);
                Posted.TrySetResult();
            }
            public void Run() => _continuation!();
        }

        [Test]
        public async Task Credits_the_session_with_a_pong_that_arrives_after_the_ping_timed_out([Values(5_000, 20_000)] int interval)
        {
            using Context context = new(interval);
            await context.TimeoutPing();
            context.Time.Advance(TimeSpan.FromSeconds(1));
            context.Pong();

            Assert.That(context.Session.LastPongUtc, Is.EqualTo(context.Time.GetUtcNow().UtcDateTime),
                "a pong after the per-ping timeout still proves the peer answers; the session monitor measures its disconnect window from this stamp (#12811)");
        }

        [TestCase(5_000, 4_999, true)]
        [TestCase(5_000, 5_000, false)]
        [TestCase(5_000, 6_000, false)]
        [TestCase(null, 9_999, true)]
        [TestCase(null, 10_000, false)]
        [TestCase(null, 11_000, false)]
        [TestCase(20_000, 19_999, true)]
        [TestCase(20_000, 20_000, false)]
        public async Task Late_credit_expires_at_the_configured_interval_from_send(int? interval, int elapsed, bool accepted)
        {
            using Context context = new(interval);
            await context.TimeoutPing();
            context.Time.Advance(TimeSpan.FromMilliseconds(elapsed) - TimeSpan.FromSeconds(3));
            context.Pong();

            Assert.That(context.Session.LastPongUtc, Is.EqualTo(accepted ? context.Time.GetUtcNow().UtcDateTime : DateTime.UnixEpoch));
        }

        [Test]
        public void Unsolicited_pong_does_not_refresh_liveness()
        {
            using Context context = new(5_000);
            context.Time.Advance(TimeSpan.FromSeconds(1));
            context.Pong();
            Assert.That(context.Session.LastPongUtc, Is.EqualTo(DateTime.UnixEpoch));
        }

        [Test]
        public async Task A_ping_can_credit_only_one_pong([Values] bool afterTimeout)
        {
            using Context context = new(5_000);
            Task<bool> ping = context.Handler.SendPing();
            if (afterTimeout)
            {
                context.Time.AdvanceAndFireTimer(TimeSpan.FromSeconds(3));
                Assert.That(await ping.WaitAsync(TimeSpan.FromSeconds(2)), Is.False);
            }
            context.Time.Advance(TimeSpan.FromMilliseconds(100));
            context.Pong();
            DateTime credited = context.Time.GetUtcNow().UtcDateTime;
            Assert.That(context.Session.LastPongUtc, Is.EqualTo(credited));
            if (!afterTimeout) Assert.That(await ping.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
            context.Time.Advance(TimeSpan.FromMilliseconds(100));
            context.Pong();
            Assert.That(context.Session.LastPongUtc, Is.EqualTo(credited));
        }

        private sealed class Context : IDisposable
        {
            private readonly IContainer _container;
            public ManualTimeProvider Time { get; } = new();
            public ISession Session { get; } = Substitute.For<ISession>();
            public P2PProtocolHandler Handler { get; }

            public Context(int? interval)
            {
                Session.Node.Returns(new Node(TestItem.PublicKeyB, IPAddress.Loopback.ToString(), 30303));
                Session.LastPongUtc = DateTime.UnixEpoch;
                ContainerBuilder builder = new();
                builder.RegisterInstance<IEnode>(new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303));
                builder.RegisterInstance(Substitute.For<INodeStatsManager>());
                builder.RegisterInstance<IMessageSerializationService>(new MessageSerializationService(SerializerInfo.Create(new PingMessageSerializer())));
                builder.RegisterInstance(Substitute.For<IBackgroundTaskScheduler>());
                builder.RegisterInstance<ILogManager>(LimboLogs.Instance);
                builder.RegisterInstance<TimeProvider>(Time);
                NetworkConfig config = new();
                if (interval is { } configuredInterval) config.P2PPingInterval = configuredInterval;
                else Assert.That(config.P2PPingInterval, Is.EqualTo(10_000));
                builder.RegisterInstance<INetworkConfig>(config);
                _container = builder.Build();
                AutofacProtocolHandlerFactory<P2PProtocolHandler> factory = new(_container, Protocol.P2P);
                Assert.That(factory.TryCreate(Session, 5, out IProtocolHandler? handler), Is.True);
                Handler = (P2PProtocolHandler)handler!;
            }

            public async Task TimeoutPing()
            {
                Task<bool> ping = Handler.SendPing();
                Time.AdvanceAndFireTimer(TimeSpan.FromSeconds(3));
                Assert.That(await ping.WaitAsync(TimeSpan.FromSeconds(2)), Is.False);
            }

            public void Pong() => Handler.HandleMessage(new Packet([]) { Protocol = Protocol.P2P, PacketType = P2PMessageCode.Pong });

            public void Dispose()
            {
                Handler.Dispose();
                Session.Dispose();
                _container.Dispose();
            }
        }
    }
}
