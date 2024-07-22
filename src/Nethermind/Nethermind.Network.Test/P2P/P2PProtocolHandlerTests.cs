// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class P2PProtocolHandlerTests
    {
        [SetUp]
        public void Setup()
        {
            _session = Substitute.For<ISession>();
            _serializer = new MessageSerializationService();
            _serializer.Register(new HelloMessageSerializer());
            _serializer.Register(new PingMessageSerializer());
        }

        [TearDown]
        public void TearDown() => _session?.Dispose();

        private ISession _session;
        private IMessageSerializationService _serializer;
        private readonly Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        private INodeStatsManager _nodeStatsManager;
        private Regex? _clientIdPattern;

        private Packet CreatePacket<T>(T message) where T : P2PMessage
        {
            return new(new ZeroPacket(_serializer.ZeroSerialize(message))
            {
                Protocol = message.Protocol,
                PacketType = (byte)message.PacketType,
            });
        }

        private const int ListenPort = 8003;

        private P2PProtocolHandler CreateSession()
        {
            _session.LocalPort.Returns(ListenPort);
            _session.Node.Returns(node);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _nodeStatsManager = new NodeStatsManager(timerFactory, LimboLogs.Instance);

            return new P2PProtocolHandler(
                _session,
                TestItem.PublicKeyA,
                _nodeStatsManager,
                _serializer,
                _clientIdPattern,
                LimboLogs.Instance);
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
            p2PProtocolHandler.Init();

            string[] expectedCapabilities = ["eth66", "eth67", "eth68", "nodedata1"];
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
                NodeId = TestItem.PublicKeyA,
            };

            IByteBuffer data = _serializer.ZeroSerialize(message);
            // to account for adaptive packet type
            data.ReadByte();

            Packet packet = new Packet(data.ReadAllBytesAsArray())
            {
                Protocol = message.Protocol,
                PacketType = (byte)message.PacketType,
            };

            p2PProtocolHandler.HandleMessage(packet);

            _nodeStatsManager.GetOrAdd(node).FailedCompatibilityValidation.Should().NotBeNull();
            _session.Received(1).InitiateDisconnect(DisconnectReason.NoCapabilityMatched, Arg.Any<string>());
        }

        [TestCase("besu", "besu/v23.4.0/linux-x86_64/openjdk-java-17", false)]
        [TestCase("besu", "Geth/v1.12.1-unstable-b8d7da87-20230808/linux-amd64/go1.19.2", true)]
        [TestCase("^((?!besu).)*$", "Geth/v1.12.1-unstable-b8d7da87-20230808/linux-amd64/go1.19.2", false)]
        [TestCase("^((?!besu).)*$", "besu/v23.4.0/linux-x86_64/openjdk-java-17", true)]
        public void On_hello_with_not_matching_client_id(string pattern, string clientId, bool shouldDisconnect)
        {
            _clientIdPattern = new Regex(pattern);
            P2PProtocolHandler p2PProtocolHandler = CreateSession();

            using HelloMessage message = new()
            {
                Capabilities = new ArrayPoolList<Capability>(1) { new Capability(Protocol.Eth, 63) },
                NodeId = TestItem.PublicKeyA,
                ClientId = clientId,
            };

            IByteBuffer data = _serializer.ZeroSerialize(message);
            // to account for adaptive packet type
            data.ReadByte();

            Packet packet = new Packet(data.ReadAllBytesAsArray())
            {
                Protocol = message.Protocol,
                PacketType = (byte)message.PacketType,
            };

            p2PProtocolHandler.HandleMessage(packet);

            if (shouldDisconnect)
            {
                _session.Received(1).InitiateDisconnect(DisconnectReason.ClientFiltered, Arg.Any<string>());
            }
            else
            {
                _session.DidNotReceive().InitiateDisconnect(DisconnectReason.ClientFiltered, Arg.Any<string>());
            }
        }

        [Test]
        public void Pongs_to_ping()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.HandleMessage(CreatePacket(PingMessage.Instance));
            _session.Received(1).DeliverMessage(Arg.Any<PongMessage>());
        }

        [Test]
        public void Sets_local_node_id_from_constructor()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            Assert.That(TestItem.PublicKeyA, Is.EqualTo(p2PProtocolHandler.LocalNodeId));
        }

        [Test]
        public void Sets_port_from_constructor()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            Assert.That(p2PProtocolHandler.ListenPort, Is.EqualTo(ListenPort));
        }
    }
}
