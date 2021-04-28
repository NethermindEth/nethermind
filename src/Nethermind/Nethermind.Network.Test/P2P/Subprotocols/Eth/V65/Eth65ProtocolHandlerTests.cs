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
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Spec;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V65
{
    [TestFixture, Parallelizable(ParallelScope.Self)]
    public class Eth65ProtocolHandlerTests
    {
        private ISession _session;
        private IMessageSerializationService _svc;
        private ISyncServer _syncManager;
        private ITxPool _txPool;
        private ISpecProvider _specProvider;
        private Block _genesisBlock;
        private ILogManager _logManager;
        private Eth62ProtocolHandler _handler;

        [SetUp]
        public void Setup()
        {
            _svc = Build.A.SerializationService().WithEth65().TestObject;

            NetworkDiagTracer.IsEnabled = true;

            _session = Substitute.For<ISession>();
            Node node = new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _syncManager = Substitute.For<ISyncServer>();
            _specProvider = MainnetSpecProvider.Instance;
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            Block block = Build.A.Block.WithNumber(0).TestObject;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.Head.Returns(block);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).Returns(new ReleaseSpec() { IsEip1559Enabled = false });
            var transactionComparerProvider = new TransactionComparerProvider(specProvider, blockTree);
            _logManager = LimboLogs.Instance;
            _txPool = new TxPool.TxPool(NullTxStorage.Instance,
                new EthereumEcdsa(_specProvider.ChainId, _logManager),
                new ChainHeadSpecProvider(_specProvider, Substitute.For<IBlockFinder>()),
                new TxPoolConfig() {GasLimit = 1_000_000},
                new StateProvider(new TrieStore(new MemDb(), _logManager), new MemDb(), _logManager),
                new TxValidator(_specProvider.ChainId),
                _logManager,
                transactionComparerProvider.GetDefaultComparer());
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _handler = new Eth65ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(timerFactory, _logManager),
                _syncManager,
                _txPool,
                _specProvider,
                _logManager);
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
            _handler.Name.Should().Be("eth65");
            _handler.ProtocolVersion.Should().Be(65);
            _handler.MessageIdSpaceSize.Should().Be(17);
            _handler.IncludeInTxPool.Should().BeTrue();
            _handler.ClientId.Should().Be(_session.Node?.ClientId);
            _handler.HeadHash.Should().BeNull();
            _handler.HeadNumber.Should().Be(0);
        }
        
        [Test]
        public void Can_handle_newPooledTransactionHashes()
        {
            _txPool.TryAddToPendingHashes(TestItem.KeccakA).Should().Be(true);
            _txPool.TryAddToPendingHashes(TestItem.KeccakA).Should().Be(false);
            
            Keccak[] hashes = {TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            NewPooledTransactionHashesMessage msg = new NewPooledTransactionHashesMessage(hashes);
            HandleIncomingStatusMessage(); 
            HandleZeroMessage(msg, Eth65MessageCode.NewPooledTransactionHashes);
            
            _txPool.TryAddToPendingHashes(TestItem.KeccakA).Should().Be(false);
            _txPool.TryAddToPendingHashes(TestItem.KeccakB).Should().Be(false);
            _txPool.TryAddToPendingHashes(TestItem.KeccakC).Should().Be(false);
            _txPool.TryAddToPendingHashes(TestItem.KeccakD).Should().Be(true);
            
            _session.Received().DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(t => t.Hashes.Count == 2));
        }
        
        [Test]
        public void Can_handle_pooledTransactions()
        {
            PooledTransactionsMessage msg = new PooledTransactionsMessage(new List<Transaction>(Build.A.Transaction.SignedAndResolved().TestObjectNTimes(3)));
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth65MessageCode.PooledTransactions);
        }
        
        [Test]
        public void Can_handle_getPooledTransactions()
        {
            Keccak[] hashes = {TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            GetPooledTransactionsMessage msg = new GetPooledTransactionsMessage(hashes);
            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth65MessageCode.GetPooledTransactions);
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
