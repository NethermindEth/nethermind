// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
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
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.PooledTransactionRetryCache;
using RecordingBackgroundTaskScheduler = Nethermind.Network.Test.P2P.Subprotocols.Eth.V66.Eth66ProtocolHandlerTests.RecordingBackgroundTaskScheduler;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V65
{
    [TestFixture, Parallelizable(ParallelScope.Self)]
    public class Eth65ProtocolHandlerTests
    {
        [Test]
        public void Legacy_replies_submit_every_transaction_without_expanding_oversized_provenance([Values(256, 16384)] int count)
        {
            HandleIncomingStatusMessage();
            RecreateHandler(new RecordingBackgroundTaskScheduler { QueueCapacity = 8192 });
            HandleIncomingStatusMessage();
            Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            for (int request = 0; request < 32; request++)
            {
                ValueHash256[] hashes = Enumerable.Range(request * 256 + 1, 256)
                    .Select(static index => new Hash256(index.ToString("X64")).ValueHash256).ToArray();
                if (request == 0) hashes[0] = transaction.Hash!.ValueHash256;
                _handler.HandleMessages(hashes);
            }
            _session.Received(32).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
            _transactionPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);
            using PooledTransactionsMessage response = new(Enumerable.Repeat(transaction, count).ToArray().ToPooledList());
            HandleZeroMessage(response, Eth65MessageCode.PooledTransactions);
            _transactionPool.Received(count).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            _handler.HandleMessages([TestItem.KeccakF.ValueHash256]);
            // An oversized reply matches no request, so it is handled as unsolicited and frees no request slot.
            _session.Received(count <= 256 ? 33 : 32).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
        }

        [Test]
        public async Task An_early_legacy_accepted_reply_requires_a_positive_completed_send(
            [Values] SendOutcome outcome, [Values] bool concurrentResponse)
        {
            HandleIncomingStatusMessage();
            ManualTimeProvider clock = new();
            await using IContainer container = CreateRequestedProgressPool(clock);
            _transactionPool = container.Resolve<ITxPool>();
            TxPool.TxPool pool = (TxPool.TxPool)_transactionPool;
            RecreateHandler(new RecordingBackgroundTaskScheduler(), clock);
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
            _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(_ =>
            {
                deliveries++;
                if (concurrentResponse) Task.Run(Reply).GetAwaiter().GetResult();
                else Reply();
                Assert.That(pool.ContainsTx(hash.ToHash256(), TxType.Legacy), Is.True);
                return outcome switch
                {
                    SendOutcome.Sent => 1,
                    SendOutcome.Dropped => 0,
                    _ => throw new InvalidOperationException("local send failed")
                };

                void Reply()
                {
                    using PooledTransactionsMessage response = new(new ArrayPoolList<Transaction>(1) { transaction });
                    HandleZeroMessage(response, Eth65MessageCode.PooledTransactions);
                }
            });
            if (outcome == SendOutcome.Throws)
                Assert.That(() => _handler.HandleMessage(PooledTransactionRequestMessage.New(hash)), Throws.TypeOf<InvalidOperationException>());
            else
                _handler.HandleMessage(PooledTransactionRequestMessage.New(hash));
            Assert.That(deliveries, Is.EqualTo(1));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.EqualTo(outcome == SendOutcome.Sent));
        }

        /// <remarks>A completed request followed by one same-hash replacement within ten seconds suppresses renewal. The late transaction still receives normal validation and acceptance.</remarks>
        [Test]
        public async Task accepted_residual_Completed_legacy_request_does_not_credit_a_same_hash_replacement([Values] bool rejectedFirst)
        {
            HandleIncomingStatusMessage();
            ManualTimeProvider clock = new();
            await using IContainer container = CreateRequestedProgressPool(clock);
            _transactionPool = container.Resolve<ITxPool>();
            TxPool.TxPool pool = (TxPool.TxPool)_transactionPool;
            RecreateHandler(new RecordingBackgroundTaskScheduler(), clock);
            HandleIncomingStatusMessage();
            ValueHash256 backlog = TestItem.KeccakF.ValueHash256;
            pool.RetryCache.Announced(backlog, _handler);
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.True);
            Transaction transaction = Build.A.Transaction.WithGasPrice(2.GWei).WithGasLimit(21000)
                .SignedAndResolved(new EthereumEcdsa(1), TestItem.PrivateKeyB).TestObject;
            ValueHash256 hash = transaction.Hash!.ValueHash256;
            pool.RetryCache.Announced(hash, _handler);
            _handler.HandleMessage(PooledTransactionRequestMessage.New(transaction.Hash));
            using (PooledTransactionsMessage first = new(rejectedFirst
                ? new ArrayPoolList<Transaction>(1) { transaction }
                : IOwnedReadOnlyList<Transaction>.Empty))
                HandleZeroMessage(first, Eth65MessageCode.PooledTransactions);
            Assert.That(pool.ContainsTx(hash.ToHash256(), TxType.Legacy), Is.False);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            clock.Advance(TimeSpan.FromSeconds(1));
            container.Resolve<TestReadOnlyStateProvider>().CreateAccount(TestItem.AddressB, UInt256.MaxValue);
            IBlockTree blockTree = container.Resolve<IBlockTree>();
            Block fundedHead = Build.A.Block.WithParent(blockTree.Head!).WithGasLimit(30_000_000)
                .WithBaseFeePerGas(0).WithTimestamp(blockTree.Head!.Timestamp + 1).TestObject;
            TaskCompletionSource<Block> headChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnHeadChanged(object? sender, Block block)
            {
                if (block.Hash == fundedHead.Hash) headChanged.TrySetResult(block);
            }
            pool.TxPoolHeadChanged += OnHeadChanged;
            try
            {
                BlockTreeBuilder.AddBlock(blockTree, fundedHead);
                await headChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { pool.TxPoolHeadChanged -= OnHeadChanged; }
            pool.RetryCache.Received(hash);
            pool.RetryCache.Announced(hash, _handler);
            _handler.HandleMessage(PooledTransactionRequestMessage.New(transaction.Hash));
            _session.Received(2).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(request => request.Hashes.Contains(hash)));
            using PooledTransactionsMessage late = new(new ArrayPoolList<Transaction>(1) { transaction });
            HandleZeroMessage(late, Eth65MessageCode.PooledTransactions);
            Assert.That(pool.ContainsTx(hash.ToHash256(), TxType.Legacy), Is.True);
            clock.Advance(TimeSpan.FromSeconds(33));
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.False);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }

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
            _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(call =>
            {
                using DisposableByteBuffer encoded = _svc.ZeroSerialize(call.Arg<GetPooledTransactionsMessage>()).AsDisposable();
                return encoded.ReadableBytes;
            });
            _transactionPool = Substitute.For<ITxPool>();
            _specProvider = Substitute.For<ISpecProvider>();
            _genesisBlock = Build.A.Block.Genesis.TestObject;
            _syncManager.Head.Returns(_genesisBlock.Header);
            _syncManager.Genesis.Returns(_genesisBlock.Header);
            _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
            _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
            _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
            RecreateHandler(RunImmediatelyScheduler.Instance);
        }

        private void RecreateHandler(IBackgroundTaskScheduler scheduler, TimeProvider? timeProvider = null)
        {
            _handler?.Dispose();
            _handler = new Eth65ProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance),
                _syncManager,
                scheduler,
                _transactionPool,
                Policy.FullGossip,
                new ForkInfo(_specProvider, _syncManager),
                LimboLogs.Instance,
                _txGossipPolicy, timeProvider);
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
        public void Sending_after_expiry_releases_reply_slots_without_a_timer_or_response()
        {
            ManualTimeProvider clock = new();
            RecordingBackgroundTaskScheduler scheduler = new() { QueueCapacity = 8192 };
            HandleIncomingStatusMessage();
            RecreateHandler(scheduler, clock);
            HandleIncomingStatusMessage();
            for (int index = 0; index < 32; index++)
                _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
            Assert.That(_session.ReceivedCalls().Count(call => call.GetArguments().OfType<GetPooledTransactionsMessage>().Any()), Is.EqualTo(32));
            clock.Advance(Timeouts.Eth);
            _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_session.ReceivedCalls().Count(call => call.GetArguments().OfType<GetPooledTransactionsMessage>().Any()), Is.EqualTo(33));
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
                Assert.That(_handler.Name, Is.EqualTo("eth65"));
                Assert.That(_handler.ProtocolVersion, Is.EqualTo(65));
                Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(17));
                Assert.That(_handler.IncludeInTxPool, Is.True);
                Assert.That(_handler.ClientId, Is.EqualTo(_session.Node?.ClientId));
                Assert.That(_handler.HeadHash, Is.Null);
                Assert.That(_handler.HeadNumber, Is.EqualTo(0));
            }
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
            _transactionPool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>())
                .Returns(x =>
                {
                    x[1] = tx;
                    return true;
                });
            using GetPooledTransactionsMessage request = new(TestItem.Keccaks.Select(static h => h.ValueHash256).ToArray().ToPooledList());
            using PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);
            Assert.That(response.Transactions.Count, Is.EqualTo(numberOfTxsInOneMsg));
        }

        [Test]
        public async Task should_send_single_requested_PooledTransaction_even_if_exceed_MaxPacketSize([Values(0, 1, 2, 32, 4096, 100000, 102400, 222222)] int dataSize)
        {
            Transaction tx = Build.A.Transaction.WithData(new byte[dataSize]).SignedAndResolved().TestObject;
            int sizeOfOneTx = tx.GetLength();
            int numberOfTxsInOneMsg = Math.Min(Math.Max(TransactionsMessage.MaxPacketSize / sizeOfOneTx, 1), 256);
            _transactionPool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>())
                .Returns(x =>
                {
                    x[1] = tx;
                    return true;
                });
            Hash256[] hashes = GenerateHashes(2048);
            using GetPooledTransactionsMessage request = new(hashes.Select(static h => h.ValueHash256).ToArray().ToPooledList());
            using PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);
            Assert.That(response.Transactions.Count, Is.EqualTo(numberOfTxsInOneMsg));
        }

        [Test]
        public async Task should_serve_at_most_256_unique_pooled_transaction_hashes_per_request()
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
            _transactionPool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>())
                .Returns(x =>
                {
                    x[1] = tx;
                    return true;
                });

            Hash256[] uniqueHashes = GenerateHashes(300);
            Hash256[] hashes = new Hash256[uniqueHashes.Length * 2];
            for (int i = 0; i < uniqueHashes.Length; i++)
            {
                hashes[i * 2] = uniqueHashes[i];
                hashes[i * 2 + 1] = uniqueHashes[i];
            }

            using GetPooledTransactionsMessage request = new(hashes.Select(static h => h.ValueHash256).ToArray().ToPooledList());

            using PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

            Assert.That(response.Transactions.Count, Is.EqualTo(256));
            _transactionPool.Received(256).TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>());
        }

        [Test]
        public async Task should_not_retain_response_hashes_between_requests([Values] bool cancelFirst)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
            _transactionPool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>())
                .Returns(x => { x[1] = tx; return true; });
            using GetPooledTransactionsMessage request = new(new[] { TestItem.KeccakA, TestItem.KeccakA }.Select(static h => h.ValueHash256).ToArray().ToPooledList());
            using (PooledTransactionsMessage first = await _handler.FulfillPooledTransactionsRequest(
                request, new CancellationToken(cancelFirst)))
            {
                Assert.That(first.Transactions.Count, Is.EqualTo(cancelFirst ? 0 : 1));
            }

            using PooledTransactionsMessage second = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);
            Assert.That(second.Transactions.Count, Is.EqualTo(1));
            Assert.That(second.Transactions[0], Is.SameAs(tx));
        }

        [Test]
        public async Task should_find_first_available_transaction_after_soft_request_limit()
        {
            Hash256[] hashes = GenerateHashes(257);
            Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
            _transactionPool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>())
                .Returns(x =>
                {
                    bool found = x.ArgAt<ValueHash256>(0) == hashes[^1];
                    x[1] = found ? tx : null;
                    return found;
                });

            using GetPooledTransactionsMessage request = new(hashes.Select(static h => h.ValueHash256).ToArray().ToPooledList());
            using PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

            Assert.That(response.Transactions, Has.Count.EqualTo(1));
            Assert.That(response.Transactions[0], Is.SameAs(tx));
            _transactionPool.Received(257).TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>());
        }

        [Test]
        public void should_handle_NewPooledTransactionHashesMessage([Values(true, false)] bool canGossipTransactions)
        {
            _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(canGossipTransactions);
            _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(call =>
            {
                using DisposableByteBuffer encoded = _svc.ZeroSerialize(call.Arg<GetPooledTransactionsMessage>()).AsDisposable();
                return encoded.ReadableBytes;
            });
            using NewPooledTransactionHashesMessage msg = new(new[] { TestItem.KeccakA, TestItem.KeccakB }.ToPooledList());

            HandleIncomingStatusMessage();
            HandleZeroMessage(msg, Eth65MessageCode.NewPooledTransactionHashes);

            _session.Received(canGossipTransactions ? 1 : 0).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
            Assert.That(_handler.RequestedPooledTransactionHashes, Is.EqualTo(canGossipTransactions ? 2 : 0));
        }

        [Test]
        public async Task First_pooled_send_cannot_publish_a_timer_after_disposal([Values] bool closing)
        {
            HandleIncomingStatusMessage();
            ManualTimeProvider clock = new();
            RecordingBackgroundTaskScheduler scheduler = new() { QueueCapacity = 8192 };
            RecreateHandler(scheduler, clock);
            HandleIncomingStatusMessage();
            await PooledTransactionRetryCache.VerifyFirstPooledSendCannotOutliveDisposal(_handler, _session, clock,
                () => _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA)), closing,
                freshClock =>
                {
                    RecreateHandler(scheduler, freshClock);
                    HandleIncomingStatusMessage();
                    return _handler;
                }, message => _handler.HandleMessage(message));
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
                Arg.Any<ValueHash256>(),
                Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
        }

        private void HandleZeroMessage<T>(T msg, int messageCode) where T : P2PMessage
        {
            using DisposableByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg).AsDisposable();
            getBlockHeadersPacket.ReadByte();
            _handler.HandleMessage(new NettyBufferPacket(getBlockHeadersPacket) { PacketType = (byte)messageCode });
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

        private static Hash256[] GenerateHashes(int count)
        {
            Hash256[] hashes = new Hash256[count];
            for (int i = 0; i < hashes.Length; i++)
            {
                hashes[i] = new Hash256(i.ToString("X64"));
            }

            return hashes;
        }

        private void HandleIncomingStatusMessage()
        {
            using StatusMessage statusMsg = new() { GenesisHash = _genesisBlock.Hash, BestHash = _genesisBlock.Hash };

            using DisposableByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg).AsDisposable();
            statusPacket.ReadByte();
            _handler.HandleMessage(new NettyBufferPacket(statusPacket) { PacketType = 0 });
        }

        [Test]
        public async Task Zero_send_retries_original_tracked_and_overflow_hashes([Values(1, 4097)] int hashes)
        {
            ManualTimeProvider requestClock = new();
            HandleIncomingStatusMessage();
            RecreateHandler(RunImmediatelyScheduler.Instance, requestClock);
            HandleIncomingStatusMessage();
            await Nethermind.Network.Test.P2P.Subprotocols.Eth.PooledTransactionRetryCache.VerifyZeroSendRetriesOriginalHashes(
                _handler, _session, _transactionPool, requestClock, hashes,
                values =>
                {
                    using NewPooledTransactionHashesMessage announcement = new(values);
                    HandleZeroMessage(announcement, Eth65MessageCode.NewPooledTransactionHashes);
                }, _ =>
                {
                    using PooledTransactionsMessage response = new(IOwnedReadOnlyList<Transaction>.Empty);
                    HandleZeroMessage(response, Eth65MessageCode.PooledTransactions);
                });
        }

        public enum SendOutcome { Sent, Dropped, Throws }

        [Test]
        public async Task Only_a_sent_request_grants_one_legacy_response_credit([Values] SendOutcome outcome, [Values] bool empty)
        {
            RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
            try
            {
                RecreateHandler(scheduler);
                HandleIncomingStatusMessage();
                _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(call =>
                {
                    using DisposableByteBuffer encoded = _svc.ZeroSerialize(call.Arg<GetPooledTransactionsMessage>()).AsDisposable();
                    return outcome switch
                    {
                        SendOutcome.Sent => encoded.ReadableBytes,
                        SendOutcome.Dropped => 0,
                        _ => throw new InvalidOperationException("local send failed")
                    };
                });
                for (int i = 0; i < 448; i++)
                {
                    using TransactionsMessage broadcast = new(IOwnedReadOnlyList<Transaction>.Empty);
                    HandleZeroMessage(broadcast, Eth62MessageCode.Transactions);
                }
                Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
                if (outcome == SendOutcome.Throws)
                    Assert.That(() => _handler.HandleMessage(PooledTransactionRequestMessage.New(tx.Hash!)), Throws.TypeOf<InvalidOperationException>());
                else
                    _handler.HandleMessage(PooledTransactionRequestMessage.New(tx.Hash!));
                Assert.That(_handler.RequestedPooledTransactionHashes, Is.EqualTo(outcome == SendOutcome.Sent ? 1 : 0));
                for (int i = 0; i < 2; i++)
                {
                    using PooledTransactionsMessage response = new(empty
                        ? IOwnedReadOnlyList<Transaction>.Empty : new ArrayPoolList<Transaction>(1) { tx });
                    HandleZeroMessage(response, Eth65MessageCode.PooledTransactions);
                    Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(outcome == SendOutcome.Sent ? 449 : 448));
                }
                await scheduler.Drain(CancellationToken.None);
                _transactionPool.Received(outcome == SendOutcome.Sent && !empty ? 1 : 0).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
                _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            finally
            {
                await scheduler.Drain(new CancellationToken(true));
            }
        }
    }
}
