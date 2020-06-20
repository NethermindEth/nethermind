﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using DotNetty.Buffers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [Parallelizable(ParallelScope.Self)]
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
                transactionPool, LimboLogs.Instance);
            handler.Init();
            
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockHash = TestItem.KeccakA;
            msg.MaxHeaders = 3;
            msg.Skip = 1;
            msg.Reverse = 1;
            
            var statusMsg = new StatusMessage();
            statusMsg.GenesisHash = genesisBlock.Hash;

            IByteBuffer statusPacket = svc.ZeroSerialize(statusMsg);
            statusPacket.ReadByte();

            IByteBuffer getBlockHeadersPacket = svc.ZeroSerialize(msg);
            getBlockHeadersPacket.ReadByte();
            
            handler.HandleMessage(new ZeroPacket(statusPacket){PacketType = 0});
            handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket){PacketType = Eth62MessageCode.GetBlockHeaders});
            syncManager.Received().FindHeaders(TestItem.KeccakA, 3, 1, true);
        }
        
        [Test]
        public void Get_headers_when_blocks_are_missing_at_the_end()
        {
            var svc = Build.A.SerializationService().WithEth().TestObject;
            
            var headers = new BlockHeader[5];
            headers[0] = Build.A.BlockHeader.TestObject;
            headers[1] = Build.A.BlockHeader.TestObject;
            headers[2] = Build.A.BlockHeader.TestObject;
            
            var session = Substitute.For<ISession>();
            var syncManager = Substitute.For<ISyncServer>();
            var transactionPool = Substitute.For<ITxPool>();
            syncManager.FindHash(100).Returns(TestItem.KeccakA);
            syncManager.FindHeaders(TestItem.KeccakA, 5, 1, true)
                .Returns(headers);
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            syncManager.Head.Returns(genesisBlock.Header);
            syncManager.Genesis.Returns(genesisBlock.Header);
            var handler = new Eth62ProtocolHandler(
                session,
                svc,
                new NodeStatsManager(new StatsConfig(), LimboLogs.Instance),
                syncManager,
                transactionPool, LimboLogs.Instance);
            handler.Init();
            
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockNumber = 100;
            msg.MaxHeaders = 5;
            msg.Skip = 1;
            msg.Reverse = 1;
            
            var statusMsg = new StatusMessage();
            statusMsg.GenesisHash = genesisBlock.Hash;
            
            IByteBuffer statusPacket = svc.ZeroSerialize(statusMsg);
            statusPacket.ReadByte();

            IByteBuffer getBlockHeadersPacket = svc.ZeroSerialize(msg);
            getBlockHeadersPacket.ReadByte();
            
            handler.HandleMessage(new ZeroPacket(statusPacket){PacketType = 0});
            handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket){PacketType = Eth62MessageCode.GetBlockHeaders});
            
            session.Received().DeliverMessage(Arg.Is<BlockHeadersMessage>(bhm => bhm.BlockHeaders.Length == 3));
            syncManager.Received().FindHash(100);
        }
        
        [Test, Ignore("Disabling it for now")]
        public void Hardcoded_1920000_works_fine()
        {
            var svc = Build.A.SerializationService().WithEth().TestObject;
            
            var headers = new BlockHeader[5];
            headers[0] = Build.A.BlockHeader.TestObject;
            headers[1] = Build.A.BlockHeader.TestObject;
            headers[2] = Build.A.BlockHeader.TestObject;
            
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
                transactionPool, LimboLogs.Instance);
            handler.Init();
            
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockNumber = 1920000;
            msg.MaxHeaders = 1;
            msg.Skip = 1;
            msg.Reverse = 1;
            
            var statusMsg = new StatusMessage();
            statusMsg.GenesisHash = genesisBlock.Hash;
            
            handler.HandleMessage(new Packet(Protocol.Eth, statusMsg.PacketType, svc.Serialize(statusMsg)));
            handler.HandleMessage(new Packet(Protocol.Eth, msg.PacketType, svc.Serialize(msg)));
            session.Received().DeliverMessage(Arg.Is<BlockHeadersMessage>(bhm => bhm.BlockHeaders.Length == 1));
        }
        
        [Test]
        public void Get_headers_when_blocks_are_missing_in_the_middle()
        {
            var svc = Build.A.SerializationService().WithEth().TestObject;
            
            var headers = new BlockHeader[5];
            headers[0] = Build.A.BlockHeader.TestObject;
            headers[1] = Build.A.BlockHeader.TestObject;
            headers[2] = null;
            headers[3] = Build.A.BlockHeader.TestObject;
            headers[4] = Build.A.BlockHeader.TestObject;
            
            var session = Substitute.For<ISession>();
            var syncManager = Substitute.For<ISyncServer>();
            var transactionPool = Substitute.For<ITxPool>();
            syncManager.FindHash(100).Returns(TestItem.KeccakA);
            syncManager.FindHeaders(TestItem.KeccakA, 5, 1, true)
                .Returns(headers);
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            syncManager.Head.Returns(genesisBlock.Header);
            syncManager.Genesis.Returns(genesisBlock.Header);
            var handler = new Eth62ProtocolHandler(
                session,
                svc,
                new NodeStatsManager(new StatsConfig(), LimboLogs.Instance),
                syncManager,
                transactionPool, LimboLogs.Instance);
            handler.Init();
            
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockNumber = 100;
            msg.MaxHeaders = 5;
            msg.Skip = 1;
            msg.Reverse = 1;
            
            var statusMsg = new StatusMessage();
            statusMsg.GenesisHash = genesisBlock.Hash;
            
            IByteBuffer statusPacket = svc.ZeroSerialize(statusMsg);
            statusPacket.ReadByte();

            IByteBuffer getBlockHeadersPacket = svc.ZeroSerialize(msg);
            getBlockHeadersPacket.ReadByte();
            
            handler.HandleMessage(new ZeroPacket(statusPacket){PacketType = 0});
            handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket){PacketType = Eth62MessageCode.GetBlockHeaders});
            
            session.Received().DeliverMessage(Arg.Is<BlockHeadersMessage>(bhm => bhm.BlockHeaders.Length == 5));
            syncManager.Received().FindHash(100);
        }
    }
}