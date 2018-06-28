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

using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class P2PSessionTests
    {
        private const int ListenPort = 8002;

        [Test]
        public void Can_start_p2p_session()
        {
            IPacketSender sender = Substitute.For<IPacketSender>();

            IMessageSerializationService service = Build.A.SerializationService().WithP2P().TestObject;
            
            P2PSession factory = new P2PSession(new NodeId(NetTestVectors.StaticKeyA.PublicKey), ListenPort, service, Substitute.For<ISynchronizationManager>(), NullLogManager.Instance);
            factory.Init(4, Substitute.For<IChannelHandlerContext>(), sender);
            
            sender.Received().Enqueue(Arg.Is<Packet>(p => p.PacketType == 0 && p.Protocol == "p2p"));
        }
        
//        [Test]
//        public void Can_start_eth_session()
//        {
//            P2PSession factory = new P2PSession(Substitute.For<IMessageSerializationService>(), NetTestVectors.StaticKeyA.PublicKey, ListenPort, NullLogger.Instance, Substitute.For<ISynchronizationManager>());
//            factory.StartSession("p2p", 5, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003);
//            factory.StartSession("eth", 62, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003);
//        }
//        
//        [TestCase(100, null, 0)]
//        [TestCase(1, "p2p", 1)]
//        [TestCase(15, "p2p", 15)]
//        [TestCase(16, "eth", 0)]
//        public void Adaptive_message_ids(int dynamicId, string protocolCode, int messageCode)
//        {        
//            P2PSession factory = new P2PSession(Substitute.For<IMessageSerializationService>(), NetTestVectors.StaticKeyA.PublicKey, ListenPort, NullLogger.Instance, Substitute.For<ISynchronizationManager>());
//            factory.StartSession("p2p", 5, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003);
//            factory.StartSession("eth", 62, Substitute.For<IPacketSender>(), NetTestVectors.StaticKeyB.PublicKey, 8003);
//
//            (string resolvedProtocolCode, int resolvedMessageId) = factory.ResolveMessageCode(dynamicId);
//            Assert.AreEqual(protocolCode, resolvedProtocolCode, "protocol code");
//            Assert.AreEqual(messageCode, resolvedMessageId, "message code");
//        }
    }
}