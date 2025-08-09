// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
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
        private ISession _session = null!;
        private IMessageSerializationService _svc = null!;
        private ISyncServer _syncManager = null!;
        private ITxPool _transactionPool = null!;
        private IPooledTxsRequestor _pooledTxsRequestor = null!;
        private IGossipPolicy _gossipPolicy = null!;
        private ITimerFactory _timerFactory = null!;
        private ISpecProvider _specProvider = null!;
        private Block _genesisBlock = null!;
        private Eth66ProtocolHandler _handler = null!;
        private CompositeDisposable _disposables = null!;

        [SetUp]
        public void Setup()
        {
            _svc = Build.A.SerializationService().WithEth66().TestObject;

            NetworkDiagTracer.IsEnabled = true;

            _disposables = new();
            _session = Substitute.For<ISession>();
            Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _session.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(c => c.Arg<P2PMessage>().AddTo(_disposables));
            _syncManager = Substitute.For<ISyncServer>();
            _transactionPool = Substitute.For<ITxPool>();
            _pooledTxsRequestor = Substitute.For<IPooledTxsRequestor>();
            _specProvider = Substitute.For<ISpecProvider>();
            _gossipPolicy = Substitute.For<IGossipPolicy>();
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            _timerFactory = Substitute.For<ITimerFactory>();
            _handler = new Eth66ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(_timerFactory, LimboLogs.Instance),
                _syncManager,
                RunImmediatelyScheduler.Instance,
                _transactionPool,
                _pooledTxsRequestor,
                _gossipPolicy,
                new ForkInfo(_specProvider, _syncManager),
                LimboLogs.Instance);
            _handler.Init();
        }

        [TearDown]
        public void TearDown()
        {
            _pooledTxsRequestor?.Dispose();
            _handler?.Dispose();
            _session?.Dispose();
            _syncManager?.Dispose();
            _disposables?.Dispose();
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
            using var msg62 = new GetBlockHeadersMessage();
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(1111, msg62);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetBlockHeaders);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage>());
        }

        [Test]
        public async Task Can_handle_block_headers()
        {
            using var msg62 = new BlockHeadersMessage(Build.A.BlockHeader.TestObjectNTimes(3).ToPooledList());
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage(1111, msg62);

            _session.When((session) => session.DeliverMessage(Arg.Any<Eth66Message<GetBlockHeadersMessage>>())).Do(callInfo =>
            {
                Eth66Message<GetBlockHeadersMessage> message = (Eth66Message<GetBlockHeadersMessage>)callInfo[0];
                msg66.RequestId = message.RequestId;
            });

            Task task = ((ISyncPeer)_handler).GetBlockHeaders(1, 1, 1, CancellationToken.None).AddResultTo(_disposables);
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.BlockHeaders);
            await task;
        }

        [Test]
        public void Should_throw_when_receiving_unrequested_block_headers()
        {
            using var msg62 = new BlockHeadersMessage(Build.A.BlockHeader.TestObjectNTimes(3).ToPooledList());
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage(1111, msg62);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.BlockHeaders);
            act.Should().Throw<SubprotocolException>();
        }

        [Test]
        public void Can_handle_get_block_bodies()
        {
            using var msg62 = new GetBlockBodiesMessage(new[] { Keccak.Zero, TestItem.KeccakA });
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockBodiesMessage(1111, msg62);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetBlockBodies);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage>());
        }

        [Test]
        public async Task Can_handle_block_bodies()
        {
            using var msg62 = new BlockBodiesMessage(Build.A.Block.TestObjectNTimes(3));
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage(1111, msg62);

            _session.When((session) => session.DeliverMessage(Arg.Any<Eth66Message<GetBlockBodiesMessage>>())).Do(callInfo =>
            {
                Eth66Message<GetBlockBodiesMessage> message = (Eth66Message<GetBlockBodiesMessage>)callInfo[0];
                msg66.RequestId = message.RequestId;
            });

            HandleIncomingStatusMessage();
            Task task = ((ISyncPeer)_handler).GetBlockBodies(new List<Hash256>(new[] { Keccak.Zero }), CancellationToken.None).AddResultTo(_disposables);
            HandleZeroMessage(msg66, Eth66MessageCode.BlockBodies);
            await task;
        }

        [Test]
        public void Should_throw_when_receiving_unrequested_block_bodies()
        {
            using var msg62 = new BlockBodiesMessage(Build.A.Block.TestObjectNTimes(3));
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage(1111, msg62);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.BlockBodies);
            act.Should().Throw<SubprotocolException>();
        }

        [Test]
        public void Can_handle_get_pooled_transactions()
        {
            using var msg65 = new GetPooledTransactionsMessage(new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage(1111, msg65);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetPooledTransactions);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage>());
        }

        [Test]
        public void Can_handle_pooled_transactions()
        {
            Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(1), TestItem.PrivateKeyA).TestObject;
            using var msg65 = new PooledTransactionsMessage(new ArrayPoolList<Transaction>(1) { tx });
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage(1111, msg65);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.PooledTransactions);
        }

        [Test]
        public void Can_handle_get_node_data()
        {
            using var msg63 = new GetNodeDataMessage(new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetNodeDataMessage(1111, msg63);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetNodeData);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage>());
        }

        [Test]
        public async Task Can_handle_node_data()
        {
            using var msg63 = new NodeDataMessage(ArrayPoolList<byte[]>.Empty());
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage(1111, msg63);

            _session.When((session) => session.DeliverMessage(Arg.Any<Eth66Message<GetNodeDataMessage>>())).Do(callInfo =>
            {
                Eth66Message<GetNodeDataMessage> message = (Eth66Message<GetNodeDataMessage>)callInfo[0];
                msg66.RequestId = message.RequestId;
            });

            HandleIncomingStatusMessage();
            Task task = ((ISyncPeer)_handler).GetNodeData(new List<Hash256>(new[] { Keccak.Zero }), CancellationToken.None).AddResultTo(_disposables);
            HandleZeroMessage(msg66, Eth66MessageCode.NodeData);
            await task;
        }

        [Test]
        public void Should_throw_when_receiving_unrequested_node_data()
        {
            using var msg63 = new NodeDataMessage(ArrayPoolList<byte[]>.Empty());
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage(1111, msg63);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.NodeData);
            act.Should().Throw<SubprotocolException>();
        }

        [Test]
        public void Can_handle_get_receipts()
        {
            using var msg63 = new GetReceiptsMessage(new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetReceiptsMessage(1111, msg63);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetReceipts);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage>());
        }

        [Test]
        public async Task Can_handle_receipts()
        {
            using var msg63 = new ReceiptsMessage(ArrayPoolList<TxReceipt[]>.Empty());
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage(1111, msg63);

            _session.When((session) => session.DeliverMessage(Arg.Any<Eth66Message<GetReceiptsMessage>>())).Do(callInfo =>
            {
                Eth66Message<GetReceiptsMessage> message = (Eth66Message<GetReceiptsMessage>)callInfo[0];
                msg66.RequestId = message.RequestId;
            });

            HandleIncomingStatusMessage();
            Task task = ((ISyncPeer)_handler).GetReceipts(new List<Hash256>(new[] { Keccak.Zero }), CancellationToken.None).AddResultTo(_disposables);
            HandleZeroMessage(msg66, Eth66MessageCode.Receipts);
            await task;
        }

        [Test]
        public void Should_throw_when_receiving_unrequested_receipts()
        {
            using var msg63 = new ReceiptsMessage(ArrayPoolList<TxReceipt[]>.Empty());
            using var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage(1111, msg63);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.Receipts);
            act.Should().Throw<SubprotocolException>();
        }


        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(256, 1)]
        [TestCase(257, 2)]
        [TestCase(512, 2)]
        [TestCase(1000, 4)]
        [TestCase(10000, 40)]
        public void should_request_in_GetPooledTransactionsMessage_up_to_256_txs(int numberOfTransactions, int expectedNumberOfMessages)
        {
            const int maxNumberOfTxsInOneMsg = 256;

            _handler = new Eth66ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(_timerFactory, LimboLogs.Instance),
                _syncManager,
                RunImmediatelyScheduler.Instance,
                _transactionPool,
                new PooledTxsRequestor(_transactionPool, new TxPoolConfig(), _specProvider),
                _gossipPolicy,
                new ForkInfo(_specProvider, _syncManager),
                LimboLogs.Instance);

            using ArrayPoolList<Hash256> hashes = new(numberOfTransactions);

            for (int i = 0; i < numberOfTransactions; i++)
            {
                hashes.Add(new Hash256(i.ToString("X64")));
            }

            using NewPooledTransactionHashesMessage hashesMsg = new(hashes);
            HandleIncomingStatusMessage();
            HandleZeroMessage(hashesMsg, Eth65MessageCode.NewPooledTransactionHashes);

            _session.Received(expectedNumberOfMessages)
                .DeliverMessage(Arg.Is<Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage>(m =>
                    m.EthMessage.Hashes.Count == maxNumberOfTxsInOneMsg ||
                    m.EthMessage.Hashes.Count == numberOfTransactions % maxNumberOfTxsInOneMsg
                ));
        }

        private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
        {
            IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg);
            getBlockHeadersPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket) { PacketType = (byte)messageCode });
        }
        private void HandleIncomingStatusMessage()
        {
            using var statusMsg = new StatusMessage();
            statusMsg.GenesisHash = _genesisBlock.Hash;
            statusMsg.BestHash = _genesisBlock.Hash;

            IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
            statusPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
        }
    }
}
