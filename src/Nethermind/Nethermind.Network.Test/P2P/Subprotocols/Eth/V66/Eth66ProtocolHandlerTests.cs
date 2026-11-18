// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
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
using Nethermind.Core.Test.Threading;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.FluentRlp;
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
using GetBlockBodiesMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockBodiesMessage;
using GetBlockHeadersMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;
using GetNodeDataMessage = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.GetNodeDataMessage;
using GetNodeDataMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetNodeDataMessage;
using GetReceiptsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.GetReceiptsMessage;
using GetReceiptsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetReceiptsMessage;
using NodeDataMessage = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.NodeDataMessage;
using PooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.PooledTransactionRetryCache;
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
            _session.DeliverMessage(Arg.Any<Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage>()).Returns(call =>
            {
                using DisposableByteBuffer encoded = _svc.ZeroSerialize(call.Arg<Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage>()).AsDisposable();
                return encoded.ReadableBytes;
            });
            _transactionPool = Substitute.For<ITxPool>();
            _specProvider = Substitute.For<ISpecProvider>();
            _syncManager.ServeReceipts(_specProvider);
            _gossipPolicy = Substitute.For<IGossipPolicy>();
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            _timerFactory = Substitute.For<ITimerFactory>();
            _handler = CreateHandler(RunImmediatelyScheduler.Instance);
            _handler.Init();
        }

        private Eth66ProtocolHandler CreateHandler(IBackgroundTaskScheduler backgroundTaskScheduler, TimeProvider? timeProvider = null, ITxGossipPolicy? txGossipPolicy = null) =>
            new(
                _session,
                _svc,
                new NodeStatsManager(_timerFactory, LimboLogs.Instance),
                _syncManager,
                backgroundTaskScheduler,
                _transactionPool,
                _gossipPolicy,
                new ForkInfo(_specProvider, _syncManager),
                LimboLogs.Instance, transactionsGossipPolicy: txGossipPolicy, timeProvider: timeProvider);

        [Test]
        public void Ignored_pooled_reply_releases_the_only_reply_slot()
        {
            ITxGossipPolicy policy = Substitute.For<ITxGossipPolicy>();
            policy.ShouldListenToGossipedTransactions.Returns(true);
            _handler.Dispose();
            _handler = CreateHandler(new RecordingBackgroundTaskScheduler { QueueCapacity = 48 }, txGossipPolicy: policy);
            _handler.Init();
            HandleIncomingStatusMessage();
            _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
            GetPooledTransactionsMessage66 first = Requests().Single();

            policy.ShouldListenToGossipedTransactions.Returns(false);
            using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage reply = new(first.RequestId,
                new PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
            HandleZeroMessage(reply, Eth66MessageCode.PooledTransactions);

            policy.ShouldListenToGossipedTransactions.Returns(true);
            _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
            Assert.That(Requests().Last().EthMessage.Hashes.AsSpan().ToArray(), Is.EqualTo(new[] { TestItem.KeccakB.ValueHash256 }));

            IEnumerable<GetPooledTransactionsMessage66> Requests() => _session.ReceivedCalls()
                .SelectMany(call => call.GetArguments().OfType<GetPooledTransactionsMessage66>());
        }

        [TearDown]
        public void TearDown()
        {
            _handler?.Dispose();
            _session?.Dispose();
            _syncManager?.Dispose();
            _disposables?.Dispose();
        }

        private void Receive<T>(Eth66ProtocolHandler handler, T message, int code) where T : P2PMessage
        {
            using DisposableByteBuffer packet = _svc.ZeroSerialize(message).AsDisposable();
            packet.ReadByte();
            handler.HandleMessage(new NettyBufferPacket(packet) { PacketType = (byte)code });
        }

        [Test]
        public async Task A_full_reply_quota_bounds_refusals_and_preserves_a_ten_second_pause([Values] bool releaseQuota)
        {
            HandleIncomingStatusMessage();
            ManualTimeProvider clock = new();
            await using IContainer container = CreateRequestedProgressPool(clock);
            _transactionPool = container.Resolve<ITxPool>();
            TxPool.TxPool pool = (TxPool.TxPool)_transactionPool;
            _handler.Dispose();
            _handler = CreateHandler(new RecordingBackgroundTaskScheduler(), clock);
            _handler.Init();
            HandleIncomingStatusMessage();
            ValueHash256[] held = Enumerable.Range(90000, 32).Select(index => new Hash256(index.ToString("X64")).ValueHash256).ToArray();
            foreach (ValueHash256 hash in held)
            {
                pool.RetryCache.Announced(hash, _handler);
                _handler.HandleMessage(PooledTransactionRequestMessage.New(hash));
            }
            Assert.That(Requests(), Has.Length.EqualTo(32));
            ValueHash256[] targets = Enumerable.Range(1000, 4064).Select(index => new Hash256(index.ToString("X64")).ValueHash256).ToArray();
            for (int start = 0; start < targets.Length; start += NewPooledTransactionHashesMessage.MaxCount)
            {
                using NewPooledTransactionHashesMessage announcement = new(targets.Skip(start)
                    .Take(NewPooledTransactionHashesMessage.MaxCount).Select(hash => hash.ToHash256()).ToArray().ToPooledList());
                HandleZeroMessage(announcement, Eth65MessageCode.NewPooledTransactionHashes);
            }
            Assert.That(Requests(), Has.Length.EqualTo(32));
            int answered = 32;
            List<ValueHash256> delivered = [];
            ISession honestSession = Substitute.For<ISession>();
            honestSession.Node.Returns(new Node(TestItem.PublicKeyB, new IPEndPoint(IPAddress.Loopback, 30304)));
            List<GetPooledTransactionsMessage66> honestRequests = [];
            honestSession.DeliverMessage(Arg.Any<P2PMessage>()).Returns(call =>
            {
                P2PMessage message = call.Arg<P2PMessage>();
                message.AddTo(_disposables);
                if (message is GetPooledTransactionsMessage66 request) honestRequests.Add(request);
                return 1;
            });
            using Eth66ProtocolHandler honest = new(honestSession, _svc,
                new NodeStatsManager(_timerFactory, LimboLogs.Instance), _syncManager,
                new RecordingBackgroundTaskScheduler(), pool, _gossipPolicy,
                new ForkInfo(_specProvider, _syncManager), LimboLogs.Instance, timeProvider: clock);
            honest.Init();
            using StatusMessage honestStatus = new() { GenesisHash = _genesisBlock.Hash, BestHash = _genesisBlock.Hash };
            Receive(honest, honestStatus, 0);
            EthereumEcdsa ecdsa = new(1);
            Transaction[] honestTransactions = Enumerable.Range(0, 256)
                .Select(index => Build.A.Transaction.WithNonce((ulong)index).WithGasPrice(2.GWei).WithGasLimit(21000)
                    .SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject).ToArray();
            MethodInfo expire = typeof(Eth66ProtocolHandler).GetMethod("ExpirePooledTransactionRequests", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int callback = 0; callback < 49; callback++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(700));
                if (releaseQuota) expire.Invoke(_handler, null);
                else foreach (ValueHash256 hash in held) _handler.HandleMessage(PooledTransactionRequestMessage.New(hash));
                if (callback == 14)
                    foreach (Transaction transaction in honestTransactions)
                    {
                        pool.RetryCache.Announced(transaction.Hash!.ValueHash256, honest);
                        Assert.That(pool.RetryCache.TryDeferRequest(transaction.Hash.ValueHash256, honest), Is.True);
                    }
                ProcessRetryTick(pool.RetryCache);
                if (callback == 14)
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(honestRequests, Has.Count.EqualTo(1));
                        Assert.That(honestRequests.SelectMany(static request => request.EthMessage.Hashes),
                            Is.EquivalentTo(honestTransactions.Select(static transaction => transaction.Hash!.ValueHash256)));
                    }
                    long accepted = pool.PendingTransactionsAdded;
                    using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(honestRequests[0].RequestId,
                        new PooledTransactionsMessage(honestTransactions.ToPooledList()));
                    Receive(honest, response, Eth66MessageCode.PooledTransactions);
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(pool.PendingTransactionsAdded - accepted, Is.EqualTo(256));
                        Assert.That(honestTransactions.All(transaction => pool.ContainsTx(transaction.Hash!, TxType.Legacy)), Is.True);
                        Assert.That(pool.RetryCache.HandlerReferences(honest), Is.EqualTo((0, 0)));
                    }
                }
                if (releaseQuota)
                {
                    GetPooledTransactionsMessage66[] requests = Requests();
                    foreach (GetPooledTransactionsMessage66 request in requests.Skip(answered))
                    {
                        Assert.That(request.EthMessage.Hashes.Count, Is.LessThanOrEqualTo(256));
                        delivered.AddRange(request.EthMessage.Hashes);
                        using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage empty = new(request.RequestId,
                            new PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
                        HandleZeroMessage(empty, Eth66MessageCode.PooledTransactions);
                    }
                    answered = requests.Length;
                }
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(delivered, releaseQuota ? Is.EquivalentTo(targets) : Is.Empty);
                Assert.That(pool.RetryCache.RawResourcesInRetryQueue, Is.Zero);
                Assert.That(pool.RetryCache.TrackedRequestsInUse, Is.Zero);
                Assert.That(pool.RetryCache.OverflowRequestsInUse, Is.Zero);
                Assert.That(pool.RetryCache.RetainedAssociationsInUse, Is.Zero);
                Assert.That(honestRequests, Has.Count.EqualTo(1));
            }
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            honestSession.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());

            GetPooledTransactionsMessage66[] Requests() => _session.ReceivedCalls()
                .SelectMany(call => call.GetArguments().OfType<GetPooledTransactionsMessage66>()).ToArray();
        }

        [Test]
        public async Task An_early_accepted_reply_requires_a_positive_completed_send(
            [Values] RequestSendOutcome outcome, [Values] bool concurrentResponse)
        {
            HandleIncomingStatusMessage();
            ManualTimeProvider clock = new();
            await using IContainer container = CreateRequestedProgressPool(clock);
            _transactionPool = container.Resolve<ITxPool>();
            TxPool.TxPool pool = (TxPool.TxPool)_transactionPool;
            _handler.Dispose();
            _handler = CreateHandler(new RecordingBackgroundTaskScheduler(), clock);
            _handler.Init();
            HandleIncomingStatusMessage();
            ValueHash256 backlog = TestItem.KeccakF.ValueHash256;
            pool.RetryCache.Announced(backlog, _handler);
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.True);
            clock.Advance(TimeSpan.FromSeconds(32));
            Transaction transaction = Build.A.Transaction.WithGasPrice(2.GWei).WithGasLimit(21000)
                .SignedAndResolved(new EthereumEcdsa(1), TestItem.PrivateKeyA).TestObject;
            ValueHash256 hash = transaction.Hash!.ValueHash256;
            pool.RetryCache.Announced(hash, _handler);
            int deliveries = 0;
            _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage66>()).Returns(call =>
            {
                deliveries++;
                long id = call.Arg<GetPooledTransactionsMessage66>().RequestId;
                if (concurrentResponse) Task.Run(Reply).GetAwaiter().GetResult();
                else Reply();
                Assert.That(pool.ContainsTx(hash.ToHash256(), TxType.Legacy), Is.True);
                return outcome switch
                {
                    RequestSendOutcome.Sent => 1,
                    RequestSendOutcome.Dropped => 0,
                    _ => throw new InvalidOperationException("local send failed")
                };

                void Reply()
                {
                    using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(id,
                        new PooledTransactionsMessage(new[] { transaction }.ToPooledList()));
                    HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                }
            });
            if (outcome == RequestSendOutcome.Throws)
                Assert.That(() => _handler.HandleMessage(PooledTransactionRequestMessage.New(hash)), Throws.TypeOf<InvalidOperationException>());
            else
                _handler.HandleMessage(PooledTransactionRequestMessage.New(hash));
            Assert.That(deliveries, Is.EqualTo(1));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.EqualTo(outcome == RequestSendOutcome.Sent));
        }

        [Test]
        public async Task Accepted_alternate_response_renews_only_the_actual_sending_identity()
        {
            HandleIncomingStatusMessage();
            ManualTimeProvider clock = new();
            await using IContainer container = CreateRequestedProgressPool(clock);
            _transactionPool = container.Resolve<ITxPool>();
            TxPool.TxPool pool = (TxPool.TxPool)_transactionPool;
            _handler.Dispose();
            _handler = CreateHandler(RunImmediatelyScheduler.Instance, clock);
            _handler.Init();
            HandleIncomingStatusMessage();
            ISession otherSession = Substitute.For<ISession>();
            otherSession.Node.Returns(new Node(TestItem.PublicKeyB, new IPEndPoint(IPAddress.Loopback, 30304)));
            List<GetPooledTransactionsMessage66> sent = [];
            otherSession.DeliverMessage(Arg.Any<P2PMessage>()).Returns(call =>
            {
                P2PMessage message = call.Arg<P2PMessage>();
                message.AddTo(_disposables);
                if (message is GetPooledTransactionsMessage66 request) sent.Add(request);
                return 1;
            });
            using Eth66ProtocolHandler other = new(otherSession, _svc,
                new NodeStatsManager(_timerFactory, LimboLogs.Instance), _syncManager,
                RunImmediatelyScheduler.Instance, _transactionPool, _gossipPolicy,
                new ForkInfo(_specProvider, _syncManager), LimboLogs.Instance, timeProvider: clock);
            other.Init();
            using StatusMessage status = new() { GenesisHash = _genesisBlock.Hash, BestHash = _genesisBlock.Hash };
            HandleOther(status, 0);
            ValueHash256 sourceBacklog = TestItem.KeccakE.ValueHash256;
            ValueHash256 senderBacklog = TestItem.KeccakF.ValueHash256;
            pool.RetryCache.Announced(sourceBacklog, _handler);
            pool.RetryCache.Announced(senderBacklog, other);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.RetryCache.TryDeferRequest(sourceBacklog, _handler), Is.True);
                Assert.That(pool.RetryCache.TryDeferRequest(senderBacklog, other), Is.True);
            }
            clock.Advance(TimeSpan.FromSeconds(28));
            Transaction transaction = Build.A.Transaction.WithGasPrice(2.GWei).WithGasLimit(21000)
                .SignedAndResolved(new EthereumEcdsa(1), TestItem.PrivateKeyA).TestObject;
            ValueHash256 hash = transaction.Hash!.ValueHash256;
            pool.RetryCache.Announced(hash, _handler);
            RequestTransaction(transaction);
            pool.RetryCache.Announced(hash, other);
            clock.Advance(TimeSpan.FromSeconds(4));
            ProcessRetryTick(pool.RetryCache);
            GetPooledTransactionsMessage66 requested = sent.Single(request => request.EthMessage.Hashes.Contains(hash));
            using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(requested.RequestId,
                new PooledTransactionsMessage(new[] { transaction }.ToPooledList()));
            HandleOther(response, Eth66MessageCode.PooledTransactions);
            Assert.That(pool.ContainsTx(hash.ToHash256(), TxType.Legacy), Is.True);
            clock.Advance(TimeSpan.FromSeconds(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.RetryCache.TryDeferRequest(sourceBacklog, _handler), Is.False);
                Assert.That(pool.RetryCache.TryDeferRequest(senderBacklog, other), Is.True);
            }

            void HandleOther<T>(T message, int code) where T : P2PMessage
            {
                using DisposableByteBuffer packet = _svc.ZeroSerialize(message).AsDisposable();
                packet.ReadByte();
                other.HandleMessage(new NettyBufferPacket(packet) { PacketType = (byte)code });
            }
        }

        [Test]
        public async Task Old_request_tokens_do_not_acquire_replacement_or_later_attempt_credit(
            [Values("generation", "attempt", "expired", "queuedExpiry")] string replacement)
        {
            HandleIncomingStatusMessage();
            ManualTimeProvider clock = new();
            await using IContainer container = CreateRequestedProgressPool(clock);
            _transactionPool = container.Resolve<ITxPool>();
            TxPool.TxPool pool = (TxPool.TxPool)_transactionPool;
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = replacement == "queuedExpiry" };
            _handler.Dispose();
            _handler = CreateHandler(scheduler, clock);
            _handler.Init();
            HandleIncomingStatusMessage();
            ValueHash256 backlog = TestItem.KeccakF.ValueHash256;
            pool.RetryCache.Announced(backlog, _handler);
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.True);
            Transaction transaction = Build.A.Transaction.WithGasPrice(2.GWei).WithGasLimit(21000)
                .SignedAndResolved(new EthereumEcdsa(1), TestItem.PrivateKeyA).TestObject;
            ValueHash256 hash = transaction.Hash!.ValueHash256;
            pool.RetryCache.Announced(hash, _handler);
            long oldId = RequestTransaction(transaction);
            double elapsed = replacement == "expired" ? 3.6 : 1;
            clock.Advance(TimeSpan.FromSeconds(elapsed));
            if (replacement != "queuedExpiry")
            {
                if (replacement != "attempt")
                {
                    pool.RetryCache.Received(hash);
                    pool.RetryCache.Announced(hash, _handler);
                }
                Assert.That(RequestTransaction(transaction), Is.Not.EqualTo(oldId));
            }
            using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(oldId,
                new PooledTransactionsMessage(new[] { transaction }.ToPooledList()));
            HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
            if (replacement == "queuedExpiry")
            {
                clock.Advance(TimeSpan.FromSeconds(2.5));
                elapsed += 2.5;
                await scheduler.Drain(CancellationToken.None);
            }
            Assert.That(pool.ContainsTx(hash.ToHash256(), TxType.Legacy), Is.True, "the old payload still undergoes normal transaction validation");
            clock.Advance(TimeSpan.FromSeconds(34 - elapsed));
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.False);
        }

        [Test]
        public async Task Only_fresh_accepted_requested_transactions_renew_source_backlog(
            [Values("accepted", "invalid", "known", "broadcast", "empty", "unmatched", "late", "cancel")] string responseKind)
        {
            HandleIncomingStatusMessage();
            ManualTimeProvider clock = new();
            await using IContainer container = CreateRequestedProgressPool(clock);
            _transactionPool = container.Resolve<ITxPool>();
            TxPool.TxPool pool = (TxPool.TxPool)_transactionPool;
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = responseKind == "cancel" };
            _handler.Dispose();
            _handler = CreateHandler(scheduler, clock);
            _handler.Init();
            HandleIncomingStatusMessage();
            ValueHash256 backlog = TestItem.KeccakF.ValueHash256;
            pool.RetryCache.Announced(backlog, _handler);
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.True);
            clock.Advance(TimeSpan.FromSeconds(32));
            Transaction transaction = Build.A.Transaction.WithGasPrice(2.GWei).WithGasLimit(responseKind == "invalid" ? 1UL : 21000UL)
                .SignedAndResolved(new EthereumEcdsa(1), TestItem.PrivateKeyA).TestObject;
            ValueHash256 hash = transaction.Hash!.ValueHash256;
            pool.RetryCache.Announced(hash, _handler);
            long requestId = RequestTransaction(transaction);
            if (responseKind == "known")
                Assert.That(pool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            if (responseKind == "late") clock.Advance(TimeSpan.FromSeconds(3.5));
            if (responseKind == "broadcast")
            {
                using TransactionsMessage broadcast = new(new[] { transaction }.ToPooledList());
                HandleZeroMessage(broadcast, Eth62MessageCode.Transactions);
            }
            else
            {
                using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                    responseKind == "unmatched" ? requestId + 1 : requestId,
                    new PooledTransactionsMessage(responseKind == "empty" ? IOwnedReadOnlyList<Transaction>.Empty
                        : new[] { transaction }.ToPooledList()));
                HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                if (responseKind == "cancel") await scheduler.Drain(new CancellationToken(true));
            }
            if (responseKind != "late") clock.Advance(TimeSpan.FromSeconds(2));
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.EqualTo(responseKind == "accepted"));
            if (responseKind == "accepted")
            {
                clock.Advance(TimeSpan.FromSeconds(34));
                Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.False);
            }
        }

        [Test]
        public async Task Pooled_response_context_has_one_owner_through_scheduling(
            [Values("complete", "cancel", "reject", "continue", "rejectContinuation", "dispose")] string outcome)
        {
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, Reject = outcome == "reject" };
            _handler.Dispose();
            _handler = CreateHandler(scheduler);
            using CancellationTokenSource cancellation = new();
            if (outcome == "continue" || outcome == "rejectContinuation")
                _transactionPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(_ =>
                {
                    cancellation.Cancel();
                    scheduler.Reject = outcome == "rejectContinuation";
                    return AcceptTxResult.Accepted;
                });
            int releases = 0;
            using PooledTransactionRequests.ResponseContext context = new(2, () => releases++);
            context.Tokens.Add(default);
            context.Tokens.Add(default);
            TransactionsMessage response = new(new ArrayPoolList<Transaction>(2)
            {
                Build.A.Transaction.WithNonce(0).SignedAndResolved().TestObject,
                Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject
            });
            MethodInfo schedule = typeof(Eth62ProtocolHandler).GetMethod("HandlePooledTransactions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            schedule.Invoke(_handler, [response, true, null, context]);
            if (outcome == "reject") Assert.That(releases, Is.EqualTo(1));
            else
            {
                Assert.That(releases, Is.Zero);
                if (outcome == "cancel") cancellation.Cancel();
                if (outcome == "dispose")
                {
                    _session.IsClosing.Returns(true);
                    _handler.Dispose();
                }
                await scheduler.RunNext(cancellation.Token);
                if (outcome == "continue")
                {
                    Assert.That(releases, Is.Zero);
                    await scheduler.Drain(CancellationToken.None);
                }
                Assert.That(releases, Is.EqualTo(1));
            }
            context.Dispose();
            Assert.That(releases, Is.EqualTo(1));
        }

        [Test]
        public void Sending_after_expiry_releases_reply_slots_without_a_timer_or_response()
        {
            ManualTimeProvider clock = new();
            RecordingBackgroundTaskScheduler scheduler = new() { QueueCapacity = 8192 };
            _handler.Dispose();
            _handler = CreateHandler(scheduler, clock);
            _handler.Init();
            HandleIncomingStatusMessage();
            for (int index = 0; index < 32; index++)
                _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
            Assert.That(_session.ReceivedCalls().Count(call => call.GetArguments().OfType<GetPooledTransactionsMessage66>().Any()), Is.EqualTo(32));
            clock.Advance(Timeouts.Eth);
            _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_session.ReceivedCalls().Count(call => call.GetArguments().OfType<GetPooledTransactionsMessage66>().Any()), Is.EqualTo(33));
                _transactionPool.DidNotReceive().TryDeferTransactionRequest(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
                _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
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
            _syncManager.FindBlockRlp(Arg.Any<Hash256>()).Returns((MemoryManager<byte>?)null);

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
        public async Task Local_pooled_reply_faults_remain_neutral_with_exact_protected_capacity([Values(0, 1, 2, 3)] int fault)
        {
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
            _handler.Dispose();
            _handler = CreateHandler(scheduler);
            _handler.Init();
            HandleIncomingStatusMessage();
            Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            Exception failure = fault switch
            {
                0 => new RlpReaderException("local transaction storage is malformed"),
                1 => new RlpLimitException("local transaction storage exceeds its read limit"),
                2 => new System.IO.InvalidDataException("local transaction storage is unavailable"),
                _ => new OperationCanceledException("local transaction storage operation was cancelled")
            };
            bool fail = true;
            _transactionPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(_ =>
                fail ? throw failure : AcceptTxResult.Accepted);
            try
            {
                for (int round = 0; round < 2; round++)
                {
                    for (int index = 0; index < 32; index++)
                    {
                        long requestId = RequestTransaction(transaction);
                        using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(requestId,
                            new PooledTransactionsMessage(new[] { transaction }.ToPooledList()));
                        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                    }
                    Assert.That(scheduler.ScheduledFulfillFuncs.Count, Is.EqualTo((round + 1) * 32));
                    _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
                    Assert.That(_session.ReceivedCalls().Count(call => call.GetArguments().OfType<GetPooledTransactionsMessage66>().Any()), Is.EqualTo((round + 1) * 32));
                    await scheduler.Drain(CancellationToken.None);
                    _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
                    _transactionPool.Received((round + 1) * 32).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
                    fail = false;
                }
            }
            finally { await scheduler.Drain(new CancellationToken(true)); }
        }

        [Test]
        public async Task Pooled_replies_must_be_an_ordered_subset_of_the_requested_hashes(
            [Values(0, 1, 2, 3, 4)] int replyShape, [Values] bool eth67)
        {
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
            _handler.Dispose();
            _handler = eth67
                ? new Nethermind.Network.P2P.Subprotocols.Eth.V67.Eth67ProtocolHandler(_session, _svc,
                    new NodeStatsManager(_timerFactory, LimboLogs.Instance), _syncManager, scheduler,
                    _transactionPool, _gossipPolicy, new ForkInfo(_specProvider, _syncManager), LimboLogs.Instance)
                : CreateHandler(scheduler);
            _handler.Init();
            HandleIncomingStatusMessage();
            Transaction first = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;
            Transaction second = Build.A.Transaction.WithNonce(2).SignedAndResolved().TestObject;
            Transaction unrequested = Build.A.Transaction.WithNonce(3).SignedAndResolved().TestObject;
            _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>()).Returns(AnnounceResult.RequestRequired);
            using NewPooledTransactionHashesMessage announcement = new(new[] { first.Hash!, second.Hash! }.ToPooledList());
            HandleZeroMessage(announcement, Eth65MessageCode.NewPooledTransactionHashes);
            long requestId = _session.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<GetPooledTransactionsMessage66>()).Last().RequestId;
            Transaction[] returned = replyShape switch
            {
                0 => [unrequested],
                1 => [first, first],
                2 => [],
                3 => [second],
                _ => [first, second]
            };
            using Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(requestId,
                new PooledTransactionsMessage(returned.ToPooledList()));
            bool rejected = replyShape < 2;
            try
            {
                if (rejected) Assert.Throws<SubprotocolException>(() => HandleZeroMessage(response, Eth66MessageCode.PooledTransactions));
                else HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                Assert.That(scheduler.ScheduledFulfillFuncs.Count, Is.EqualTo(!rejected ? 1 : 0));
                await scheduler.Drain(CancellationToken.None);
                _transactionPool.Received(rejected ? 0 : returned.Length).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
                HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                Assert.That(scheduler.SchedulingAttempts, Is.EqualTo(!rejected ? 1 : 0));
                for (int index = 0; index < 32; index++) _handler.HandleMessage(PooledTransactionRequestMessage.New(first.Hash!));
                Assert.That(_session.ReceivedCalls().Count(call => call.GetArguments().OfType<GetPooledTransactionsMessage66>().Any()), Is.EqualTo(33));
            }
            finally { await scheduler.Drain(new CancellationToken(true)); }
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

        private long RequestTransactions(params Transaction[] transactions)
        {
            _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>()).Returns(AnnounceResult.RequestRequired);
            using NewPooledTransactionHashesMessage announcement = new(transactions.Select(static tx => tx.Hash!).ToArray().ToPooledList());
            HandleZeroMessage(announcement, Eth65MessageCode.NewPooledTransactionHashes);
            return _session.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<GetPooledTransactionsMessage66>()).Last().RequestId;
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

            Assert.That(() => _handler.HandleMessage(new NettyBufferPacket(packet) { PacketType = Eth66MessageCode.PooledTransactions }), Throws.Nothing);
            _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }

        public enum BudgetMessageKind { Broadcast, CorrelatedPooled, UncorrelatedPooled }

        [Test]
        public void Transaction_budget_charges_larger_of_wire_bytes_and_count([Values(0, 1, 16, 16384)] int count, [Values(1, 65536)] int bytes)
        {
            InboundTransactionBudget budget = new(new RecordingBackgroundTaskScheduler());
            int charge = Math.Max(InboundTransactionBudget.MinimumCharge, Math.Max(bytes, count * InboundTransactionBudget.TransactionCharge));
            using InboundTransactionBudget.Reservation? reservation = budget.TryReserve(bytes, count);
            Assert.That(reservation, Is.Not.Null);
            using InboundTransactionBudget.Reservation? remaining = budget.TryReserve(InboundTransactionBudget.PeerLimit - charge);
            Assert.That(remaining, Is.Not.Null);
            using InboundTransactionBudget.Reservation? excess = budget.TryReserve(1);
            Assert.That(excess, Is.Null);
        }

        [Test]
        public void Transaction_count_charge_cannot_overflow()
        {
            InboundTransactionBudget budget = new(new RecordingBackgroundTaskScheduler());
            Assert.That(budget.TryReserve(1, int.MaxValue), Is.Null);
            using InboundTransactionBudget.Reservation? available = budget.TryReserve(InboundTransactionBudget.PeerLimit);
            Assert.That(available, Is.Not.Null);
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
                RequestTransactions(first, second), new PooledTransactionsMessage(new[] { first, second }.ToPooledList()));
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
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(start.SignalAndWait(TimeSpan.FromSeconds(30)), Is.True);
                    Assert.That(rejected, Is.Null);
                }

                for (int i = 0; i < 2000; i++)
                {
                    int charge = InboundTransactionBudget.PeerLimit / (2 + (i + worker) % 3);
                    using InboundTransactionBudget.Reservation? reservation = budget.TryReserve(charge);
                    if (reservation is null) continue;
                    int peerTotal = Interlocked.Add(ref peerBytes[peer], charge);
                    int sharedTotal = Interlocked.Add(ref sharedBytes, charge);
                    try
                    {
                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(peerTotal, Is.LessThanOrEqualTo(InboundTransactionBudget.PeerLimit));
                            Assert.That(sharedTotal, Is.LessThanOrEqualTo(InboundTransactionBudget.PeerLimit));
                        }
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
        public void Transaction_budget_rejects_charge_larger_than_remaining_capacity([Values] bool sharedLimit)
        {
            RecordingBackgroundTaskScheduler scheduler = new();
            InboundTransactionBudget budget = new(scheduler);
            using CompositeDisposable reservations = [];
            if (sharedLimit)
            {
                for (int i = 0; i < InboundTransactionBudget.GlobalLimit / InboundTransactionBudget.PeerLimit - 1; i++)
                    new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit)!.AddTo(reservations);
            }
            InboundTransactionBudget lastPeer = sharedLimit ? new(scheduler) : budget;
            using InboundTransactionBudget.Reservation? held = lastPeer.TryReserve(InboundTransactionBudget.PeerLimit - 1);
            Assert.That(held, Is.Not.Null);
            using InboundTransactionBudget.Reservation? excess = budget.TryReserve(InboundTransactionBudget.MinimumCharge);
            Assert.That(excess, Is.Null);
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
        public void Serves_stored_and_decoded_receipts_with_blooms_as_decoded_ones()
        {
            (Block storedBlock, TxReceipt[] storedReceipts) = ReceiptsServingTestHelper.CreateStoredBlock(_specProvider, 3);
            TxReceipt[][] blocks = [storedReceipts, [Build.A.Receipt.WithAllFieldsFilled.TestObject]];
            Hash256[] hashes = [storedBlock.Hash!, TestItem.KeccakB];
            for (int i = 0; i < hashes.Length; i++)
            {
                _syncManager.GetReceipts(hashes[i]).Returns(blocks[i]);
            }

            _syncManager.ServeReceipts(_specProvider, storedBlock);
            Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessageSerializer serializer =
                new(new Network.P2P.Subprotocols.Eth.V63.Messages.ReceiptsMessageSerializer(_specProvider));
            byte[]? response = null;
            _session.When(session => session.DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage>()))
                .Do(call => response = serializer.SerializeToArray((Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage)call[0]));
            HandleIncomingStatusMessage();

            using GetReceiptsMessage msg63 = new(hashes.ToPooledList());
            using GetReceiptsMessage66 msg66 = new(1111, msg63);
            HandleZeroMessage(msg66, Eth66MessageCode.GetReceipts);

            using Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage decoded = new(1111, new ReceiptsMessage(blocks.ToPooledList()));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(response, Is.EqualTo(serializer.SerializeToArray(decoded)));
                _syncManager.Received(1).TryAppendReceipts(storedBlock.Hash!, Arg.Any<IRlpDecoder<TxReceipt>>(), true, Arg.Any<ArrayPoolList<byte>>());
            }
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

        [Test]
        public async Task Should_discard_expired_undecodable_response_before_decoding(
            [Values(Eth66MessageCode.BlockHeaders, Eth66MessageCode.BlockBodies, Eth66MessageCode.Receipts, Eth66MessageCode.NodeData)] int messageCode)
        {
            HandleIncomingStatusMessage();
            Task responseTask;
            long requestId;

            switch (messageCode)
            {
                case Eth66MessageCode.BlockHeaders:
                    Eth66Message<GetBlockHeadersMessage>? headersRequest = null;
                    _session.When(session => session.DeliverMessage(Arg.Any<Eth66Message<GetBlockHeadersMessage>>()))
                        .Do(callInfo => headersRequest = (Eth66Message<GetBlockHeadersMessage>)callInfo[0]);
                    responseTask = ((ISyncPeer)_handler).GetBlockHeaders(1, 1, 1, CancellationToken.None);
                    Assert.That(SpinWait.SpinUntil(() => headersRequest is not null, 1000), Is.True);
                    requestId = headersRequest!.RequestId;
                    Expire(GetDictionary<GetBlockHeadersMessage66, IOwnedReadOnlyList<BlockHeader>>(_handler, "_headersRequests66"), requestId);
                    break;
                case Eth66MessageCode.BlockBodies:
                    Eth66Message<GetBlockBodiesMessage>? bodiesRequest = null;
                    _session.When(session => session.DeliverMessage(Arg.Any<Eth66Message<GetBlockBodiesMessage>>()))
                        .Do(callInfo => bodiesRequest = (Eth66Message<GetBlockBodiesMessage>)callInfo[0]);
                    responseTask = ((ISyncPeer)_handler).GetBlockBodies([Keccak.Zero], CancellationToken.None);
                    Assert.That(SpinWait.SpinUntil(() => bodiesRequest is not null, 1000), Is.True);
                    requestId = bodiesRequest!.RequestId;
                    Expire(GetDictionary<GetBlockBodiesMessage66, (OwnedBlockBodies, long)>(_handler, "_bodiesRequests66"), requestId);
                    break;
                case Eth66MessageCode.Receipts:
                    Eth66Message<GetReceiptsMessage>? receiptsRequest = null;
                    _session.When(session => session.DeliverMessage(Arg.Any<Eth66Message<GetReceiptsMessage>>()))
                        .Do(callInfo => receiptsRequest = (Eth66Message<GetReceiptsMessage>)callInfo[0]);
                    responseTask = ((ISyncPeer)_handler).GetReceipts([Keccak.Zero], CancellationToken.None);
                    Assert.That(SpinWait.SpinUntil(() => receiptsRequest is not null, 1000), Is.True);
                    requestId = receiptsRequest!.RequestId;
                    Expire(GetDictionary<GetReceiptsMessage66, (IOwnedReadOnlyList<TxReceipt[]>, long)>(_handler, "_receiptsRequests66"), requestId);
                    break;
                case Eth66MessageCode.NodeData:
                    Eth66Message<GetNodeDataMessage>? nodeDataRequest = null;
                    _session.When(session => session.DeliverMessage(Arg.Any<Eth66Message<GetNodeDataMessage>>()))
                        .Do(callInfo => nodeDataRequest = (Eth66Message<GetNodeDataMessage>)callInfo[0]);
                    responseTask = ((ISyncPeer)_handler).GetNodeData([Keccak.Zero], CancellationToken.None);
                    Assert.That(SpinWait.SpinUntil(() => nodeDataRequest is not null, 1000), Is.True);
                    requestId = nodeDataRequest!.RequestId;
                    Expire(GetDictionary<GetNodeDataMessage66, IByteArrayList>(_handler, "_nodeDataRequests66"), requestId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(messageCode), messageCode, null);
            }

            using PooledBufferLeakDetector detector = new();
            HandleUndecodableResponse(messageCode, requestId, detector);
            Assert.That(() => HandleUndecodableResponse(messageCode, requestId, detector), Throws.TypeOf<SubprotocolException>());
            Assert.That(() => HandleUndecodableResponse(messageCode, requestId + 1, detector), Throws.TypeOf<SubprotocolException>());

            Exception? responseException = null;
            try
            {
                await responseTask;
            }
            catch (Exception exception)
            {
                responseException = exception;
            }

            Assert.That(responseException, Is.TypeOf<TimeoutException>());
        }


        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(256, 1)]
        [TestCase(257, 2)]
        [TestCase(512, 2)]
        [TestCase(1000, 4)]
        [TestCase(10000, 40)]
        public async Task should_request_in_GetPooledTransactionsMessage_up_to_256_txs(int numberOfTransactions, int expectedNumberOfMessages)
        {
            const int maxNumberOfTxsInOneMsg = 256;

            HandleIncomingStatusMessage();
            _handler.Dispose();
            ManualTimeProvider requestClock = new();
            _handler = CreateHandler(new RecordingBackgroundTaskScheduler { QueueCapacity = 8192 }, requestClock);
            _handler.Init();
            ManualTimeProvider clock = new();
            await using RetryCache<PooledTransactionRequestMessage, ValueHash256> retry = CreateRetryCache(clock);
            _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
                .Returns(call =>
                {
                    ValueHash256 hash = call.ArgAt<ValueHash256>(0);
                    return retry.Announced(in hash, call.ArgAt<IMessageHandler<PooledTransactionRequestMessage>>(1));
                });
            _transactionPool.TryDeferTransactionRequest(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
                .Returns(call => TryDeferRequest(retry, call.ArgAt<ValueHash256>(0),
                    call.ArgAt<IMessageHandler<PooledTransactionRequestMessage>>(1)));

            using ArrayPoolList<Hash256> hashes = new(numberOfTransactions);

            for (int i = 0; i < numberOfTransactions; i++)
            {
                hashes.Add(new Hash256(i.ToString("X64")));
            }

            using NewPooledTransactionHashesMessage hashesMsg = new(hashes);
            HandleIncomingStatusMessage();
            HandleZeroMessage(hashesMsg, Eth65MessageCode.NewPooledTransactionHashes);

            Assert.That(SentRequests().Length, Is.EqualTo(Math.Min(expectedNumberOfMessages, 32)));
            int answered = 0;
            for (int round = 0; round <= (numberOfTransactions + RetryWorkBatchSize - 1) / RetryWorkBatchSize; round++)
            {
                GetPooledTransactionsMessage66[] sent = SentRequests();
                foreach (GetPooledTransactionsMessage66 request in sent.Skip(answered))
                {
                    using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                        request.RequestId, new PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
                    HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                }
                answered = sent.Length;
                if (answered == expectedNumberOfMessages) break;
                clock.Advance(TimeSpan.FromMilliseconds(500));
                requestClock.Advance(TimeSpan.FromMilliseconds(500));
                ProcessRetryTick(retry);
                Assert.That(SentRequests().Length, Is.GreaterThan(answered));
            }
            using (Assert.EnterMultipleScope())
            {
                _session.Received(expectedNumberOfMessages)
                    .DeliverMessage(Arg.Is<GetPooledTransactionsMessage66>(m =>
                        m.EthMessage.Hashes.Count == maxNumberOfTxsInOneMsg ||
                        m.EthMessage.Hashes.Count == numberOfTransactions % maxNumberOfTxsInOneMsg));
                Assert.That(SentRequests().SelectMany(static request => request.EthMessage.Hashes),
                    Is.EquivalentTo(hashes.Select(static hash => hash.ValueHash256)));
                _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }

            GetPooledTransactionsMessage66[] SentRequests() => _session.ReceivedCalls()
                .SelectMany(static call => call.GetArguments().OfType<GetPooledTransactionsMessage66>()).ToArray();

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

        private void HandleZeroMessage<T>(T msg, int messageCode) where T : P2PMessage
        {
            using DisposableByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg).AsDisposable();
            getBlockHeadersPacket.ReadByte();
            _handler.HandleMessage(new NettyBufferPacket(getBlockHeadersPacket) { PacketType = (byte)messageCode });
        }

        private static MessageDictionary<TMsg, TData> GetDictionary<TMsg, TData>(Eth66ProtocolHandler handler, string fieldName)
            where TMsg : P2PMessage, IEth66Message
        {
            FieldInfo field = typeof(Eth66ProtocolHandler).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (MessageDictionary<TMsg, TData>)field.GetValue(handler)!;
        }

        private static void Expire<TMsg, TData>(MessageDictionary<TMsg, TData> dictionary, long requestId)
            where TMsg : P2PMessage, IEth66Message
        {
            FieldInfo thresholdField = typeof(MessageDictionary<TMsg, TData>).GetField("_oldRequestThreshold", BindingFlags.Instance | BindingFlags.NonPublic)!;
            thresholdField.SetValue(dictionary, TimeSpan.Zero);
            Assert.That(SpinWait.SpinUntil(() => dictionary.TryExpireRequest(requestId), TimeSpan.FromSeconds(1)), Is.True);
        }

        private void HandleUndecodableResponse(int messageCode, long requestId, PooledBufferLeakDetector detector)
        {
            using DisposableByteBuffer source = UndecodableResponse.Create(requestId).AsDisposable();
            using DisposableByteBuffer content = detector.Allocator.Buffer(source.ReadableBytes).AsDisposable();
            content.WriteBytes(source, source.ReaderIndex, source.ReadableBytes);
            _handler.HandleMessage(new NettyBufferPacket(content) { PacketType = (byte)messageCode });
        }

        private void HandleIncomingStatusMessage()
        {
            using StatusMessage statusMsg = new();
            statusMsg.GenesisHash = _genesisBlock.Hash;
            statusMsg.BestHash = _genesisBlock.Hash;

            using DisposableByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg).AsDisposable();
            statusPacket.ReadByte();
            _handler.HandleMessage(new NettyBufferPacket(statusPacket) { PacketType = 0 });
        }

        [Test]
        public async Task Pooled_reply_expiry_is_neutral_and_returns_capacity([Values(-1, 0, 1)] int deadlineTicks)
        {
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
            _handler.Dispose();
            ManualTimeProvider clock = new();
            _handler = CreateHandler(scheduler, clock);
            _handler.Init();
            HandleIncomingStatusMessage();
            Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            try
            {
                for (int i = 0; i < 32; i++) _handler.HandleMessage(PooledTransactionRequestMessage.New(transaction.Hash!));
                GetPooledTransactionsMessage66[] initial = SentRequests();
                Assert.That(initial, Has.Length.EqualTo(32));
                clock.Advance(Timeouts.Eth + TimeSpan.FromTicks(deadlineTicks));
                using (Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                    initial[0].RequestId, new PooledTransactionsMessage(new ArrayPoolList<Transaction>(1) { transaction })))
                {
                    HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                    HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                }
                Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(deadlineTicks < 0 ? 1 : 0));
                await scheduler.Drain(CancellationToken.None);
                _transactionPool.Received(deadlineTicks < 0 ? 1 : 0).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
                _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
                Assert.That(SentRequests(), Has.Length.EqualTo(33), "expiry or a completed claim must release protected reply capacity");
                _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            finally { await scheduler.Drain(new CancellationToken(true)); }

            GetPooledTransactionsMessage66[] SentRequests() => _session.ReceivedCalls()
                .SelectMany(static call => call.GetArguments().OfType<GetPooledTransactionsMessage66>()).ToArray();
        }

        [Test]
        public async Task First_pooled_send_cannot_publish_a_timer_after_disposal([Values] bool closing)
        {
            HandleIncomingStatusMessage();
            _handler.Dispose();
            ManualTimeProvider clock = new();
            RecordingBackgroundTaskScheduler scheduler = new() { QueueCapacity = 8192 };
            _handler = CreateHandler(scheduler, clock);
            _handler.Init();
            HandleIncomingStatusMessage();
            await VerifyFirstPooledSendCannotOutliveDisposal(_handler, _session, clock,
                () => _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA)), closing,
                freshClock =>
                {
                    _handler = CreateHandler(scheduler, freshClock);
                    _handler.Init();
                    HandleIncomingStatusMessage();
                    return _handler;
                }, message => _handler.HandleMessage(message));
        }

        [Test]
        public async Task Zero_send_retries_original_tracked_and_overflow_hashes([Values(1, 4097)] int hashes)
        {
            ManualTimeProvider requestClock = new();
            HandleIncomingStatusMessage();
            _handler.Dispose();
            _handler = CreateHandler(RunImmediatelyScheduler.Instance, requestClock);
            _handler.Init();
            HandleIncomingStatusMessage();
            await VerifyZeroSendRetriesOriginalHashes(_handler, _session, _transactionPool, requestClock, hashes,
                values =>
                {
                    using NewPooledTransactionHashesMessage announcement = new(values);
                    HandleZeroMessage(announcement, Eth65MessageCode.NewPooledTransactionHashes);
                }, id =>
                {
                    using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                        id!.Value, new PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
                    HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                });
        }

        public enum RequestSendOutcome { Sent, Dropped, Throws }

        [Test]
        public async Task Failed_modern_sends_release_correlations_and_do_not_admit_responses([Values] RequestSendOutcome outcome)
        {
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
            _handler.Dispose();
            _handler = CreateHandler(scheduler, new ManualTimeProvider());
            _handler.Init();
            HandleIncomingStatusMessage();
            bool sendSucceeds = outcome == RequestSendOutcome.Sent;
            _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage66>()).Returns(call =>
            {
                if (!sendSucceeds)
                {
                    if (outcome == RequestSendOutcome.Throws) throw new InvalidOperationException("local send failed");
                    return 0;
                }
                using DisposableByteBuffer encoded = _svc.ZeroSerialize(call.Arg<GetPooledTransactionsMessage66>()).AsDisposable();
                return encoded.ReadableBytes;
            });
            try
            {
                for (int i = 0; i < 32; i++)
                {
                    if (outcome == RequestSendOutcome.Throws)
                        Assert.That(() => _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA)),
                            Throws.TypeOf<InvalidOperationException>());
                    else
                        _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
                }
                GetPooledTransactionsMessage66[] attempts = SentRequests();
                Assert.That(attempts, Has.Length.EqualTo(32));
                using (Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                    attempts[0].RequestId, new PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty)))
                {
                    HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                }
                Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(outcome == RequestSendOutcome.Sent ? 1 : 0),
                    "a failed send grants neither a pending correlation nor queued processing");
                await scheduler.Drain(CancellationToken.None);
                sendSucceeds = true;
                _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
                GetPooledTransactionsMessage66[] sent = SentRequests();
                Assert.That(sent, Has.Length.EqualTo(33), "failed sends must release all correlation and reply capacity");
                using (Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage response = new(
                    sent[^1].RequestId, new PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty)))
                {
                    HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                    HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                }
                Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(outcome == RequestSendOutcome.Sent ? 2 : 1));
                await scheduler.Drain(CancellationToken.None);
                _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            finally { await scheduler.Drain(new CancellationToken(true)); }

            GetPooledTransactionsMessage66[] SentRequests() => _session.ReceivedCalls()
                .SelectMany(static call => call.GetArguments().OfType<GetPooledTransactionsMessage66>()).ToArray();
        }

        internal sealed class RecordingBackgroundTaskScheduler : IBackgroundTaskScheduler
        {
            public int? QueueCapacity { get; init; }
            public bool Defer { get; init; }
            public bool Reject { get; set; }
            private readonly Queue<Func<CancellationToken, Task>> _pending = new();

            public Task RunNext(CancellationToken cancellationToken) => _pending.Dequeue()(cancellationToken);

            public async Task Drain(CancellationToken cancellationToken)
            {
                while (_pending.TryDequeue(out Func<CancellationToken, Task>? pending))
                    await pending(cancellationToken);
            }

            public List<Delegate> ScheduledFulfillFuncs { get; } = [];
            public int SchedulingAttempts { get; private set; }
            public List<bool> ScheduledRequestsHaveDelegateFields { get; } = [];

            public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
                where TReq : notnull, IBackgroundTaskRequest<TReq>
            {
                SchedulingAttempts++;
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
