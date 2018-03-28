/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class P2PSessionTests
    {
        [SetUp]
        public void Setup()
        {
            _packetSender = Substitute.For<IPacketSender>();
            _sessionManager = Substitute.For<ISessionManager>();
            _serializer = Substitute.For<IMessageSerializationService>();
        }

        private IPacketSender _packetSender;
        private ISessionManager _sessionManager;
        private IMessageSerializationService _serializer;

        private Packet CreatePacket(P2PMessage message)
        {
            return new Packet(message.Protocol, message.PacketType, _serializer.Serialize(message));
        }

        [Test]
        public void On_init_sends_a_hello_message()
        {
            P2PSession p2PSession = CreateSession();
            p2PSession.Init();

            _packetSender.Received(1).Enqueue(Arg.Is<Packet>(p => p.PacketType == P2PMessageCode.Hello));
        }

        [Test]
        public void Pongs_to_ping()
        {
            P2PSession p2PSession = CreateSession();
            p2PSession.HandleMessage(CreatePacket(PingMessage.Instance));
            _packetSender.Received(1).Enqueue(Arg.Is<Packet>(p => p.PacketType == P2PMessageCode.Pong));
        }

        [Test]
        public void Sets_local_node_id_from_constructor()
        {
            P2PSession p2PSession = CreateSession();
            Assert.AreEqual(p2PSession.LocalNodeId, NetTestVectors.StaticKeyA.PublicKey);
        }

        [Test]
        public void Sets_port_from_constructor()
        {
            P2PSession p2PSession = CreateSession();
            Assert.AreEqual(p2PSession.ListenPort, 8002);
        }

        private P2PSession CreateSession()
        {
            return new P2PSession(
                _sessionManager,
                _serializer,
                _packetSender,
                NetTestVectors.StaticKeyA.PublicKey,
                8002,
                NetTestVectors.StaticKeyB.PublicKey,
                new NullLogger());
        }
    }
}