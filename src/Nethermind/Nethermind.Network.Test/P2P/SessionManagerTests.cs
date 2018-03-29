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

using System;
using Nethermind.Core;
using Nethermind.Network.P2P;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class SessionManagerTests
    {
        private const int ListenPort = 8002;

        [Test]
        public void Can_start_p2p_session()
        {
            SessionManager factory = new SessionManager(Substitute.For<IMessageSerializationService>(), NetTestVectors.StaticKeyA.PublicKey, ListenPort, new NullLogger());
            factory.Start("p2p", 5, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003);
        }

        [Test]
        public void Cannot_start_eth_before_p2p()
        {
            SessionManager factory = new SessionManager(Substitute.For<IMessageSerializationService>(), NetTestVectors.StaticKeyA.PublicKey, ListenPort, new NullLogger());
            Assert.Throws<InvalidOperationException>(() => factory.Start("eth", 62, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003));
        }
        
        [Test]
        public void Can_start_eth_session()
        {
            SessionManager factory = new SessionManager(Substitute.For<IMessageSerializationService>(), NetTestVectors.StaticKeyA.PublicKey, ListenPort, new NullLogger());
            factory.Start("p2p", 5, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003);
            factory.Start("eth", 62, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003);
        }
        
        [TestCase(100, null, 0)]
        [TestCase(1, "p2p", 1)]
        [TestCase(15, "p2p", 15)]
        [TestCase(16, "eth", 0)]
        public void Adaptive_message_ids(int dynamicId, string protocolCode, int messageCode)
        {        
            SessionManager factory = new SessionManager(Substitute.For<IMessageSerializationService>(), NetTestVectors.StaticKeyA.PublicKey, ListenPort, new NullLogger());
            factory.Start("p2p", 5, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003);
            factory.Start("eth", 62, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003);

            (string resolvedProtocolCode, int resolvedMessageId) = factory.ResolveMessageCode(dynamicId);
            Assert.AreEqual(protocolCode, resolvedProtocolCode, "protocol code");
            Assert.AreEqual(messageCode, resolvedMessageId, "message code");
            
        }
    }
}