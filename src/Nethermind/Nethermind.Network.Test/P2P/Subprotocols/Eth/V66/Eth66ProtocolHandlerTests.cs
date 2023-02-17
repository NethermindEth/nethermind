// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using BlockBodiesMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.BlockBodiesMessage;
using BlockHeadersMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.BlockHeadersMessage;
using GetBlockBodiesMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockBodiesMessage;
using GetBlockHeadersMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockHeadersMessage;
using GetNodeDataMessage = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.GetNodeDataMessage;
using GetPooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.GetPooledTransactionsMessage;
using GetReceiptsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.GetReceiptsMessage;
using NodeDataMessage = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.NodeDataMessage;
using PooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;
using ReceiptsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.ReceiptsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture, Parallelizable(ParallelScope.Self)]
    public class Eth66ProtocolHandlerTests
    {
        private ISession _session;
        private IMessageSerializationService _svc;
        private ISyncServer _syncManager;
        private ITxPool _transactionPool;
        private IPooledTxsRequestor _pooledTxsRequestor;
        private IGossipPolicy _gossipPolicy;
        private ISpecProvider _specProvider;
        private Block _genesisBlock;
        private Eth66ProtocolHandler _handler;

        [SetUp]
        public void Setup()
        {
            _svc = Build.A.SerializationService().WithEth66().TestObject;

            NetworkDiagTracer.IsEnabled = true;

            _session = Substitute.For<ISession>();
            Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _syncManager = Substitute.For<ISyncServer>();
            _transactionPool = Substitute.For<ITxPool>();
            _pooledTxsRequestor = Substitute.For<IPooledTxsRequestor>();
            _specProvider = Substitute.For<ISpecProvider>();
            _gossipPolicy = Substitute.For<IGossipPolicy>();
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _handler = new Eth66ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(timerFactory, LimboLogs.Instance),
                _syncManager,
                _transactionPool,
                _pooledTxsRequestor,
                _gossipPolicy,
                new ForkInfo(_specProvider, _genesisBlock.Header.Hash!),
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
            _handler.Name.Should().Be("eth66");
            _handler.ProtocolVersion.Should().Be(66);
            _handler.MessageIdSpaceSize.Should().Be(17);
            _handler.IncludeInTxPool.Should().BeTrue();
            _handler.ClientId.Should().Be(_session.Node?.ClientId);
            _handler.HeadHash.Should().BeNull();
            _handler.HeadNumber.Should().Be(0);
        }

        [Test]
        public void Can_handle_get_block_headers()
        {
            var msg62 = new GetBlockHeadersMessage();
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(1111, msg62);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetBlockHeaders);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage>());
        }

        [Test]
        public void Can_handle_block_headers()
        {
            var msg62 = new BlockHeadersMessage(Build.A.BlockHeader.TestObjectNTimes(3));
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage(1111, msg62);

            _session.When((session) => session.DeliverMessage(Arg.Any<Eth66Message<GetBlockHeadersMessage>>())).Do(callInfo =>
            {
                Eth66Message<GetBlockHeadersMessage> message = (Eth66Message<GetBlockHeadersMessage>)callInfo[0];
                msg66.RequestId = message.RequestId;
            });

            ((ISyncPeer)_handler).GetBlockHeaders(1, 1, 1, CancellationToken.None);
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.BlockHeaders);
        }

        [Test]
        public void Should_throw_when_receiving_unrequested_block_headers()
        {
            var msg62 = new BlockHeadersMessage(Build.A.BlockHeader.TestObjectNTimes(3));
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage(1111, msg62);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.BlockHeaders);
            act.Should().Throw<SubprotocolException>();
        }

        [Test]
        public void Can_handle_get_block_bodies()
        {
            var msg62 = new GetBlockBodiesMessage(new[] { Keccak.Zero, TestItem.KeccakA });
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockBodiesMessage(1111, msg62);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetBlockBodies);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage>());
        }

        [Test]
        public void Can_handle_block_bodies()
        {
            var msg62 = new BlockBodiesMessage(Build.A.Block.TestObjectNTimes(3));
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage(1111, msg62);

            _session.When((session) => session.DeliverMessage(Arg.Any<Eth66Message<GetBlockBodiesMessage>>())).Do(callInfo =>
            {
                Eth66Message<GetBlockBodiesMessage> message = (Eth66Message<GetBlockBodiesMessage>)callInfo[0];
                msg66.RequestId = message.RequestId;
            });

            HandleIncomingStatusMessage();
            ((ISyncPeer)_handler).GetBlockBodies(new List<Keccak>(new[] { Keccak.Zero }), CancellationToken.None);
            HandleZeroMessage(msg66, Eth66MessageCode.BlockBodies);
        }

        [Test]
        public void Should_throw_when_receiving_unrequested_block_bodies()
        {
            var msg62 = new BlockBodiesMessage(Build.A.Block.TestObjectNTimes(3));
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage(1111, msg62);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.BlockBodies);
            act.Should().Throw<SubprotocolException>();
        }

        [Test]
        public void Can_handle_get_pooled_transactions()
        {
            var msg65 = new GetPooledTransactionsMessage(new[] { Keccak.Zero, TestItem.KeccakA });
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage(1111, msg65);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetPooledTransactions);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage>());
        }

        [Test]
        public void Can_handle_pooled_transactions()
        {
            Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(1, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            var msg65 = new PooledTransactionsMessage(new[] { tx });
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage(1111, msg65);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.PooledTransactions);
        }

        [Test]
        public void Can_handle_get_node_data()
        {
            var msg63 = new GetNodeDataMessage(new[] { Keccak.Zero, TestItem.KeccakA });
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetNodeDataMessage(1111, msg63);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetNodeData);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage>());
        }

        [Test]
        public void Can_handle_node_data()
        {
            var msg63 = new NodeDataMessage(System.Array.Empty<byte[]>());
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage(1111, msg63);

            _session.When((session) => session.DeliverMessage(Arg.Any<Eth66Message<GetNodeDataMessage>>())).Do(callInfo =>
            {
                Eth66Message<GetNodeDataMessage> message = (Eth66Message<GetNodeDataMessage>)callInfo[0];
                msg66.RequestId = message.RequestId;
            });

            HandleIncomingStatusMessage();
            ((ISyncPeer)_handler).GetNodeData(new List<Keccak>(new[] { Keccak.Zero }), CancellationToken.None);
            HandleZeroMessage(msg66, Eth66MessageCode.NodeData);
        }

        [Test]
        public void Should_throw_when_receiving_unrequested_node_data()
        {
            var msg63 = new NodeDataMessage(System.Array.Empty<byte[]>());
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage(1111, msg63);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.NodeData);
            act.Should().Throw<SubprotocolException>();
        }

        [Test]
        public void Can_handle_get_receipts()
        {
            var msg63 = new GetReceiptsMessage(new[] { Keccak.Zero, TestItem.KeccakA });
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetReceiptsMessage(1111, msg63);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetReceipts);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage>());
        }

        [Test]
        public void Can_handle_receipts()
        {
            var msg63 = new ReceiptsMessage(System.Array.Empty<TxReceipt[]>());
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage(1111, msg63);

            _session.When((session) => session.DeliverMessage(Arg.Any<Eth66Message<GetReceiptsMessage>>())).Do(callInfo =>
            {
                Eth66Message<GetReceiptsMessage> message = (Eth66Message<GetReceiptsMessage>)callInfo[0];
                msg66.RequestId = message.RequestId;
            });

            HandleIncomingStatusMessage();
            ((ISyncPeer)_handler).GetReceipts(new List<Keccak>(new[] { Keccak.Zero }), CancellationToken.None);
            HandleZeroMessage(msg66, Eth66MessageCode.Receipts);
        }

        [Test]
        public void Should_throw_when_receiving_unrequested_receipts()
        {
            var msg63 = new ReceiptsMessage(System.Array.Empty<TxReceipt[]>());
            var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage(1111, msg63);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.Receipts);
            act.Should().Throw<SubprotocolException>();
        }

        private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
        {
            IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg);
            getBlockHeadersPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket) { PacketType = (byte)messageCode });
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
