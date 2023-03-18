// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
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
        private IGossipPolicy _gossipPolicy;
        private readonly TxDecoder _txDecoder = new();

        [SetUp]
        public void Setup()
        {
            _svc = Build.A.SerializationService().WithEth().TestObject;

            NetworkDiagTracer.IsEnabled = true;

            _session = Substitute.For<ISession>();
            Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _syncManager = Substitute.For<ISyncServer>();
            _transactionPool = Substitute.For<ITxPool>();
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _gossipPolicy = Substitute.For<IGossipPolicy>();
            _gossipPolicy.CanGossipBlocks.Returns(true);
            _gossipPolicy.ShouldGossipBlock(Arg.Any<BlockHeader>()).Returns(true);
            _gossipPolicy.ShouldDisconnectGossipingNodes.Returns(false);
            _handler = new Eth62ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(timerFactory, LimboLogs.Instance),
                _syncManager,
                _transactionPool,
                _gossipPolicy,
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
        public void Should_stop_notifying_about_new_blocks_and_new_block_hashes_if_in_PoS()
        {
            _gossipPolicy.CanGossipBlocks.Returns(false);

            _handler = new Eth62ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance),
                _syncManager,
                _transactionPool,
                _gossipPolicy,
                LimboLogs.Instance);

            _syncManager.Received().StopNotifyingPeersAboutNewBlocks();
        }

        [Test]
        public void Can_broadcast_a_block([Values(SendBlockMode.HashOnly, SendBlockMode.FullBlock, (SendBlockMode)99)] SendBlockMode mode)
        {
            Block block = Build.A.Block.WithTotalDifficulty(1L).TestObject;
            Type expectedMessageType = mode == SendBlockMode.FullBlock ? typeof(NewBlockMessage) : typeof(NewBlockHashesMessage);
            _handler.NotifyOfNewBlock(block, mode);
            _session.Received().DeliverMessage(Arg.Is<P2PMessage>(m => m.GetType().IsAssignableFrom(expectedMessageType)));
        }

        [Test]
        public void Broadcasts_only_once([Values(SendBlockMode.HashOnly, SendBlockMode.FullBlock)] SendBlockMode mode)
        {
            Block block = Build.A.Block.WithTotalDifficulty(1L).TestObject;
            _handler.NotifyOfNewBlock(block, mode);
            _handler.NotifyOfNewBlock(block, SendBlockMode.HashOnly);
            _handler.NotifyOfNewBlock(block, SendBlockMode.FullBlock);
            _session.Received(1).DeliverMessage(Arg.Is<P2PMessage>(m =>
                m.GetType().IsAssignableFrom(typeof(NewBlockMessage))
                || m.GetType().IsAssignableFrom(typeof(NewBlockHashesMessage))));
        }

        [Test]
        public void Should_not_broadcast_a_block_if_in_PoS()
        {
            Block block = Build.A.Block.WithTotalDifficulty(1L).TestObject;
            _gossipPolicy.CanGossipBlocks.Returns(false);
            _handler.NotifyOfNewBlock(block, SendBlockMode.FullBlock);
            _session.Received(0).DeliverMessage(Arg.Any<NewBlockMessage>());
            _session.ClearReceivedCalls();
            _handler.NotifyOfNewBlock(block, SendBlockMode.HashOnly);
            _session.Received(0).DeliverMessage(Arg.Any<NewBlockHashesMessage>());
            _session.ClearReceivedCalls();
            _handler.NotifyOfNewBlock(block, (SendBlockMode)99);
            _session.Received(0).DeliverMessage(Arg.Any<NewBlockHashesMessage>());
        }

        [Test]
        public void Cannot_broadcast_a_block_without_total_difficulty_but_can_hint()
        {
            Block block = Build.A.Block.TestObject;
            Assert.Throws<InvalidOperationException>(
                () => _handler.NotifyOfNewBlock(block, SendBlockMode.FullBlock));
            _handler.NotifyOfNewBlock(block, SendBlockMode.HashOnly);
            _handler.NotifyOfNewBlock(block, (SendBlockMode)99);
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
                () => _handler.HandleMessage(new ZeroPacket(packet) { PacketType = Eth62MessageCode.GetBlockHeaders }));
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
            NewBlockMessage newBlockMessage = new();
            newBlockMessage.Block = Build.A.Block.WithParent(_genesisBlock).TestObject;
            newBlockMessage.TotalDifficulty = _genesisBlock.Difficulty + newBlockMessage.Block.Difficulty;

            HandleIncomingStatusMessage();
            HandleZeroMessage(newBlockMessage, Eth62MessageCode.NewBlock);

            _syncManager.Received().AddNewBlock(
                Arg.Is<Block>(b => b.Hash == newBlockMessage.Block.Hash),
                _handler);
        }

        [Test]
        public void Should_disconnect_peer_sending_new_block_message_in_PoS()
        {
            NewBlockMessage newBlockMessage = new NewBlockMessage();
            newBlockMessage.Block = Build.A.Block.WithParent(_genesisBlock).TestObject;
            newBlockMessage.TotalDifficulty = _genesisBlock.Difficulty + newBlockMessage.Block.Difficulty;

            _gossipPolicy.ShouldDisconnectGossipingNodes.Returns(true);

            HandleIncomingStatusMessage();
            HandleZeroMessage(newBlockMessage, Eth62MessageCode.NewBlock);

            _session.Received().InitiateDisconnect(InitiateDisconnectReason.GossipingInPoS, "NewBlock message received after FIRST_FINALIZED_BLOCK PoS block. Disconnecting Peer.");
        }

        [Test]
        public void Throws_if_adding_new_block_fails()
        {
            NewBlockMessage newBlockMessage = new();
            newBlockMessage.Block = Build.A.Block.WithParent(_genesisBlock).TestObject;
            newBlockMessage.TotalDifficulty = _genesisBlock.Difficulty + newBlockMessage.Block.Difficulty;

            HandleIncomingStatusMessage();

            IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(newBlockMessage);
            getBlockHeadersPacket.ReadByte();

            _syncManager.WhenForAnyArgs(w => w.AddNewBlock(null, _handler)).Do(ci => throw new Exception());
            Assert.Throws<Exception>(
                () => _handler.HandleMessage(
                    new ZeroPacket(getBlockHeadersPacket) { PacketType = Eth62MessageCode.NewBlock }));
        }

        [Test]
        public void Can_handle_new_block_hashes()
        {
            NewBlockHashesMessage msg = new((Keccak.Zero, 1), (Keccak.Zero, 2));
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.NewBlockHashes);
        }

        [Test]
        public void Should_disconnect_peer_sending_new_block_hashes_in_PoS()
        {
            NewBlockHashesMessage msg = new NewBlockHashesMessage((Keccak.Zero, 1), (Keccak.Zero, 2));

            _gossipPolicy.ShouldDisconnectGossipingNodes.Returns(true);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.NewBlockHashes);

            _session.Received().InitiateDisconnect(InitiateDisconnectReason.GossipingInPoS, "NewBlock message received after FIRST_FINALIZED_BLOCK PoS block. Disconnecting Peer.");
        }

        [Test]
        public void Can_handle_get_block_bodies()
        {
            GetBlockBodiesMessage msg = new(new[] { Keccak.Zero, TestItem.KeccakA });

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.GetBlockBodies);
        }

        [TestCase(5, 5)]
        [TestCase(50, 20)]
        public void Should_truncate_array_when_too_many_body(int availableBody, int expectedResponseSize)
        {
            List<Block> blocks = new List<Block>();
            Transaction[] transactions = Build.A.Transaction.TestObjectNTimes(1000);
            for (int i = 0; i < availableBody; i++)
            {
                if (i == 0)
                {
                    blocks.Add(Build.A.Block.WithTransactions(transactions).TestObject);
                }
                else
                {
                    blocks.Add(Build.A.Block.WithTransactions(transactions).WithParent(blocks[^1]).TestObject);
                }

                _syncManager.Find(blocks[^1].Hash).Returns(blocks[^1]);
            }

            GetBlockBodiesMessage msg = new(blocks.Select(block => block.Hash).ToArray());

            BlockBodiesMessage response = null;
            _session.When(session => session.DeliverMessage(Arg.Any<BlockBodiesMessage>())).Do((call) => response = (BlockBodiesMessage)call[0]);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.GetBlockBodies);

            response.Should().NotBeNull();
            response.Bodies.Length.Should().Be(expectedResponseSize);
            foreach (BlockBody responseBody in response.Bodies)
            {
                responseBody.Should().NotBeNull();
            }
        }

        [Test]
        public void Can_handle_transactions()
        {
            TransactionsMessage msg = new(new List<Transaction>(Build.A.Transaction.SignedAndResolved().TestObjectNTimes(3)));

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.Transactions);
        }

        [Test]
        public void Can_handle_transactions_without_filtering()
        {
            TransactionsMessage msg = new(new List<Transaction>(Build.A.Transaction.SignedAndResolved().TestObjectNTimes(3)));

            _handler.DisableTxFiltering();
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.Transactions);
        }

        [Test]
        public void Can_handle_block_bodies()
        {
            BlockBodiesMessage msg = new(Build.A.Block.TestObjectNTimes(3));

            HandleIncomingStatusMessage();
            ((ISyncPeer)_handler).GetBlockBodies(new List<Keccak>(new[] { Keccak.Zero }), CancellationToken.None);
            HandleZeroMessage(msg, Eth62MessageCode.BlockBodies);
        }

        [Test]
        public async Task Get_block_bodies_returns_immediately_when_empty_hash_list()
        {
            BlockBody[] bodies =
                await ((ISyncPeer)_handler).GetBlockBodies(new List<Keccak>(), CancellationToken.None);

            bodies.Should().HaveCount(0);
        }

        [Test]
        public void Throws_when_receiving_a_bodies_message_that_has_not_been_requested()
        {
            BlockBodiesMessage msg = new(Build.A.Block.TestObjectNTimes(3));

            HandleIncomingStatusMessage();
            Assert.Throws<SubprotocolException>(() => HandleZeroMessage(msg, Eth62MessageCode.BlockBodies));
        }

        [Test]
        public void Can_handle_headers()
        {
            BlockHeadersMessage msg = new(Build.A.BlockHeader.TestObjectNTimes(3));

            ((ISyncPeer)_handler).GetBlockHeaders(1, 1, 1, CancellationToken.None);
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth62MessageCode.BlockHeaders);
        }

        [Test]
        public void Throws_when_receiving_a_headers_message_that_has_not_been_requested()
        {
            BlockHeadersMessage msg = new(Build.A.BlockHeader.TestObjectNTimes(3));

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

        [TestCase(1, true)]
        [TestCase(1055, true)]
        [TestCase(1056, false)]
        public void should_send_txs_with_size_up_to_MaxPacketSize_in_one_TransactionsMessage(int txCount, bool shouldBeSentInJustOneMessage)
        {
            Transaction[] txs = new Transaction[txCount];

            for (int i = 0; i < txCount; i++)
            {
                txs[i] = Build.A.Transaction.WithNonce((UInt256)i).SignedAndResolved().TestObject;
            }

            _handler.SendNewTransactions(txs);

            if (shouldBeSentInJustOneMessage)
            {
                _session.Received(1).DeliverMessage(Arg.Is<TransactionsMessage>(m => m.Transactions.Count == txCount));
            }
            else
            {
                _session.Received(2).DeliverMessage(Arg.Any<TransactionsMessage>());
            }
        }

        [TestCase(257)]
        [TestCase(300)]
        [TestCase(1055)]
        [TestCase(1056)]
        [TestCase(1500)]
        [TestCase(10000)]
        public void should_send_txs_with_size_exceeding_MaxPacketSize_in_more_than_one_TransactionsMessage(int txCount)
        {
            int sizeOfOneTestTransaction = _txDecoder.GetLength(Build.A.Transaction.SignedAndResolved().TestObject);
            int maxNumberOfTxsInOneMsg = TransactionsMessage.MaxPacketSize / sizeOfOneTestTransaction; // it's 1055
            int nonFullMsgTxsCount = txCount % maxNumberOfTxsInOneMsg;
            int messagesCount = txCount / maxNumberOfTxsInOneMsg + (nonFullMsgTxsCount > 0 ? 1 : 0);

            Transaction[] txs = new Transaction[txCount];

            for (int i = 0; i < txCount; i++)
            {
                txs[i] = Build.A.Transaction.WithNonce((UInt256)i).SignedAndResolved().TestObject;
            }

            _handler.SendNewTransactions(txs);

            _session.Received(messagesCount).DeliverMessage(Arg.Is<TransactionsMessage>(m => m.Transactions.Count == maxNumberOfTxsInOneMsg || m.Transactions.Count == nonFullMsgTxsCount));
        }

        [TestCase(0)]
        [TestCase(128)]
        [TestCase(4096)]
        [TestCase(100000)]
        [TestCase(102400)]
        [TestCase(222222)]
        public void should_send_single_transaction_even_if_exceed_MaxPacketSize(int dataSize)
        {
            int txCount = 512; //we will try to send 512 txs

            Transaction[] txs = new Transaction[txCount];

            for (int i = 0; i < txCount; i++)
            {
                txs[i] = Build.A.Transaction.WithData(new byte[dataSize]).WithNonce((UInt256)i).SignedAndResolved().TestObject;
            }

            Transaction tx = txs[0];
            int sizeOfOneTx = tx.GetLength(new TxDecoder());
            int numberOfTxsInOneMsg = Math.Max(TransactionsMessage.MaxPacketSize / sizeOfOneTx, 1);
            int nonFullMsgTxsCount = txCount % numberOfTxsInOneMsg;
            int messagesCount = txCount / numberOfTxsInOneMsg + (nonFullMsgTxsCount > 0 ? 1 : 0);

            _handler.SendNewTransactions(txs);

            _session.Received(messagesCount).DeliverMessage(Arg.Is<TransactionsMessage>(m => m.Transactions.Count == numberOfTxsInOneMsg || m.Transactions.Count == nonFullMsgTxsCount));
        }

        private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
        {
            IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg);
            getBlockHeadersPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket) { PacketType = (byte)messageCode });
        }

        [Test]
        public void Throws_if_new_block_message_received_before_status()
        {
            NewBlockMessage newBlockMessage = new();
            newBlockMessage.Block = Build.A.Block.WithParent(_genesisBlock).TestObject;
            newBlockMessage.TotalDifficulty = _genesisBlock.Difficulty + newBlockMessage.Block.Difficulty;

            IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(newBlockMessage);
            getBlockHeadersPacket.ReadByte();
            Assert.Throws<SubprotocolException>(
                () => _handler.HandleMessage(
                    new ZeroPacket(getBlockHeadersPacket) { PacketType = Eth62MessageCode.NewBlock }));
        }

        private void HandleIncomingStatusMessage()
        {
            var statusMsg = new StatusMessage();
            statusMsg.GenesisHash = _genesisBlock.Hash;
            statusMsg.BestHash = _genesisBlock.Hash;

            IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
            statusPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
        }
    }
}
