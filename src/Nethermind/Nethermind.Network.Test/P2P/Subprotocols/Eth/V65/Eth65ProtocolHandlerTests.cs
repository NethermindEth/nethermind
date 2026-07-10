// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Rlpx;
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
        private ISession _session = null!;
        private IMessageSerializationService _svc = null!;
        private ISyncServer _syncManager = null!;
        private ITxPool _transactionPool = null!;
        private ISpecProvider _specProvider = null!;
        private Block _genesisBlock = null!;
        private Eth65ProtocolHandler _handler = null!;
        private ITxGossipPolicy _txGossipPolicy = null!;
        private CompositeDisposable _disposables = null!;

        [SetUp]
        public void Setup()
        {
            _svc = Build.A.SerializationService().WithEth65().TestObject;

            NetworkDiagTracer.IsEnabled = true;

            _disposables = [];
            _session = Substitute.For<ISession>();
            Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _session.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(c => c.Arg<P2PMessage>().AddTo(_disposables));
            _syncManager = Substitute.For<ISyncServer>();
            _transactionPool = Substitute.For<ITxPool>();
            _specProvider = Substitute.For<ISpecProvider>();
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
            _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
            _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
            _handler = new Eth65ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(timerFactory, LimboLogs.Instance),
                _syncManager,
                RunImmediatelyScheduler.Instance,
                _transactionPool,
                Policy.FullGossip,
                new ForkInfo(_specProvider, _syncManager),
                LimboLogs.Instance,
                _txGossipPolicy);
            _handler.Init();
        }

        [TearDown]
        public void TearDown()
        {
            _handler?.Dispose();
            _session?.Dispose();
            _syncManager?.Dispose();
            _disposables.Dispose();
        }

        [Test]
        public void Metadata_correct()
        {
            Assert.That(_handler.ProtocolCode, Is.EqualTo("eth"));
            Assert.That(_handler.Name, Is.EqualTo("eth65"));
            Assert.That(_handler.ProtocolVersion, Is.EqualTo(65));
            Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(17));
            Assert.That(_handler.IncludeInTxPool, Is.True);
            Assert.That(_handler.ClientId, Is.EqualTo(_session.Node?.ClientId));
            Assert.That(_handler.HeadHash, Is.Null);
            Assert.That(_handler.HeadNumber, Is.EqualTo(0));
        }

        [TestCase(1)]
        [TestCase(NewPooledTransactionHashesMessage.MaxCount - 1)]
        [TestCase(NewPooledTransactionHashesMessage.MaxCount)]
        public void should_send_up_to_MaxCount_hashes_in_one_NewPooledTransactionHashesMessage(int txCount)
        {
            Transaction[] txs = new Transaction[txCount];

            for (int i = 0; i < txCount; i++)
            {
                txs[i] = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved().TestObject;
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
                txs[i] = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved().TestObject;
            }

            _handler.SendNewTransactions(txs, false);

            _session.Received(messagesCount).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage>(m => m.Hashes.Count == NewPooledTransactionHashesMessage.MaxCount || m.Hashes.Count == nonFullMsgTxsCount));
        }

        [Test]
        public async Task should_send_requested_PooledTransactions_up_to_MaxPacketSize()
        {
            Transaction tx = Build.A.Transaction.WithData(new byte[1024]).SignedAndResolved().TestObject;
            int sizeOfOneTx = tx.GetLength();
            int numberOfTxsInOneMsg = TransactionsMessage.MaxPacketSize / sizeOfOneTx;
            _transactionPool.TryGetPendingTransaction(Arg.Any<Hash256>(), out Arg.Any<Transaction>())
                .Returns(x =>
                {
                    x[1] = tx;
                    return true;
                });
            using GetPooledTransactionsMessage request = new(TestItem.Keccaks.ToPooledList());
            using PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);
            Assert.That(response.Transactions.Count, Is.EqualTo(numberOfTxsInOneMsg));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(32)]
        [TestCase(4096)]
        [TestCase(100000)]
        [TestCase(102400)]
        [TestCase(222222)]
        public async Task should_send_single_requested_PooledTransaction_even_if_exceed_MaxPacketSize(int dataSize)
        {
            Transaction tx = Build.A.Transaction.WithData(new byte[dataSize]).SignedAndResolved().TestObject;
            int sizeOfOneTx = tx.GetLength();
            int numberOfTxsInOneMsg = Math.Max(TransactionsMessage.MaxPacketSize / sizeOfOneTx, 1);
            _transactionPool.TryGetPendingTransaction(Arg.Any<Hash256>(), out Arg.Any<Transaction>())
                .Returns(x =>
                {
                    x[1] = tx;
                    return true;
                });
            using GetPooledTransactionsMessage request = new(new Hash256[2048].ToPooledList());
            using PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);
            Assert.That(response.Transactions.Count, Is.EqualTo(numberOfTxsInOneMsg));
        }

        [Test]
        public void should_handle_NewPooledTransactionHashesMessage([Values(true, false)] bool canGossipTransactions)
        {
            _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(canGossipTransactions);
            using NewPooledTransactionHashesMessage msg = new(new[] { TestItem.KeccakA, TestItem.KeccakB }.ToPooledList());

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth65MessageCode.NewPooledTransactionHashes);

            _session.Received(canGossipTransactions ? 1 : 0).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
        }

        [Test]
        public void should_track_new_pooled_transaction_metrics_by_peer_client()
        {
            const string client = "erigon";
            _session.Node!.ClientId = "erigon/v2.60.0/linux-amd64/go1.22";
            _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

            long announcedBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsAnnouncedByClient, client);
            long requestedBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClient, client);
            long initialRequestedBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClientAndReason, (client, "initial"));
            long initialRequestMessagesBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionRequestMessagesByClientAndReason, (client, "initial"));
            long returnedBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsReturnedByClient, client);
            long responseMessagesBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionResponseMessagesByClient, client);
            long emptyResponseMessagesBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionEmptyResponseMessagesByClient, client);

            using NewPooledTransactionHashesMessage hashesMsg = new(new[] { TestItem.KeccakA, TestItem.KeccakB }.ToPooledList());
            Transaction tx1 = Build.A.Transaction.WithNonce(1UL).SignedAndResolved().TestObject;
            Transaction tx2 = Build.A.Transaction.WithNonce(2UL).SignedAndResolved().TestObject;
            using PooledTransactionsMessage txsMsg = new(new[] { tx1, tx2 }.ToPooledList());
            using PooledTransactionsMessage emptyTxsMsg = new(Array.Empty<Transaction>().ToPooledList());

            HandleIncomingStatusMessage();
            HandleZeroMessage(hashesMsg, Eth65MessageCode.NewPooledTransactionHashes);
            HandleZeroMessage(txsMsg, Eth65MessageCode.PooledTransactions);
            HandleZeroMessage(emptyTxsMsg, Eth65MessageCode.PooledTransactions);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsAnnouncedByClient, client), Is.EqualTo(announcedBefore + 2));
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClient, client), Is.EqualTo(requestedBefore + 2));
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClientAndReason, (client, "initial")), Is.EqualTo(initialRequestedBefore + 2));
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionRequestMessagesByClientAndReason, (client, "initial")), Is.EqualTo(initialRequestMessagesBefore + 1));
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsReturnedByClient, client), Is.EqualTo(returnedBefore + 2));
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionResponseMessagesByClient, client), Is.EqualTo(responseMessagesBefore + 2));
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionEmptyResponseMessagesByClient, client), Is.EqualTo(emptyResponseMessagesBefore + 1));
            }
        }

        [Test]
        public void should_page_batched_retry_request_hashes_inside_handler()
        {
            const int txCount = 300;
            ValueHash256[] txHashes = GenerateValueHashes(txCount);
            _transactionPool.ClearReceivedCalls();

            _handler.HandleMessages(txHashes);

            _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.Hashes.Count == 256));
            _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.Hashes.Count == txCount - 256));
            _transactionPool.DidNotReceive().NotifyAboutTx(
                Arg.Any<Hash256>(),
                Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
        }

        [Test]
        public void should_track_batched_retry_request_metrics_by_peer_client()
        {
            const string client = "geth";
            _session.Node!.ClientId = "Geth/v1.16.0/linux-amd64/go1.24";
            ValueHash256[] txHashes = GenerateValueHashes(3);

            long requestedBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClient, client);
            long retryRequestedBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClientAndReason, (client, "retry"));
            long retryRequestMessagesBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionRequestMessagesByClientAndReason, (client, "retry"));

            _handler.HandleMessages(txHashes);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClient, client), Is.EqualTo(requestedBefore + 3));
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClientAndReason, (client, "retry")), Is.EqualTo(retryRequestedBefore + 3));
                Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionRequestMessagesByClientAndReason, (client, "retry")), Is.EqualTo(retryRequestMessagesBefore + 1));
            }
        }

        private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
        {
            using DisposableByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg).AsDisposable();
            getBlockHeadersPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket) { PacketType = (byte)messageCode });
        }

        private static ValueHash256[] GenerateValueHashes(int count)
        {
            ValueHash256[] txHashes = new ValueHash256[count];
            for (int i = 0; i < txHashes.Length; i++)
            {
                txHashes[i] = new Hash256(i.ToString("X64"));
            }

            return txHashes;
        }

        private static long GetMetricValue<TKey>(IDictionary<TKey, long> metric, TKey key)
            where TKey : notnull
        {
            metric.TryGetValue(key, out long value);
            return value;
        }

        private void HandleIncomingStatusMessage()
        {
            using StatusMessage statusMsg = new() { GenesisHash = _genesisBlock.Hash, BestHash = _genesisBlock.Hash };

            using DisposableByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg).AsDisposable();
            statusPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
        }
    }
}
