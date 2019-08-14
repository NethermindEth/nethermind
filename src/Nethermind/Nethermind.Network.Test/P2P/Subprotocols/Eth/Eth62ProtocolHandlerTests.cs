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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class Eth62ProtocolHandlerTests
    {
        [Test]
        public void Get_headers_from_genesis()
        {
            var svc = Build.A.SerializationService().WithEth().TestObject;
            
            var session = Substitute.For<ISession>();
            var syncManager = Substitute.For<ISyncServer>();
            var transactionPool = Substitute.For<ITxPool>();
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            syncManager.Head.Returns(genesisBlock.Header);
            syncManager.Genesis.Returns(genesisBlock.Header);
            var handler = new Eth62ProtocolHandler(
                session,
                svc,
                new NodeStatsManager(new StatsConfig(), LimboLogs.Instance),
                syncManager,
                LimboLogs.Instance,
                new PerfService(LimboLogs.Instance),
                transactionPool);
            handler.Init();
            
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockHash = TestItem.KeccakA;
            msg.MaxHeaders = 3;
            msg.Skip = 1;
            msg.Reverse = 1;
            
            var statusMsg = new StatusMessage();
            statusMsg.GenesisHash = genesisBlock.Hash;
            
            handler.HandleMessage(new Packet(Protocol.Eth, statusMsg.PacketType, svc.Serialize(statusMsg)));
            handler.HandleMessage(new Packet(Protocol.Eth, msg.PacketType, svc.Serialize(msg)));
            syncManager.Received().FindHeaders(TestItem.KeccakA, 3, 1, true);
        }
        
        [Test]
        public void Get_headers_when_blocks_are_missing()
        {
            var svc = Build.A.SerializationService().WithEth().TestObject;
            
            var session = Substitute.For<ISession>();
            var syncManager = Substitute.For<ISyncServer>();
            var transactionPool = Substitute.For<ITxPool>();
            syncManager.FindHeaders(null, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>()).Throws(new ArgumentNullException());
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            syncManager.Head.Returns(genesisBlock.Header);
            syncManager.Genesis.Returns(genesisBlock.Header);
            var handler = new Eth62ProtocolHandler(
                session,
                svc,
                new NodeStatsManager(new StatsConfig(), LimboLogs.Instance),
                syncManager,
                LimboLogs.Instance,
                new PerfService(LimboLogs.Instance),
                transactionPool);
            handler.Init();
            
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockNumber = 1920000;
            msg.MaxHeaders = 3;
            msg.Skip = 1;
            msg.Reverse = 1;
            
            var statusMsg = new StatusMessage();
            statusMsg.GenesisHash = genesisBlock.Hash;
            
            handler.HandleMessage(new Packet(Protocol.Eth, statusMsg.PacketType, svc.Serialize(statusMsg)));
            handler.HandleMessage(new Packet(Protocol.Eth, msg.PacketType, svc.Serialize(msg)));
            syncManager.Received().FindHash(1920000);
        }
    }
}