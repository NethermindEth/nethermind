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

using System.Net;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
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
        private ITxPool _transactionPool;
        private IPooledTxsRequestor _pooledTxsRequestor;
        private ISpecProvider _specProvider;
        private Block _genesisBlock;
        private Eth65ProtocolHandler _handler;

        [SetUp]
        public void Setup()
        {
            _svc = Build.A.SerializationService().WithEth65().TestObject;

            NetworkDiagTracer.IsEnabled = true;

            _session = Substitute.For<ISession>();
            Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _syncManager = Substitute.For<ISyncServer>();
            _transactionPool = Substitute.For<ITxPool>();
            _pooledTxsRequestor = Substitute.For<IPooledTxsRequestor>();
            _specProvider = Substitute.For<ISpecProvider>();
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _handler = new Eth65ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(timerFactory, LimboLogs.Instance),
                _syncManager,
                _transactionPool,
                _pooledTxsRequestor,
                Policy.FullGossip,
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
            _handler.Name.Should().Be("eth65");
            _handler.ProtocolVersion.Should().Be(65);
            _handler.MessageIdSpaceSize.Should().Be(17);
            _handler.IncludeInTxPool.Should().BeTrue();
            _handler.ClientId.Should().Be(_session.Node?.ClientId);
            _handler.HeadHash.Should().BeNull();
            _handler.HeadNumber.Should().Be(0);
        }
        
        [TestCase(1)]
        [TestCase(NewPooledTransactionHashesMessage.MaxCount - 1)]
        [TestCase(NewPooledTransactionHashesMessage.MaxCount)]
        public void should_send_up_to_MaxCount_hashes_in_one_NewPooledTransactionHashesMessage(int txCount)
        {
            Transaction[] txs = new Transaction[txCount];

            for (int i = 0; i < txCount; i++)
            {
                txs[i] = Build.A.Transaction.SignedAndResolved().TestObject;
            }

            _handler.SendNewTransactions(txs, false);
            
            _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage>(m => m.Hashes.Count == txCount));
        }
        
        [TestCase(3201)]
        [TestCase(10000)]
        [TestCase(20000)]
        public void should_send_more_than_MaxCount_hashes_in_more_than_one_NewPooledTransactionHashesMessage(int txCount)
        {
            int messagesCount = txCount / NewPooledTransactionHashesMessage.MaxCount + 1;
            int nonFullMsgTxsCount = txCount % NewPooledTransactionHashesMessage.MaxCount;
            Transaction[] txs = new Transaction[txCount];

            for (int i = 0; i < txCount; i++)
            {
                txs[i] = Build.A.Transaction.SignedAndResolved().TestObject;
            }

            _handler.SendNewTransactions(txs, false);
            
            _session.Received(messagesCount).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage>(m => m.Hashes.Count == NewPooledTransactionHashesMessage.MaxCount || m.Hashes.Count == nonFullMsgTxsCount));
        }
    }
}
