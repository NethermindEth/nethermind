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

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.Self)]
    public class Eth62ProtocolHandlerTests
    {
        private ISession _session;
        private IMessageSerializationService _svc;
        private ISyncServer _syncManager;
        private ITxPool _transactionPool;
        private Block _genesisBlock;
        private Eth62ProtocolHandler _handler;

        [SetUp]
        public void Setup()
        {
            _svc = Build.A.SerializationService().WithEth().TestObject;

            NetworkDiagTracer.IsEnabled = true;

            _session = Substitute.For<ISession>();
            Node node = new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _syncManager = Substitute.For<ISyncServer>();
            _transactionPool = Substitute.For<ITxPool>();
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _handler = new Eth62ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(timerFactory, LimboLogs.Instance),
                _syncManager,
                _transactionPool,
                LimboLogs.Instance);
            _handler.Init();
        }

        [TearDown]
        public void TearDown()
        {
            _handler.Dispose();
        }

        [Test]
        public void Metadata_correct()
        {
            _handler.ProtocolCode.Should().Be("eth");
            _handler.Name.Should().Be("eth62");
            _handler.ProtocolVersion.Should().Be(62);
            _handler.MessageIdSpaceSize.Should().Be(8);
            _handler.IncludeInTxPool.Should().BeTrue();
            _handler.ClientId.Should().Be(_session.Node?.ClientId);
            _handler.HeadHash.Should().BeNull();
            _handler.HeadNumber.Should().Be(0);
        }
        
        [Test]
        public void Cannot_init_if_sync_server_head_is_not_set()
        {
            _syncManager.Head.Returns((BlockHeader)null);
            Assert.Throws<InvalidOperationException>(() => _handler.Init());
        }

        [Test]
        public void Can_broadcast_a_block()
        {
            Block block = Build.A.Block.WithTotalDifficulty(1L).TestObject;
            _handler.NotifyOfNewBlock(block, SendBlockPriority.High);
            _session.Received().DeliverMessage(Arg.Any<NewBlockMessage>());
            _session.ClearReceivedCalls();
            _handler.NotifyOfNewBlock(block, SendBlockPriority.Low);
            _session.Received().DeliverMessage(Arg.Any<NewBlockHashesMessage>());
            _session.ClearReceivedCalls();
            _handler.NotifyOfNewBlock(block, (SendBlockPriority) 99);
            _session.Received().DeliverMessage(Arg.Any<NewBlockHashesMessage>());
        }

        [Test]
        public void Cannot_broadcast_a_block_without_total_difficulty_but_can_hint()
        {
            Block block = Build.A.Block.TestObject;
            Assert.Throws<InvalidOperationException>(
                () => _handler.NotifyOfNewBlock(block, SendBlockPriority.High));
            _handler.NotifyOfNewBlock(block, SendBlockPriority.Low);
            _handler.NotifyOfNewBlock(block, (SendBlockPriority) 99);
        }

        [Test]
        public void Get_headers_from_genesis()
        {
            var msg = new GetBlockHeadersMessage();
            msg.StartBlockHash = TestItem.KeccakA;
            msg.MaxHeaders = 3;
            msg.Skip = 1;
            msg.Reverse = 1;

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.GetBlockHeaders);

            _syncManager.Received().FindHeaders(TestItem.KeccakA, 3, 1, true);
        }

        [Test]
        public void Receiving_request_before_status_fails()
        {
            var msg = new GetBlockHeadersMessage();
            msg.StartBlockHash = TestItem.KeccakA;
            msg.MaxHeaders = 3;
            msg.Skip = 1;
            msg.Reverse = 1;

            IByteBuffer packet = _svc.ZeroSerialize(msg);
            packet.ReadByte();

            Assert.Throws<SubprotocolException>(
                () => _handler.HandleMessage(new ZeroPacket(packet) {PacketType = Eth62MessageCode.GetBlockHeaders}));
        }

        [Test]
        public void Get_headers_when_blocks_are_missing_at_the_end()
        {
            var headers = new BlockHeader[5];
            headers[0] = Build.A.BlockHeader.TestObject;
            headers[1] = Build.A.BlockHeader.TestObject;
            headers[2] = Build.A.BlockHeader.TestObject;

            _syncManager.FindHash(100).Returns(TestItem.KeccakA);
            _syncManager.FindHeaders(TestItem.KeccakA, 5, 1, true).Returns(headers);
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);

            var msg = new GetBlockHeadersMessage();
            msg.StartBlockNumber = 100;
            msg.MaxHeaders = 5;
            msg.Skip = 1;
            msg.Reverse = 1;

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.GetBlockHeaders);

            _session.Received().DeliverMessage(Arg.Is<BlockHeadersMessage>(bhm => bhm.BlockHeaders.Length == 3));
            _syncManager.Received().FindHash(100);
        }
        
        [Test]
        public void Throws_after_receiving_status_message_for_the_second_time()
        {
            HandleIncomingStatusMessage();
            Assert.Throws<SubprotocolException>(HandleIncomingStatusMessage);
        }

        [Test]
        public void Get_headers_when_blocks_are_missing_in_the_middle()
        {
            var headers = new BlockHeader[5];
            headers[0] = Build.A.BlockHeader.TestObject;
            headers[1] = Build.A.BlockHeader.TestObject;
            headers[2] = null;
            headers[3] = Build.A.BlockHeader.TestObject;
            headers[4] = Build.A.BlockHeader.TestObject;

            _syncManager.FindHash(100).Returns(TestItem.KeccakA);
            _syncManager.FindHeaders(TestItem.KeccakA, 5, 1, true)
                .Returns(headers);

            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);

            var msg = new GetBlockHeadersMessage();
            msg.StartBlockNumber = 100;
            msg.MaxHeaders = 5;
            msg.Skip = 1;
            msg.Reverse = 1;

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.GetBlockHeaders);

            _session.Received().DeliverMessage(Arg.Is<BlockHeadersMessage>(bhm => bhm.BlockHeaders.Length == 5));
            _syncManager.Received().FindHash(100);
        }

        [Test]
        public void Can_handle_new_block_message()
        {
            NewBlockMessage newBlockMessage = new NewBlockMessage();
            newBlockMessage.Block = Build.A.Block.WithParent(_genesisBlock).TestObject;
            newBlockMessage.TotalDifficulty = _genesisBlock.Difficulty + newBlockMessage.Block.Difficulty;

            HandleIncomingStatusMessage();
            HandleZeroMessage(newBlockMessage, Eth62MessageCode.NewBlock);

            _syncManager.Received().AddNewBlock(
                Arg.Is<Block>(b => b.Hash == newBlockMessage.Block.Hash),
                _handler);
        }

        [Test]
        public void Throws_if_adding_new_block_fails()
        {
            NewBlockMessage newBlockMessage = new NewBlockMessage();
            newBlockMessage.Block = Build.A.Block.WithParent(_genesisBlock).TestObject;
            newBlockMessage.TotalDifficulty = _genesisBlock.Difficulty + newBlockMessage.Block.Difficulty;

            HandleIncomingStatusMessage();

            IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(newBlockMessage);
            getBlockHeadersPacket.ReadByte();

            _syncManager.WhenForAnyArgs(w => w.AddNewBlock(null, _handler)).Do(ci => throw new Exception());
            Assert.Throws<Exception>(
                () => _handler.HandleMessage(
                    new ZeroPacket(getBlockHeadersPacket) {PacketType = Eth62MessageCode.NewBlock}));
        }

        [Test]
        public void Can_handle_new_block_hashes()
        {
            NewBlockHashesMessage msg = new NewBlockHashesMessage((Keccak.Zero, 1), (Keccak.Zero, 2));
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.NewBlockHashes);
        }

        [Test]
        public void Can_handle_get_block_bodies()
        {
            GetBlockBodiesMessage msg = new GetBlockBodiesMessage(new[] {Keccak.Zero, TestItem.KeccakA});

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.GetBlockBodies);
        }

        [Test]
        public void Can_handle_transactions()
        {
            TransactionsMessage msg = new TransactionsMessage(new List<Transaction>(Build.A.Transaction.SignedAndResolved().TestObjectNTimes(3)));

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.Transactions);
        }
        
        [Test]
        public void Can_handle_transactions_without_filtering()
        {
            TransactionsMessage msg = new TransactionsMessage(new List<Transaction>(Build.A.Transaction.SignedAndResolved().TestObjectNTimes(3)));

            _handler.DisableTxFiltering();
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.Transactions);
        }

        [Test]
        public void Can_handle_block_bodies()
        {
            BlockBodiesMessage msg = new BlockBodiesMessage(Build.A.Block.TestObjectNTimes(3));

            HandleIncomingStatusMessage();
            ((ISyncPeer) _handler).GetBlockBodies(new List<Keccak>(new[] {Keccak.Zero}), CancellationToken.None);
            HandleZeroMessage(msg, Eth62MessageCode.BlockBodies);
        }

        [Test]
        public async Task Get_block_bodies_returns_immediately_when_empty_hash_list()
        {
            BlockBody[] bodies =
                await ((ISyncPeer) _handler).GetBlockBodies(new List<Keccak>(), CancellationToken.None);

            bodies.Should().HaveCount(0);
        }

        [Test]
        public void Throws_when_receiving_a_bodies_message_that_has_not_been_requested()
        {
            BlockBodiesMessage msg = new BlockBodiesMessage(Build.A.Block.TestObjectNTimes(3));

            HandleIncomingStatusMessage();
            Assert.Throws<SubprotocolException>(() => HandleZeroMessage(msg, Eth62MessageCode.BlockBodies));
        }

        [Test]
        public void Can_handle_headers()
        {
            BlockHeadersMessage msg = new BlockHeadersMessage(Build.A.BlockHeader.TestObjectNTimes(3));

            ((ISyncPeer) _handler).GetBlockHeaders(1, 1, 1, CancellationToken.None);
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.BlockHeaders);
        }

        [Test]
        public void Throws_when_receiving_a_headers_message_that_has_not_been_requested()
        {
            BlockHeadersMessage msg = new BlockHeadersMessage(Build.A.BlockHeader.TestObjectNTimes(3));

            HandleIncomingStatusMessage();
            Assert.Throws<SubprotocolException>(() => HandleZeroMessage(msg, Eth62MessageCode.BlockHeaders));
        }
        
        [Test]
        public void Add_remove_listener()
        {
            static void HandlerOnSubprotocolRequested(object sender, ProtocolEventArgs e) { }

            _handler.SubprotocolRequested += HandlerOnSubprotocolRequested;
            _handler.SubprotocolRequested -= HandlerOnSubprotocolRequested;
        }
            
        private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
        {
            IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg);
            getBlockHeadersPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket) {PacketType = (byte) messageCode});
        }

        [Test]
        public void Throws_if_new_block_message_received_before_status()
        {
            NewBlockMessage newBlockMessage = new NewBlockMessage();
            newBlockMessage.Block = Build.A.Block.WithParent(_genesisBlock).TestObject;
            newBlockMessage.TotalDifficulty = _genesisBlock.Difficulty + newBlockMessage.Block.Difficulty;

            IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(newBlockMessage);
            getBlockHeadersPacket.ReadByte();
            Assert.Throws<SubprotocolException>(
                () => _handler.HandleMessage(
                    new ZeroPacket(getBlockHeadersPacket) {PacketType = Eth62MessageCode.NewBlock}));
        }

        private void HandleIncomingStatusMessage()
        {
            var statusMsg = new StatusMessage();
            statusMsg.GenesisHash = _genesisBlock.Hash;
            statusMsg.BestHash = _genesisBlock.Hash;

            IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
            statusPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(statusPacket) {PacketType = 0});
        }
    }
}
