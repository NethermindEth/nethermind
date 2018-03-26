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
            _messageSender = Substitute.For<IMessageSender>();
        }

        private IMessageSender _messageSender;

        [Test]
        public void Can_ping()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002, new NullLogger());
            session.Ping();
            _messageSender.Received(1).Enqueue(Arg.Any<PingMessage>());
        }

        [Test]
        public void On_init_outbound_sends_a_hello_message()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002, new NullLogger());
            session.InitOutbound();

            _messageSender.Received(1).Enqueue(Arg.Any<HelloMessage>());
        }

        [Test]
        public void Pongs_to_ping()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002, new NullLogger());
            session.HandlePing();
            _messageSender.Received(1).Enqueue(Arg.Any<PongMessage>());
        }

        [Test]
        public void Sets_local_node_id_from_constructor()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002, new NullLogger());
            Assert.AreEqual(session.LocalNodeId, NetTestVectors.StaticKeyA.PublicKey);
        }

        [Test]
        public void Sets_port_from_constructor()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002, new NullLogger());
            Assert.AreEqual(session.ListenPort, 8002);
        }
    }
}