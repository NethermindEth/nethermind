// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
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

        [TestCase(NewPooledTransactionHashesMessage.MaxCount - 1)]
        [TestCase(NewPooledTransactionHashesMessage.MaxCount)]
        [TestCase(10000)]
        [TestCase(20000)]
        public void should_send_more_than_MaxCount_hashes_in_more_than_one_NewPooledTransactionHashesMessage(int txCount)
        {
            int nonFullMsgTxsCount = txCount % NewPooledTransactionHashesMessage.MaxCount;
            int messagesCount = txCount / NewPooledTransactionHashesMessage.MaxCount + (nonFullMsgTxsCount > 0 ? 1 : 0);
            Transaction[] txs = new Transaction[txCount];

            for (int i = 0; i < txCount; i++)
            {
                txs[i] = Build.A.Transaction.SignedAndResolved().TestObject;
            }

            _handler.SendNewTransactions(txs, false);

            _session.Received(messagesCount).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage>(m => m.Hashes.Count == NewPooledTransactionHashesMessage.MaxCount || m.Hashes.Count == nonFullMsgTxsCount));
        }

        [Test]
        public void should_send_requested_PooledTransactions_up_to_MaxPacketSize()
        {
            Transaction tx = Build.A.Transaction.WithData(new byte[1024]).SignedAndResolved().TestObject;
            int sizeOfOneTx = tx.GetLength(new TxDecoder());
            int numberOfTxsInOneMsg = TransactionsMessage.MaxPacketSize / sizeOfOneTx;
            _transactionPool.TryGetPendingTransaction(Arg.Any<Keccak>(), out Arg.Any<Transaction>())
                .Returns(x =>
                {
                    x[1] = tx;
                    return true;
                });
            GetPooledTransactionsMessage request = new(TestItem.Keccaks);
            PooledTransactionsMessage response = _handler.FulfillPooledTransactionsRequest(request, new List<Transaction>());
            response.Transactions.Count.Should().Be(numberOfTxsInOneMsg);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(32)]
        [TestCase(4096)]
        [TestCase(100000)]
        [TestCase(102400)]
        [TestCase(222222)]
        public void should_send_single_requested_PooledTransaction_even_if_exceed_MaxPacketSize(int dataSize)
        {
            Transaction tx = Build.A.Transaction.WithData(new byte[dataSize]).SignedAndResolved().TestObject;
            int sizeOfOneTx = tx.GetLength(new TxDecoder());
            int numberOfTxsInOneMsg = Math.Max(TransactionsMessage.MaxPacketSize / sizeOfOneTx, 1);
            _transactionPool.TryGetPendingTransaction(Arg.Any<Keccak>(), out Arg.Any<Transaction>())
                .Returns(x =>
                {
                    x[1] = tx;
                    return true;
                });
            GetPooledTransactionsMessage request = new(new Keccak[2048]);
            PooledTransactionsMessage response = _handler.FulfillPooledTransactionsRequest(request, new List<Transaction>());
            response.Transactions.Count.Should().Be(numberOfTxsInOneMsg);
        }
    }
}
