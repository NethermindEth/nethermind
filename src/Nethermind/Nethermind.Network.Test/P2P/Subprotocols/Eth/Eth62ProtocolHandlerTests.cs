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

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class Eth62ProtocolHandlerTests
    {
        [Test]
        public void Test()
        {
            var svc = Build.A.SerializationService().WithEth().TestObject;
            
            var session = Substitute.For<IP2PSession>();
            var syncManager = Substitute.For<ISynchronizationManager>();
            var handler = new Eth62ProtocolHandler(session, svc, syncManager, NullLogger.Instance);
            handler.Init();
            
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockHash = TestObject.KeccakA;
            msg.MaxHeaders = 3;
            msg.Skip = 1;
            msg.Reverse = 1;
            
            var statusMsg = new StatusMessage();
            
            handler.HandleMessage(new Packet(Protocol.Eth, statusMsg.PacketType, svc.Serialize(statusMsg)));
            handler.HandleMessage(new Packet(Protocol.Eth, msg.PacketType, svc.Serialize(msg)));
            syncManager.Received().Find(TestObject.KeccakA, 3, 1, true);
        }
    }
}