// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
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
using Nethermind.Network.Contract.Messages;
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
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;
using GetNodeDataMessage = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.GetNodeDataMessage;
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

            _disposables = [];
            _session = Substitute.For<ISession>();
            Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _session.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(c => c.Arg<P2PMessage>().AddTo(_disposables));
            _syncManager = Substitute.For<ISyncServer>();
            _transactionPool = Substitute.For<ITxPool>();
            _specProvider = Substitute.For<ISpecProvider>();
            _gossipPolicy = Substitute.For<IGossipPolicy>();
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            _timerFactory = Substitute.For<ITimerFactory>();
            _handler = CreateHandler(RunImmediatelyScheduler.Instance);
            _handler.Init();
        }

        private Eth66ProtocolHandler CreateHandler(IBackgroundTaskScheduler backgroundTaskScheduler) =>
            new(
                _session,
                _svc,
                new NodeStatsManager(_timerFactory, LimboLogs.Instance),
                _syncManager,
                backgroundTaskScheduler,
                _transactionPool,
                _gossipPolicy,
                new ForkInfo(_specProvider, _syncManager),
                LimboLogs.Instance);

        [TearDown]
        public void TearDown()
        {
            _handler?.Dispose();
            _session?.Dispose();
            _syncManager?.Dispose();
            _disposables?.Dispose();
        }

        [Test]
        public void Metadata_correct()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_handler.ProtocolCode, Is.EqualTo("eth"));
                Assert.That(_handler.Name, Is.EqualTo("eth66"));
                Assert.That(_handler.ProtocolVersion, Is.EqualTo(66));
                Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(17));
                Assert.That(_handler.IncludeInTxPool, Is.True);
                Assert.That(_handler.ClientId, Is.EqualTo(_session.Node?.ClientId));
                Assert.That(_handler.HeadHash, Is.Null);
                Assert.That(_handler.HeadNumber, Is.EqualTo(0));
            }
        }

        [Test]
        public void Can_handle_get_block_headers()
        {
            using GetBlockHeadersMessage msg62 = new();
            using Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage msg66 = new(1111, msg62);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetBlockHeaders);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage>());
        }

        [Test]
        public async Task Can_handle_block_headers()
        {
            using BlockHeadersMessage msg62 = new(Build.A.BlockHeader.TestObjectNTimes(3).ToPooledList());
            using Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage msg66 = new(1111, msg62);

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
            using BlockHeadersMessage msg62 = new(Build.A.BlockHeader.TestObjectNTimes(3).ToPooledList());
            using Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage msg66 = new(1111, msg62);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.BlockHeaders);
            Assert.That(act, Throws.TypeOf<SubprotocolException>());
        }

        [Test]
        public void Can_handle_get_block_bodies()
        {
            using GetBlockBodiesMessage msg62 = new(new[] { Keccak.Zero, TestItem.KeccakA });
            using Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockBodiesMessage msg66 = new(1111, msg62);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetBlockBodies);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage>());
        }

        [Test]
        public void Should_throw_when_receiving_get_block_bodies_before_status()
        {
            using GetBlockBodiesMessage msg62 = new(new[] { Keccak.Zero, TestItem.KeccakA });
            using Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockBodiesMessage msg66 = new(1111, msg62);

            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.GetBlockBodies);

            Assert.That(act, Throws.TypeOf<SubprotocolException>());
            _session.DidNotReceive().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage>());
        }

        [Test]
        public async Task Can_handle_block_bodies()
        {
            using BlockBodiesMessage msg62 = new(Build.A.Block.TestObjectNTimes(3));
            using Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage msg66 = new(1111, msg62);

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
            using BlockBodiesMessage msg62 = new(Build.A.Block.TestObjectNTimes(3));
            using Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage msg66 = new(1111, msg62);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.BlockBodies);
            Assert.That(act, Throws.TypeOf<SubprotocolException>());
        }

        [Test]
        public void Can_handle_get_pooled_transactions()
        {
            using Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage msg66 = new(new[] { Keccak.Zero, TestItem.KeccakA }.Select(static h => h.ValueHash256).ToArray().ToPooledList());

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetPooledTransactions);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage>());
        }

        [Test]
        public void Should_schedule_GetPooledTransactions_without_request_handler_delegate()
        {
            _handler.Dispose();
            RecordingBackgroundTaskScheduler backgroundTaskScheduler = new();
            _handler = CreateHandler(backgroundTaskScheduler);
            _handler.Init();

            using GetPooledTransactionsMessage66 firstMessage = new(new[] { Keccak.Zero }.Select(static h => h.ValueHash256).ToArray().ToPooledList());
            using GetPooledTransactionsMessage66 secondMessage = new(new[] { TestItem.KeccakA }.Select(static h => h.ValueHash256).ToArray().ToPooledList());

            HandleIncomingStatusMessage();
            HandleZeroMessage(firstMessage, Eth66MessageCode.GetPooledTransactions);
            HandleZeroMessage(secondMessage, Eth66MessageCode.GetPooledTransactions);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(backgroundTaskScheduler.ScheduledFulfillFuncs.Count, Is.EqualTo(2));
                Assert.That(backgroundTaskScheduler.ScheduledFulfillFuncs[1], Is.SameAs(backgroundTaskScheduler.ScheduledFulfillFuncs[0]));
                Assert.That(backgroundTaskScheduler.ScheduledRequestsHaveDelegateFields, Is.EqualTo(new[] { false, false }));
            }
        }

        [Test]
        public void Can_handle_pooled_transactions_once()
        {
            Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(1), TestItem.PrivateKeyA).TestObject;
            using PooledTransactionsMessage msg65 = new(new ArrayPoolList<Transaction>(1) { tx });
            HandleIncomingStatusMessage();
            long requestId = RequestTransaction(tx);
            using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage msg66 = new(requestId, msg65);

            HandleZeroMessage(msg66, Eth66MessageCode.PooledTransactions);
            HandleZeroMessage(msg66, Eth66MessageCode.PooledTransactions);

            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }

        private long RequestTransaction(Transaction tx)
        {
            _handler.HandleMessage(PooledTransactionRequestMessage.New(tx.Hash!));
            return _session.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<GetPooledTransactionsMessage66>()).Last().RequestId;
        }

        [Test]
        public void Unsolicited_pooled_response_is_not_decoded()
        {
            using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                1111, new PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
            using DisposableByteBuffer packet = _svc.ZeroSerialize(response).AsDisposable();
            packet.EnsureWritable(1);
            packet.WriteByte(0);
            packet.ReadByte();
            HandleIncomingStatusMessage();

            Assert.That(() => _handler.HandleMessage(new ZeroPacket(packet) { PacketType = Eth66MessageCode.PooledTransactions }), Throws.Nothing);
            _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }

        public enum BudgetMessageKind { Broadcast, CorrelatedPooled, UncorrelatedPooled }

        [Test]
        public async Task Budget_rejection_excludes_correlated_responses_from_flood_sampling(
            [Values] bool peerLimit, [Values] BudgetMessageKind messageKind, [Values] bool malformed)
        {
            bool pooledResponse = messageKind != BudgetMessageKind.Broadcast;
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = true };
            _handler.Dispose();
            _handler = CreateHandler(scheduler);
            HandleIncomingStatusMessage();
            Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
            long requestId = RequestTransaction(tx);
            using CompositeDisposable reservations = [];
            try
            {
                if (peerLimit)
                {
                    using TransactionsMessage emptyBroadcast = new(IOwnedReadOnlyList<Transaction>.Empty);
                    for (int i = 0; i < InboundTransactionBudget.PeerLimit / InboundTransactionBudget.MinimumCharge; i++)
                        HandleZeroMessage(emptyBroadcast, emptyBroadcast.PacketType);
                }
                else
                {
                    for (int i = 0; i < InboundTransactionBudget.GlobalLimit / InboundTransactionBudget.PeerLimit; i++)
                        new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit)!.AddTo(reservations);
                }
                Assert.That(_handler.RequestedPooledTransactionHashes, Is.EqualTo(1));
                int scheduled = scheduler.ScheduledFulfillFuncs.Count;
                using P2PMessage response = pooledResponse
                    ? new Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage(
                        messageKind == BudgetMessageKind.UncorrelatedPooled ? requestId ^ 1 : requestId,
                        new PooledTransactionsMessage(new ArrayPoolList<Transaction>(1) { tx }))
                    : new TransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty);
                using DisposableByteBuffer packet = (pooledResponse
                    ? _svc.ZeroSerialize((Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage)response)
                    : _svc.ZeroSerialize((TransactionsMessage)response)).AsDisposable();
                if (malformed)
                {
                    packet.EnsureWritable(1);
                    packet.WriteByte(0);
                }
                packet.ReadByte();

                Assert.That(() => _handler.HandleMessage(new ZeroPacket(packet) { PacketType = (byte)response.PacketType }), Throws.Nothing);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(_handler.RequestedPooledTransactionHashes, Is.EqualTo(messageKind == BudgetMessageKind.CorrelatedPooled ? 0 : 1));
                    Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(scheduled));
                    _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
                }
            }
            finally
            {
                await scheduler.Drain(new CancellationToken(true));
            }
        }

        [Test]
        public async Task Queued_transaction_holds_budget_until_submission_finishes([Values] bool cancelled)
        {
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = true };
            _handler.Dispose();
            _handler = CreateHandler(scheduler);
            HandleIncomingStatusMessage();
            using CompositeDisposable reservations = [];
            ReserveAllButOneMessage(scheduler, reservations);
            Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
            using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                RequestTransaction(tx), new PooledTransactionsMessage(new[] { tx }.ToPooledList()));

            HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
            Assert.That(new InboundTransactionBudget(scheduler).TryReserve(1), Is.Null);
            await scheduler.Drain(new CancellationToken(cancelled));
            using InboundTransactionBudget.Reservation available = new InboundTransactionBudget(scheduler).TryReserve(1)!;
            Assert.That(available, Is.Not.Null);
            _transactionPool.Received(cancelled ? 0 : 1).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }

        [Test]
        public async Task Rescheduling_keeps_transaction_budget_reserved()
        {
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = true };
            _handler.Dispose();
            _handler = CreateHandler(scheduler);
            HandleIncomingStatusMessage();
            using CompositeDisposable reservations = [];
            ReserveAllButOneMessage(scheduler, reservations);
            Transaction first = Build.A.Transaction.WithNonce(0).SignedAndResolved().TestObject;
            Transaction second = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;
            using CancellationTokenSource cancellation = new();
            _transactionPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(_ =>
            {
                cancellation.Cancel();
                return AcceptTxResult.Accepted;
            });
            using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                RequestTransaction(first), new PooledTransactionsMessage(new[] { first, second }.ToPooledList()));
            HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

            await scheduler.RunNext(cancellation.Token);
            Assert.That(new InboundTransactionBudget(scheduler).TryReserve(1), Is.Null);
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            await scheduler.Drain(CancellationToken.None);
            using InboundTransactionBudget.Reservation available = new InboundTransactionBudget(scheduler).TryReserve(1)!;
            Assert.That(available, Is.Not.Null);
            _transactionPool.Received(2).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }

        private static void ReserveAllButOneMessage(IBackgroundTaskScheduler scheduler, CompositeDisposable reservations)
        {
            for (int i = 0; i < InboundTransactionBudget.GlobalLimit / InboundTransactionBudget.PeerLimit - 1; i++)
                new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit)!.AddTo(reservations);
            new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit - InboundTransactionBudget.MinimumCharge)!.AddTo(reservations);
        }

        [Test]
        public void Rejected_or_malformed_response_releases_budget([Values] bool malformed)
        {
            RecordingBackgroundTaskScheduler scheduler = new() { Reject = true };
            _handler.Dispose();
            _handler = CreateHandler(scheduler);
            HandleIncomingStatusMessage();
            Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
            using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                RequestTransaction(tx), new PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
            using DisposableByteBuffer packet = _svc.ZeroSerialize(response).AsDisposable();
            if (malformed)
            {
                packet.EnsureWritable(1);
                packet.WriteByte(0);
            }
            packet.ReadByte();
            Action receive = () => _handler.HandleMessage(new ZeroPacket(packet) { PacketType = Eth66MessageCode.PooledTransactions });
            if (malformed)
                Assert.That(receive, Throws.Exception);
            else
                Assert.That(receive, Throws.Nothing);

            using CompositeDisposable reservations = [];
            for (int i = 0; i < InboundTransactionBudget.GlobalLimit / InboundTransactionBudget.PeerLimit; i++)
            {
                InboundTransactionBudget.Reservation? reservation = new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit);
                Assert.That(reservation, Is.Not.Null);
                reservation!.AddTo(reservations);
            }
        }

        [Test]
        public void Transaction_budget_is_shared_and_released_with_owned_list([Values] bool sharedBudgetFull)
        {
            RecordingBackgroundTaskScheduler scheduler = new();
            InboundTransactionBudget budget = new(scheduler);
            using InboundTransactionBudget.Reservation reservation = budget.TryReserve(InboundTransactionBudget.PeerLimit)!;
            IOwnedReadOnlyList<Transaction> transactions = Substitute.For<IOwnedReadOnlyList<Transaction>>();
            reservation.Attach(transactions);
            using CompositeDisposable otherPeers = [];
            if (sharedBudgetFull)
            {
                for (int i = 1; i < InboundTransactionBudget.GlobalLimit / InboundTransactionBudget.PeerLimit; i++)
                    new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit)!.AddTo(otherPeers);
            }
            using InboundTransactionBudget.Reservation? excess = budget.TryReserve(1);
            using InboundTransactionBudget.Reservation? anotherPeer = new InboundTransactionBudget(scheduler).TryReserve(1);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(excess, Is.Null);
                Assert.That(anotherPeer is null, Is.EqualTo(sharedBudgetFull));
            }
            reservation.Dispose();
            reservation.Dispose();
            using InboundTransactionBudget.Reservation replacement = budget.TryReserve(InboundTransactionBudget.PeerLimit)!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(replacement, Is.Not.Null);
                transactions.Received(1).Dispose();
            }
        }

        [Test]
        public void Transaction_budget_limits_all_peers_but_not_other_schedulers()
        {
            RecordingBackgroundTaskScheduler scheduler = new();
            using CompositeDisposable reservations = [];
            for (int i = 0; i < InboundTransactionBudget.GlobalLimit / InboundTransactionBudget.PeerLimit; i++)
                new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit)!.AddTo(reservations);
            using InboundTransactionBudget.Reservation independent = new InboundTransactionBudget(new RecordingBackgroundTaskScheduler()).TryReserve(1)!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(new InboundTransactionBudget(scheduler).TryReserve(1), Is.Null);
                Assert.That(independent, Is.Not.Null);
            }
        }

        [Test]
        public void Concurrent_transaction_reservations_respect_both_limits([Values(1, 2, 8)] int peers)
        {
            RecordingBackgroundTaskScheduler scheduler = new();
            InboundTransactionBudget[] budgets = new InboundTransactionBudget[peers];
            for (int i = 0; i < peers; i++) budgets[i] = new(scheduler);
            const int charge = InboundTransactionBudget.PeerLimit / 4;

            for (int round = 0; round < 32; round++)
            {
                InboundTransactionBudget.Reservation?[] reservations = new InboundTransactionBudget.Reservation?[128];
                try
                {
                    Parallel.For(0, reservations.Length, i => reservations[i] = budgets[i % peers].TryReserve(charge));
                    int[] admitted = new int[peers];
                    for (int i = 0; i < reservations.Length; i++)
                        if (reservations[i] is not null) admitted[i % peers] += charge;
                    Assert.That(admitted, Is.All.LessThanOrEqualTo(InboundTransactionBudget.PeerLimit));
                    Assert.That(admitted.Sum(), Is.EqualTo(Math.Min(peers * InboundTransactionBudget.PeerLimit, InboundTransactionBudget.GlobalLimit)));
                }
                finally
                {
                    Parallel.ForEach(reservations, reservation => reservation?.Dispose());
                }
            }
        }

        [Test]
        public async Task Concurrent_reserve_release_and_rollback_preserve_capacity()
        {
            const int workers = 8;
            const int peers = 4;
            RecordingBackgroundTaskScheduler scheduler = new();
            using CompositeDisposable held = [];
            for (int i = 0; i < 3; i++)
                new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit)!.AddTo(held);
            using InboundTransactionBudget.Reservation blockedSlot = new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit)!;
            using Barrier start = new(workers, _ => blockedSlot.Dispose());
            InboundTransactionBudget[] budgets = new InboundTransactionBudget[peers];
            for (int i = 0; i < peers; i++) budgets[i] = new(scheduler);
            int[] peerBytes = new int[peers];
            int sharedBytes = 0;

            await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() =>
            {
                int peer = worker % peers;
                InboundTransactionBudget budget = budgets[peer];
                using InboundTransactionBudget.Reservation? rejected = budget.TryReserve(InboundTransactionBudget.MinimumCharge);
                Assert.That(start.SignalAndWait(TimeSpan.FromSeconds(30)), Is.True);
                Assert.That(rejected, Is.Null);

                for (int i = 0; i < 2000; i++)
                {
                    int charge = InboundTransactionBudget.PeerLimit / (2 + (i + worker) % 3);
                    using InboundTransactionBudget.Reservation? reservation = budget.TryReserve(charge);
                    if (reservation is null) continue;
                    int peerTotal = Interlocked.Add(ref peerBytes[peer], charge);
                    int sharedTotal = Interlocked.Add(ref sharedBytes, charge);
                    try
                    {
                        Assert.That(peerTotal, Is.LessThanOrEqualTo(InboundTransactionBudget.PeerLimit));
                        Assert.That(sharedTotal, Is.LessThanOrEqualTo(InboundTransactionBudget.PeerLimit));
                        Thread.Yield();
                    }
                    finally
                    {
                        Interlocked.Add(ref sharedBytes, -charge);
                        Interlocked.Add(ref peerBytes[peer], -charge);
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));

            held.Dispose();
            foreach (InboundTransactionBudget budget in budgets)
            {
                using InboundTransactionBudget.Reservation? reservation = budget.TryReserve(InboundTransactionBudget.PeerLimit);
                Assert.That(reservation, Is.Not.Null);
                using InboundTransactionBudget.Reservation? peerExcess = budget.TryReserve(1);
                Assert.That(peerExcess, Is.Null);
            }
            using CompositeDisposable recovered = [];
            foreach (InboundTransactionBudget budget in budgets)
            {
                InboundTransactionBudget.Reservation? reservation = budget.TryReserve(InboundTransactionBudget.PeerLimit);
                Assert.That(reservation, Is.Not.Null);
                reservation!.AddTo(recovered);
            }
            using InboundTransactionBudget.Reservation? excess = new InboundTransactionBudget(scheduler).TryReserve(1);
            Assert.That(excess, Is.Null);
        }

        [Test]
        public void Shared_budget_rejection_rolls_back_peer_charge()
        {
            RecordingBackgroundTaskScheduler scheduler = new();
            InboundTransactionBudget rejectedPeer = new(scheduler);
            using (CompositeDisposable reservations = [])
            {
                for (int i = 0; i < InboundTransactionBudget.GlobalLimit / InboundTransactionBudget.PeerLimit; i++)
                    new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit)!.AddTo(reservations);
                for (int i = 0; i < 100; i++)
                {
                    Assert.That(rejectedPeer.TryReserve(InboundTransactionBudget.PeerLimit), Is.Null);
                }
            }
            using InboundTransactionBudget.Reservation? available = rejectedPeer.TryReserve(InboundTransactionBudget.PeerLimit);
            Assert.That(available, Is.Not.Null);
        }

        [Test]
        public void Concurrent_disposal_releases_transaction_budget_once()
        {
            InboundTransactionBudget budget = new(new RecordingBackgroundTaskScheduler());
            InboundTransactionBudget.Reservation reservation = budget.TryReserve(InboundTransactionBudget.PeerLimit)!;
            Parallel.For(0, 32, _ => reservation.Dispose());
            using InboundTransactionBudget.Reservation? replacement = budget.TryReserve(InboundTransactionBudget.PeerLimit);
            Assert.That(replacement, Is.Not.Null);
            using InboundTransactionBudget.Reservation? excess = budget.TryReserve(1);
            Assert.That(excess, Is.Null);
        }

        [Test]
        public void Can_handle_get_node_data()
        {
            using GetNodeDataMessage msg63 = new(new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());
            using Network.P2P.Subprotocols.Eth.V66.Messages.GetNodeDataMessage msg66 = new(1111, msg63);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetNodeData);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage>());
        }

        [Test]
        public async Task Can_handle_node_data()
        {
            using NodeDataMessage msg63 = new(new ByteArrayListAdapter(ArrayPoolList<byte[]>.Empty()));
            using Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage msg66 = new(1111, msg63);

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
            using NodeDataMessage msg63 = new(new ByteArrayListAdapter(ArrayPoolList<byte[]>.Empty()));
            using Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage msg66 = new(1111, msg63);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.NodeData);
            Assert.That(act, Throws.TypeOf<SubprotocolException>());
        }

        [Test]
        public void Can_handle_get_receipts()
        {
            using GetReceiptsMessage msg63 = new(new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());
            using Network.P2P.Subprotocols.Eth.V66.Messages.GetReceiptsMessage msg66 = new(1111, msg63);

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg66, Eth66MessageCode.GetReceipts);
            _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage>());
        }

        [Test]
        public void Should_throw_when_receiving_get_receipts_before_status()
        {
            using GetReceiptsMessage msg63 = new(new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());
            using Network.P2P.Subprotocols.Eth.V66.Messages.GetReceiptsMessage msg66 = new(1111, msg63);

            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.GetReceipts);

            Assert.That(act, Throws.TypeOf<SubprotocolException>());
            _session.DidNotReceive().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage>());
        }

        [Test]
        public async Task Can_handle_receipts()
        {
            using ReceiptsMessage msg63 = new(ArrayPoolList<TxReceipt[]>.Empty());
            using Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage msg66 = new(1111, msg63);

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
            using ReceiptsMessage msg63 = new(ArrayPoolList<TxReceipt[]>.Empty());
            using Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage msg66 = new(1111, msg63);

            HandleIncomingStatusMessage();
            System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.Receipts);
            Assert.That(act, Throws.TypeOf<SubprotocolException>());
        }

        [Test]
        public void Should_reject_unrequested_response_before_decoding(
            [Values(Eth66MessageCode.BlockHeaders, Eth66MessageCode.BlockBodies, Eth66MessageCode.Receipts, Eth66MessageCode.NodeData)] int messageCode)
        {
            HandleIncomingStatusMessage();
            UndecodableResponse.AssertRejectedAsUnrequested(_handler.HandleMessage, messageCode);
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

        [Test]
        public void Should_send_single_retry_without_registering_another_retry()
        {
            _transactionPool.ClearReceivedCalls();

            _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));

            _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage66>(m =>
                m.EthMessage.Hashes.Count == 1 && m.EthMessage.Hashes[0] == TestItem.KeccakA));
            _transactionPool.DidNotReceive().NotifyAboutTx(
                Arg.Any<ValueHash256>(),
                Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
        }

        private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
        {
            using DisposableByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg).AsDisposable();
            getBlockHeadersPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket) { PacketType = (byte)messageCode });
        }
        private void HandleIncomingStatusMessage()
        {
            using StatusMessage statusMsg = new();
            statusMsg.GenesisHash = _genesisBlock.Hash;
            statusMsg.BestHash = _genesisBlock.Hash;

            using DisposableByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg).AsDisposable();
            statusPacket.ReadByte();
            _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
        }

        private sealed class RecordingBackgroundTaskScheduler : IBackgroundTaskScheduler
        {
            public bool Defer { get; init; }
            public bool Reject { get; init; }
            private readonly Queue<Func<CancellationToken, Task>> _pending = new();

            public Task RunNext(CancellationToken cancellationToken) => _pending.Dequeue()(cancellationToken);

            public async Task Drain(CancellationToken cancellationToken)
            {
                while (_pending.TryDequeue(out Func<CancellationToken, Task>? pending))
                    await pending(cancellationToken);
            }

            public List<Delegate> ScheduledFulfillFuncs { get; } = [];
            public List<bool> ScheduledRequestsHaveDelegateFields { get; } = [];

            public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
                where TReq : notnull, IBackgroundTaskRequest<TReq>
            {
                if (Reject) return false;
                ScheduledRequestsHaveDelegateFields.Add(HasDelegateField<TReq>());
                ScheduledFulfillFuncs.Add(fulfillFunc);
                if (Defer)
                    _pending.Enqueue(token => fulfillFunc(request, token));
                else
                    fulfillFunc(request, CancellationToken.None).GetAwaiter().GetResult();
                return true;
            }

            private static bool HasDelegateField<TReq>()
            {
                FieldInfo[] fields = typeof(TReq).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (typeof(Delegate).IsAssignableFrom(field.FieldType))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
