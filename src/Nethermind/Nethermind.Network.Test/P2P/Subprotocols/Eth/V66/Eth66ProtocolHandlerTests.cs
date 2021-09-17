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
// 

using System.Collections.Generic;
using System.Net;
using System.Threading;
using DotNetty.Buffers;
using FluentAssertions;
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
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

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
        private IPoSSwitcher _poSSwitcher;
        private ISpecProvider _specProvider;
        private Block _genesisBlock;
        private Eth66ProtocolHandler _handler;

        [SetUp]
        public void Setup()
        {
            _svc = Build.A.SerializationService().WithEth66().TestObject;

            NetworkDiagTracer.IsEnabled = true;

            _session = Substitute.For<ISession>();
            Node node = new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _syncManager = Substitute.For<ISyncServer>();
            _transactionPool = Substitute.For<ITxPool>();
            _pooledTxsRequestor = Substitute.For<IPooledTxsRequestor>();
            _specProvider = Substitute.For<ISpecProvider>();
            _poSSwitcher = Substitute.For<IPoSSwitcher>();
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
                _poSSwitcher,
                _specProvider,
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
            var msg62 = new Nethermind.Network.P2P.Subprotocols.Eth.V62.GetBlockHeadersMessage();
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.GetBlockHeadersMessage(1111, msg62);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetBlockHeaders);
        }
        
        [Test]
        public void Can_handle_block_headers()
        {
            var msg62 = new Nethermind.Network.P2P.Subprotocols.Eth.V62.BlockHeadersMessage(Build.A.BlockHeader.TestObjectNTimes(3));
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.BlockHeadersMessage(1111, msg62);
        
            ((ISyncPeer) _handler).GetBlockHeaders(1, 1, 1, CancellationToken.None);
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.BlockHeaders);
        }
        
        [Test]
        public void Can_handle_get_block_bodies()
        {
            var msg62 = new Nethermind.Network.P2P.Subprotocols.Eth.V62.GetBlockBodiesMessage(new[] {Keccak.Zero, TestItem.KeccakA});
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.GetBlockBodiesMessage(1111, msg62);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetBlockBodies);
        }
        
        [Test]
        public void Can_handle_block_bodies()
        {
            var msg62 = new Nethermind.Network.P2P.Subprotocols.Eth.V62.BlockBodiesMessage(Build.A.Block.TestObjectNTimes(3));
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.BlockBodiesMessage(1111, msg62);

            HandleIncomingStatusMessage();
            ((ISyncPeer) _handler).GetBlockBodies(new List<Keccak>(new[] {Keccak.Zero}), CancellationToken.None);
            HandleZeroMessage(msg66, Eth66MessageCode.BlockBodies);
        }
        
        [Test]
        public void Can_handle_get_pooled_transactions()
        {
            var msg65 = new Nethermind.Network.P2P.Subprotocols.Eth.V65.GetPooledTransactionsMessage(new[] {Keccak.Zero, TestItem.KeccakA});
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.GetPooledTransactionsMessage(1111, msg65);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetPooledTransactions);
        }
        
        [Test]
        public void Can_handle_pooled_transactions()
        {
            Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(1, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            var msg65 = new Nethermind.Network.P2P.Subprotocols.Eth.V65.PooledTransactionsMessage(new[] {tx});
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.PooledTransactionsMessage(1111, msg65);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.PooledTransactions);
        }
        
        [Test]
        public void Can_handle_get_node_data()
        {
            var msg63 = new Nethermind.Network.P2P.Subprotocols.Eth.V63.GetNodeDataMessage(new[] {Keccak.Zero, TestItem.KeccakA});
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.GetNodeDataMessage(1111, msg63);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetNodeData);
        }
        
        [Test]
        public void Can_handle_node_data()
        {
            var msg63 = new Nethermind.Network.P2P.Subprotocols.Eth.V63.NodeDataMessage(System.Array.Empty<byte[]>());
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.NodeDataMessage(1111, msg63);

            HandleIncomingStatusMessage();
            ((ISyncPeer)_handler).GetNodeData(new List<Keccak>(new[] {Keccak.Zero}), CancellationToken.None);
            HandleZeroMessage(msg66, Eth66MessageCode.NodeData);
        }
        
        [Test]
        public void Can_handle_get_receipts()
        {
            var msg63 = new Nethermind.Network.P2P.Subprotocols.Eth.V63.GetReceiptsMessage(new[] {Keccak.Zero, TestItem.KeccakA});
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.GetReceiptsMessage(1111, msg63);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetReceipts);
        }
        
        [Test]
        public void Can_handle_receipts()
        {
            var msg63 = new Nethermind.Network.P2P.Subprotocols.Eth.V63.ReceiptsMessage(System.Array.Empty<TxReceipt[]>());
            var msg66 = new Nethermind.Network.P2P.Subprotocols.Eth.V66.ReceiptsMessage(1111, msg63);

            HandleIncomingStatusMessage();
            ((ISyncPeer)_handler).GetReceipts(new List<Keccak>(new[] {Keccak.Zero}), CancellationToken.None);
            HandleZeroMessage(msg66, Eth66MessageCode.Receipts);
        }
        
        private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
        {
            IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg);
            getBlockHeadersPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket) {PacketType = (byte) messageCode});
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
