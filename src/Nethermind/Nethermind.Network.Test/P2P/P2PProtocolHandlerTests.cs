// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
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
            _serializer = Substitute.For<IMessageSerializationService>();
        }

        private ISession _session;
        private IMessageSerializationService _serializer;

        private Packet CreatePacket(P2PMessage message)
        {
            return new(message.Protocol, message.PacketType, _serializer.Serialize(message));
        }

        private const int ListenPort = 8003;

        private P2PProtocolHandler CreateSession()
        {
            _session.LocalPort.Returns(ListenPort);
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            _session.Node.Returns(node);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            return new P2PProtocolHandler(
                _session,
                TestItem.PublicKeyA,
                new NodeStatsManager(timerFactory, LimboLogs.Instance),
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

            string[] expectedCapabilities = { "eth66", "wit0" };
            _session.Received(1).DeliverMessage(
                Arg.Is<HelloMessage>(m => m.Capabilities.Select(c => c.ToString()).SequenceEqual(expectedCapabilities)));
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
            Assert.AreEqual(p2PProtocolHandler.LocalNodeId, TestItem.PublicKeyA);
        }

        [Test]
        public void Sets_port_from_constructor()
        {
            P2PProtocolHandler p2PProtocolHandler = CreateSession();
            Assert.AreEqual(ListenPort, p2PProtocolHandler.ListenPort);
        }
    }
}
