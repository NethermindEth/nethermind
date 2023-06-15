// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using FluentAssertions;
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

        private ISession _session;
        private IMessageSerializationService _serializer;
        private Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        private INodeStatsManager _nodeStatsManager;

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
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Wit, 0));
            p2PProtocolHandler.Init();

            string[] expectedCapabilities = { "eth66", "eth67", "eth68", "nodedata1", "wit0" };
            _session.Received(1).DeliverMessage(
                Arg.Is<HelloMessage>(m => m.Capabilities.Select(c => c.ToString()).SequenceEqual(expectedCapabilities)));
        }

        [Test]
        public void On_hello_with_no_matching_capability()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            p2PProtocolHandler.AddSupportedCapability(new Capability(Protocol.Wit, 66));

            HelloMessage message = new HelloMessage()
            {
                Capabilities = new List<Capability>()
                {
                    new Capability(Protocol.Eth, 63)
                },
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
            _session.Received(1).InitiateDisconnect(InitiateDisconnectReason.NoCapabilityMatched, Arg.Any<string>());
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
