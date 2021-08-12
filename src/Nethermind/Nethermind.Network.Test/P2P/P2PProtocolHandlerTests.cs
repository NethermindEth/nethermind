//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
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
            return new Packet(message.Protocol, message.PacketType, _serializer.Serialize(message));
        }

        private const int ListenPort = 8003;

        private P2PProtocolHandler CreateSession()
        {
            _session.LocalPort.Returns(ListenPort);
            Node node = new Node("127.0.0.1", 30303, false);
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

            string[] expectedCapabilities = {"eth62", "eth63", "eth64", "eth65", "wit0"};
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
