// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Buffers.Binary;
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
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Eth65GetPooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.GetPooledTransactionsMessage;
using Eth65PooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;
using Eth65PooledTransactionsMessageSerializer = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessageSerializer;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.PooledTransactionRetryCache;
using RecordingBackgroundTaskScheduler = Nethermind.Network.Test.P2P.Subprotocols.Eth.V66.Eth66ProtocolHandlerTests.RecordingBackgroundTaskScheduler;
using GetPooledTransactionsMessage65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.GetPooledTransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V68;

public class Eth68ProtocolHandlerTests
{
    [Test]
    public void Early_pooled_shape_rejection_releases_the_owned_response_context()
    {
        HandleIncomingStatusMessage();
        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
        using NewPooledTransactionHashesMessage68 announcement = new(
            new ArrayPoolList<byte>(1) { (byte)tx.Type },
            new ArrayPoolList<int>(1) { tx.GetLength() + 1 },
            new ArrayPoolList<ValueHash256>(1) { tx.Hash! });
        HandleZeroMessage(announcement, Eth68MessageCode.NewPooledTransactionHashes);
        int releases = 0;
        using PooledTransactionRequests.ResponseContext context = new(1, () => releases++);
        context.Tokens.Add(default);
        ArrayPoolList<Transaction> transactions = new(1) { tx };
        Type requestType = typeof(Eth62ProtocolHandler).GetNestedType("TransactionsRequest", BindingFlags.NonPublic)!;
        object request = Activator.CreateInstance(requestType, transactions, 0)!;
        requestType.GetProperty("ResponseContext", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(request, context);
        MethodInfo process = typeof(Eth68ProtocolHandler).GetMethod("HandleSlow", BindingFlags.Instance | BindingFlags.NonPublic)!;

        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() =>
            process.Invoke(_handler, [request, CancellationToken.None]))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.InnerException, Is.TypeOf<InvalidPeerRequestException>());
            Assert.That(releases, Is.EqualTo(1));
        }
        context.Dispose();
        Assert.That(releases, Is.EqualTo(1));
    }

    private ISession _session = null!;
    private IMessageSerializationService _svc = null!;
    private ISyncServer _syncManager = null!;
    private ITxPool _transactionPool = null!;
    private IGossipPolicy _gossipPolicy = null!;
    private ISpecProvider _specProvider = null!;
    private Block _genesisBlock = null!;
    private Eth68ProtocolHandler _handler = null!;
    private ITxGossipPolicy _txGossipPolicy = null!;
    private ITimerFactory _timerFactory = null!;
    private CompositeDisposable _disposables = null!;

    [SetUp]
    public void Setup()
    {
        _svc = Build.A.SerializationService().WithEth68().TestObject;

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
        _gossipPolicy = Substitute.For<IGossipPolicy>();
        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncManager.Head.Returns(_genesisBlock.Header);
        _syncManager.Genesis.Returns(_genesisBlock.Header);
        _timerFactory = Substitute.For<ITimerFactory>();
        _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
        _handler = CreateHandler(Substitute.For<ITxPoolConfig>());
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_handler.ProtocolCode, Is.EqualTo("eth"));
            Assert.That(_handler.Name, Is.EqualTo("eth68"));
            Assert.That(_handler.ProtocolVersion, Is.EqualTo(68));
            Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(17));
            Assert.That(_handler.IncludeInTxPool, Is.True);
            Assert.That(_handler.ClientId, Is.EqualTo(_session.Node?.ClientId));
            Assert.That(_handler.HeadHash, Is.Null);
            Assert.That(_handler.HeadNumber, Is.EqualTo(0));
        }
    }

    [Test]
    public void Can_handle_NewPooledTransactions_message([Values(0, 1, 2, 100)] int txCount, [Values(true, false)] bool canGossipTransactions)
    {
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(canGossipTransactions);

        GenerateLists(txCount, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<ValueHash256> hashes);

        using NewPooledTransactionHashesMessage68 msg = new(types, sizes, hashes);

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(canGossipTransactions && txCount != 0 ? 1 : 0).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
        Assert.That(
            _handler.RequestedPooledTransactionHashes,
            Is.EqualTo(canGossipTransactions ? txCount : 0));
    }

    private void RecreateVariant(Type handlerType, IBackgroundTaskScheduler scheduler)
    {
        HandleIncomingStatusMessage();
        _handler.Dispose();
        _svc = Build.A.SerializationService().WithEth71(_specProvider).TestObject;
        _handler = (Eth68ProtocolHandler)Activator.CreateInstance(handlerType,
            _session, _svc, new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager, scheduler, _transactionPool, _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager), LimboLogs.Instance,
            Substitute.For<ITxPoolConfig>(), Substitute.For<IChainHeadSpecProvider>(), _txGossipPolicy, TimeProvider.System)!;
        _handler.Init();
        if (_handler.ProtocolVersion == 68)
        {
            HandleIncomingStatusMessage();
        }
        else
        {
            using StatusMessage69 status = new()
            {
                ProtocolVersion = _handler.ProtocolVersion,
                GenesisHash = _genesisBlock.Hash!,
                LatestBlockHash = _genesisBlock.Hash!
            };
            using DisposableByteBuffer packet = _svc.ZeroSerialize(status).AsDisposable();
            packet.ReadByte();
            _handler.HandleMessage(new NettyBufferPacket(packet) { PacketType = 0 });
        }

    }

    [Test]
    public async Task Pooled_reply_membership_is_enforced_through_eth68_to_eth71(
        [Values(typeof(Eth68ProtocolHandler), typeof(Eth69ProtocolHandler), typeof(Eth70ProtocolHandler), typeof(Eth71ProtocolHandler))] Type handlerType,
        [Values(0, 1, 2, 3, 4)] int replyShape)
    {
        RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
        RecreateVariant(handlerType, scheduler);
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>()).Returns(AnnounceResult.RequestRequired);
        Transaction first = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;
        Transaction second = Build.A.Transaction.WithNonce(2).SignedAndResolved().TestObject;
        Transaction unrequested = Build.A.Transaction.WithNonce(3).SignedAndResolved().TestObject;
        using NewPooledTransactionHashesMessage68 announcement = new(
            new byte[] { 0, 0 }.ToPooledList(), new int[] { first.GetLength(), second.GetLength() }.ToPooledList(),
            new[] { first.Hash!.ValueHash256, second.Hash!.ValueHash256 }.ToPooledList());
        HandleZeroMessage(announcement, Eth68MessageCode.NewPooledTransactionHashes);
        long requestId = _session.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<GetPooledTransactionsMessage>()).Last().RequestId;
        Transaction[] returned = replyShape switch
        {
            0 => [unrequested],
            1 => [first, first],
            2 => [],
            3 => [second],
            _ => [first, second]
        };
        using PooledTransactionsMessage response = new(requestId, new Eth65PooledTransactionsMessage(returned.ToPooledList()));
        bool rejected = replyShape < 2;
        try
        {
            if (rejected) Assert.Throws<SubprotocolException>(() => HandleZeroMessage(response, Eth66MessageCode.PooledTransactions));
            else HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
            Assert.That(scheduler.ScheduledFulfillFuncs.Count, Is.EqualTo(!rejected ? 1 : 0));
            await scheduler.Drain(CancellationToken.None);
            _transactionPool.Received(rejected ? 0 : returned.Length).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            for (int index = 0; index < 32; index++) _handler.HandleMessage(PooledTransactionRequestMessage.New(first.Hash!));
            Assert.That(_session.ReceivedCalls().Count(call => call.GetArguments().OfType<GetPooledTransactionsMessage>().Any()), Is.EqualTo(33));
        }
        finally { await scheduler.Drain(new CancellationToken(true)); }
    }

    [Test]
    public void Should_throw_when_sizes_do_not_match([Values] bool removeSize)
    {
        GenerateLists(4, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<ValueHash256> hashes);

        if (removeSize)
        {
            sizes.RemoveAt(sizes.Count - 1);
        }
        else
        {
            types.RemoveAt(sizes.Count - 1);
        }

        using NewPooledTransactionHashesMessage68 msg = new(types, sizes, hashes);

        HandleIncomingStatusMessage();
        Action action = () => HandleZeroMessage(msg, Eth68MessageCode.NewPooledTransactionHashes);
        Assert.That(action, Throws.TypeOf<SubprotocolException>());
    }


    [Test, NonParallelizable]
    public void Should_disconnect_if_tx_size_is_wrong()
    {
        GenerateTxLists(4, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<ValueHash256> hashes, out ArrayPoolList<Transaction> txs);
        sizes[0] += 10;
        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        using PooledTransactionsMessage txsMsg = new(1111, new(txs));

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        Transaction reusable = TxDecoder.TxObjectPool.Get();
        TxDecoder.TxObjectPool.Return(reusable);
        txsMsg.RequestId = LastPooledRequestId();
        HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reusable.Signature, Is.Null);
            Assert.That(reusable.GasLimit, Is.Zero);
        }
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
    }

    [Test]
    public void Retry_requests_a_hash_whether_or_not_a_delivery_consumed_its_announced_shape([Values] bool delivered)
    {
        Transaction tx = Build.A.Transaction.WithNonce(0UL).SignedAndResolved().TestObject;
        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { (byte)tx.Type },
            new ArrayPoolList<int>(1) { tx.GetLength() },
            new ArrayPoolList<ValueHash256>(1) { tx.Hash! });

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        if (delivered)
        {
            using PooledTransactionsMessage txsMsg = new(LastPooledRequestId(), new(new ArrayPoolList<Transaction>(1) { tx }));
            HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);
        }
        _session.ClearReceivedCalls();

        _handler.HandleMessages([tx.Hash!.ValueHash256]);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == 1 && m.EthMessage.Hashes[0] == tx.Hash));
    }

    [Test]
    public void Should_accept_full_blob_tx_with_announced_network_size()
    {
        RecreateHandlerWithBlobSupport();
        Transaction tx = Build.A.Transaction.WithNonce(0UL).WithShardBlobTxTypeAndFields().SignedAndResolved().TestObject;
        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { (byte)tx.Type },
            new ArrayPoolList<int>(1) { tx.GetLength() },
            new ArrayPoolList<ValueHash256>(1) { tx.Hash! });
        using PooledTransactionsMessage txsMsg = new(1111, new(new ArrayPoolList<Transaction>(1) { tx }));

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        txsMsg.RequestId = LastPooledRequestId();
        HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);

        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        _transactionPool.Received().SubmitTx(Arg.Is<Transaction>(received => received.Hash == tx.Hash), Arg.Any<TxHandlingOptions>());
    }

    [Test]
    public void Should_accept_geth_blob_size_estimate()
    {
        RecreateHandlerWithBlobSupport();
        Transaction tx = Build.A.Transaction.WithNonce(0UL).WithShardBlobTxTypeAndFields(spec: Osaka.Instance).SignedAndResolved().TestObject;
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        int sidecarContentLength = Rlp.LengthOf(wrapper.Blobs)
            + Rlp.LengthOf(wrapper.Commitments)
            + Rlp.LengthOf(wrapper.SplitProofs());
        int gethSizeEstimate = tx.GetLength(shouldCountBlobs: false) + Rlp.LengthOfSequence(sidecarContentLength);
        Assert.That(gethSizeEstimate, Is.Not.EqualTo(tx.GetLength()));
        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { (byte)tx.Type },
            new ArrayPoolList<int>(1) { gethSizeEstimate },
            new ArrayPoolList<ValueHash256>(1) { tx.Hash! });
        using PooledTransactionsMessage txsMsg = new(1111, new(new ArrayPoolList<Transaction>(1) { tx }));

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        txsMsg.RequestId = LastPooledRequestId();
        HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);

        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        _transactionPool.Received().SubmitTx(Arg.Is<Transaction>(received => received.Hash == tx.Hash), Arg.Any<TxHandlingOptions>());
    }

    [Test]
    public void Should_reject_inexact_blob_size_estimate([Values(-8, 8)] int sizeDifference)
    {
        RecreateHandlerWithBlobSupport();
        Transaction tx = Build.A.Transaction.WithNonce(0UL).WithShardBlobTxTypeAndFields(spec: Osaka.Instance).SignedAndResolved().TestObject;
        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { (byte)tx.Type },
            new ArrayPoolList<int>(1) { tx.GetLength() + sizeDifference },
            new ArrayPoolList<ValueHash256>(1) { tx.Hash! });
        using PooledTransactionsMessage txsMsg = new(1111, new(new ArrayPoolList<Transaction>(1) { tx }));

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        txsMsg.RequestId = LastPooledRequestId();
        HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);

        _session.Received(1).InitiateDisconnect(
            DisconnectReason.BreachOfProtocol,
            "invalid pooled tx type or size");
    }


    [Test]
    public void Should_disconnect_if_tx_type_is_wrong()
    {
        GenerateTxLists(4, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<ValueHash256> hashes, out ArrayPoolList<Transaction> txs);
        types[0]++;
        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        using PooledTransactionsMessage txsMsg = new(1111, new(txs));

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        txsMsg.RequestId = LastPooledRequestId();
        HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);

        _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
    }

    [Test]
    public void should_disconnect_if_pooled_transactions_response_contains_sparse_blob_tx()
    {
        RecreateHandlerWithBlobSupport();
        Transaction sparseTx = BuildSparseBlobTransaction();
        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { (byte)sparseTx.Type },
            new ArrayPoolList<int>(1) { sparseTx.GetLength() },
            new ArrayPoolList<ValueHash256>(1) { sparseTx.Hash! });
        using PooledTransactionsMessage txsMsg = new(1111, new(new ArrayPoolList<Transaction>(1) { sparseTx }));

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        txsMsg.RequestId = LastPooledRequestId();
        HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);

        _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }

    [Test]
    public void should_disconnect_if_transactions_broadcast_contains_blob_tx()
    {
        RecreateHandlerWithBlobSupport();
        Transaction legacyTx = Build.A.Transaction.SignedAndResolved().TestObject;
        using TransactionsMessage msg = new(new ArrayPoolList<Transaction>(2) { legacyTx, BuildFullBlobTransaction() });

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth62MessageCode.Transactions);

        _session.Received(1).InitiateDisconnect(DisconnectReason.InvalidTxOrUncle, "blob transaction in a Transactions broadcast");
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }

    public enum AnnouncedShape { None, Matching, WrongSize, WrongType }

    [Test]
    public void Known_broadcast_tx_is_settled_undecoded_only_when_it_matches_the_announced_shape([Values] AnnouncedShape announced)
    {
        IRecyclableTxPool recyclablePool = ReplaceWithRecyclableTxPool();
        recyclablePool.TrySubmitKnown(Arg.Any<ValueHash256>()).Returns(true);
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).SignedAndResolved().TestObject;
        if (announced is not AnnouncedShape.None)
        {
            using NewPooledTransactionHashesMessage68 announcement = new(
                new ArrayPoolList<byte>(1) { (byte)(announced is AnnouncedShape.WrongType ? TxType.AccessList : tx.Type) },
                new ArrayPoolList<int>(1) { tx.GetLength() + (announced is AnnouncedShape.WrongSize ? 1 : 0) },
                new ArrayPoolList<ValueHash256>(1) { tx.Hash! });
            HandleZeroMessage(announcement, Eth68MessageCode.NewPooledTransactionHashes);
        }

        using TransactionsMessage broadcast = new(new ArrayPoolList<Transaction>(1) { tx });
        HandleZeroMessage(broadcast, Eth62MessageCode.Transactions);

        bool shapeMatches = announced is AnnouncedShape.None or AnnouncedShape.Matching;
        recyclablePool.Received(shapeMatches ? 1 : 0).TrySubmitKnown(Arg.Any<ValueHash256>());
        recyclablePool.DidNotReceive().SubmitOwnedTx(Arg.Any<Transaction>(), Arg.Any<object?>(), out _);
        _session.Received(shapeMatches ? 0 : 1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
    }

    [Test]
    public void Known_broadcast_tx_from_a_closing_peer_is_not_settled()
    {
        IRecyclableTxPool recyclablePool = ReplaceWithRecyclableTxPool();
        recyclablePool.TrySubmitKnown(Arg.Any<ValueHash256>()).Returns(true);
        _session.IsClosing.Returns(true);
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).SignedAndResolved().TestObject;

        using TransactionsMessage broadcast = new(new ArrayPoolList<Transaction>(1) { tx });
        HandleZeroMessage(broadcast, Eth62MessageCode.Transactions);

        recyclablePool.DidNotReceive().TrySubmitKnown(Arg.Any<ValueHash256>());
        recyclablePool.DidNotReceive().SubmitOwnedTx(Arg.Any<Transaction>(), Arg.Any<object?>(), out _);
    }

    [Test]
    public void Should_validate_retained_shape_for_oversized_announcement([Values] bool correlated)
    {
        ITxPoolConfig txPoolConfig = Substitute.For<ITxPoolConfig>();
        txPoolConfig.MaxTxSize.Returns(128 * MemorySizes.KiB);
        ReplaceHandler(txPoolConfig);

        Transaction tx = Build.A.Transaction.WithShardBlobTxTypeAndFields().SignedAndResolved()
            .WithData(new byte[200_000]).WithHash(TestItem.KeccakC).TestObject;

        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { (byte)TxType.EIP1559 },
            new ArrayPoolList<int>(1) { tx.GetLength() },
            new ArrayPoolList<ValueHash256>(1) { tx.Hash });
        AssertResponseForUnrequestableAnnouncement(tx, hashesMsg, correlated);
    }

    [Test]
    public void Should_process_huge_transaction()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithData(new byte[(int)2.MiB])
            .WithHash(TestItem.KeccakA).TestObject;

        using NewPooledTransactionHashesMessage68 msg = new(new ArrayPoolList<byte>(1) { (byte)tx.Type },
            new ArrayPoolList<int>(1) { tx.GetLength() }, new ArrayPoolList<ValueHash256>(1) { tx.Hash });

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
    }

    [TestCase(1)]
    [TestCase(NewPooledTransactionHashesMessage68.MaxCount - 1)]
    [TestCase(NewPooledTransactionHashesMessage68.MaxCount)]
    public void should_send_up_to_MaxCount_hashes_in_one_NewPooledTransactionHashesMessage68(int txCount)
    {
        Transaction[] txs = new Transaction[txCount];

        for (int i = 0; i < txCount; i++)
        {
            txs[i] = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved().TestObject;
        }

        _handler.SendNewTransactions(txs, false);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage68>(m =>
            m.Hashes.Count == txCount &&
            m.Sizes.Count == txCount &&
            m.Types.Count == txCount));
    }

    [Test]
    public void transaction_knowledge_is_independent_for_each_peer([Values] bool receivedFromPeer)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
        if (receivedFromPeer)
        {
            HandleIncomingStatusMessage();
            using NewPooledTransactionHashesMessage68 message = new(
                new ArrayPoolList<byte>(1) { (byte)tx.Type },
                new ArrayPoolList<int>(1) { tx.GetLength() },
                new ArrayPoolList<ValueHash256>(1) { tx.Hash! });
            HandleZeroMessage(message, Eth68MessageCode.NewPooledTransactionHashes);
        }
        else
        {
            _handler.SendNewTransactions([tx], sendFullTx: false);
        }

        _session.ClearReceivedCalls();
        _handler.SendNewTransactions([tx], sendFullTx: false);
        _session.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage68>());

        using Eth68ProtocolHandler otherPeer = CreateHandler(Substitute.For<ITxPoolConfig>());
        otherPeer.SendNewTransactions([tx], sendFullTx: false);
        otherPeer.SendNewTransactions([tx], sendFullTx: false);
        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage68>(m => m.Hashes[0] == tx.Hash));

        _handler.SendNewTransactions([tx], sendFullTx: true);
        _session.Received(1).DeliverMessage(Arg.Any<TransactionsMessage>());
    }

    [Test]
    public void shared_hash_cache_handles_capacity([Values(0, 1, 8, 17)] int capacity)
    {
        TransactionHashCache cache = new(capacity);
        TransactionHashCache.PeerCache peer = cache.CreatePeerCache();
        ValueHash256 hash = TestItem.KeccakA.ValueHash256;
        Assert.That(peer.Set(in hash), Is.True);
        Assert.That(peer.Set(in hash), Is.EqualTo(capacity == 0));
    }

    [Test]
    public void shared_hash_slot_reuse_does_not_suppress_new_transactions()
    {
        TransactionHashCache cache = new(8);
        TransactionHashCache.PeerCache first = cache.CreatePeerCache();
        TransactionHashCache.PeerCache second = cache.CreatePeerCache();
        for (int i = 0; i < 64; i++)
        {
            ValueHash256 hash = Keccak.Compute(i.ToString()).ValueHash256;
            Assert.That(first.Set(in hash), Is.True);
#pragma warning disable NUnit2045 // The assertions advance or mutate the same state before checking it; preserve fail-fast ordering.
            Assert.That(first.Set(in hash), Is.False);
#pragma warning restore NUnit2045
            Assert.That(second.Set(in hash), Is.True);
            Assert.That(second.Set(in hash), Is.False);
        }

        ValueHash256 evicted = Keccak.Compute("0").ValueHash256;
#pragma warning disable NUnit2045 // The assertions advance or mutate the same state before checking it; preserve fail-fast ordering.
        Assert.That(first.Set(in evicted), Is.True);
#pragma warning restore NUnit2045
        Assert.That(second.Set(in evicted), Is.True);
    }

    [Test]
    public void shared_hash_cache_keeps_recently_announced_transactions()
    {
        TransactionHashCache cache = new(8);
        TransactionHashCache.PeerCache peer = cache.CreatePeerCache();
        ValueHash256 active = TestItem.KeccakA.ValueHash256;
        Assert.That(peer.Set(in active), Is.True);
        for (int i = 0; i < 64; i++)
        {
            ValueHash256 hash = Keccak.Compute(i.ToString()).ValueHash256;
#pragma warning disable NUnit2045 // The assertions advance or mutate the same state before checking it; preserve fail-fast ordering.
            Assert.That(peer.Set(in hash), Is.True);
#pragma warning restore NUnit2045
            Assert.That(peer.Set(in active), Is.False);
        }
    }

    [Test]
    public void concurrent_eviction_does_not_suppress_first_announcements()
    {
        TransactionHashCache cache = new(8);
        ValueHash256[] hashes = new ValueHash256[1024];
        for (int i = 0; i < hashes.Length; i++) hashes[i] = Keccak.Compute(i.ToString()).ValueHash256;
        int suppressed = 0;
        Parallel.For(0, 4, _ =>
        {
            TransactionHashCache.PeerCache peer = cache.CreatePeerCache();
            foreach (ValueHash256 hash in hashes)
            {
                if (!peer.Set(in hash)) Interlocked.Increment(ref suppressed);
            }
        });
        Assert.That(suppressed, Is.Zero);
    }

    [Test]
    public void concurrent_announcements_are_deduplicated_per_peer()
    {
        TransactionHashCache cache = new(8);
        TransactionHashCache.PeerCache[] peers = [cache.CreatePeerCache(), cache.CreatePeerCache()];
        int[] announcements = new int[peers.Length];
        ValueHash256 hash = TestItem.KeccakA.ValueHash256;
        Parallel.For(0, 1024, i =>
        {
            int peer = i % peers.Length;
            if (peers[peer].Set(in hash)) Interlocked.Increment(ref announcements[peer]);
        });
        Assert.That(announcements, Is.EqualTo(new[] { 1, 1 }));
    }

    [Test]
    public void should_send_blob_tx_announcement_in_NewPooledTransactionHashesMessage68()
    {
        Transaction tx = Build.A.Transaction.WithNonce(0UL).WithShardBlobTxTypeAndFields().SignedAndResolved().TestObject;

        _handler.SendNewTransaction(tx);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage68>(m =>
            m.Hashes.Count == 1 &&
            m.Sizes.Count == 1 &&
            m.Types.Count == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.Sizes[0] == tx.GetLength() &&
            (TxType)m.Types[0] == tx.Type));
    }

    [Test]
    public void should_not_announce_sparse_blob_tx_to_eth68_peer()
    {
        Transaction tx = BuildSparseBlobTransaction();

        _handler.SendNewTransaction(tx);

        _session.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage68>());
    }

    [Test]
    public void should_not_announce_sparse_light_blob_tx_to_eth68_peer()
    {
        Transaction tx = new LightTransaction(BuildSparseBlobTransaction());

        _handler.SendNewTransaction(tx);

        _session.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage68>());
    }

    [Test]
    public void should_announce_completed_blob_tx_after_skipping_sparse_version([Values] bool sendBatch)
    {
        Transaction fullTx = BuildFullBlobTransaction();
        Transaction sparseTx = BuildSparseBlobTransaction(fullTx);

        SendTransaction(sparseTx, sendBatch);
        _session.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage68>());

        SendTransaction(fullTx, sendBatch);
        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage68>(m =>
            m.Hashes.Count == 1 && m.Hashes[0] == fullTx.Hash));
    }

    [Test]
    public async Task should_not_serve_sparse_blob_tx_to_eth68_peer()
    {
        Transaction tx = BuildSparseBlobTransaction();
        _transactionPool.TryGetPendingTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });

        using GetPooledTransactionsMessage65 request = new(new ArrayPoolList<ValueHash256>(1) { tx.Hash! });
        using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

        Assert.That(response.Transactions, Is.Empty);
    }

    [Test]
    public async Task should_not_serve_sparse_light_blob_tx_to_eth68_peer()
    {
        Transaction tx = new LightTransaction(BuildSparseBlobTransaction());
        _transactionPool.TryGetPendingTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });

        using GetPooledTransactionsMessage65 request = new(new ArrayPoolList<ValueHash256>(1) { tx.Hash! });
        using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

        Assert.That(response.Transactions, Is.Empty);
    }

    [Test]
    public async Task should_reject_many_sparse_blob_hashes_without_loading_transactions()
    {
        const int hashCount = 300;
        Hash256[] hashes = new Hash256[hashCount];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = new Hash256(i.ToString("X64"));
        }

        BlobCellMask sparseMask = BlobCellMask.FromIndices([0]);
        _transactionPool.TryGetPendingBlobCellMaskAndNetworkForm(Arg.Any<ValueHash256>(), out Arg.Any<BlobCellMask>(), out Arg.Any<IOwnedReadOnlyList<byte>>())
            .Returns(x =>
            {
                x[1] = sparseMask;
                x[2] = null;
                return true;
            });

        using GetPooledTransactionsMessage65 request = new(hashes.Select(static h => h.ValueHash256).ToArray().ToPooledList());
        using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage response =
            await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

        Assert.That(response.Transactions, Is.Empty);
        _transactionPool.Received(hashCount).TryGetPendingBlobCellMaskAndNetworkForm(Arg.Any<ValueHash256>(), out Arg.Any<BlobCellMask>(), out Arg.Any<IOwnedReadOnlyList<byte>>());
        _transactionPool.DidNotReceive().TryGetPendingBlobCellMask(Arg.Any<ValueHash256>(), out Arg.Any<BlobCellMask>());
        _transactionPool.DidNotReceive().TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>());
    }

    [TestCase(NewPooledTransactionHashesMessage68.MaxCount - 1)]
    [TestCase(NewPooledTransactionHashesMessage68.MaxCount)]
    [TestCase(10000)]
    [TestCase(20000)]
    public void should_send_more_than_MaxCount_hashes_in_more_than_one_NewPooledTransactionHashesMessage68(int txCount)
    {
        int nonFullMsgTxsCount = txCount % NewPooledTransactionHashesMessage68.MaxCount;
        int messagesCount = txCount / NewPooledTransactionHashesMessage68.MaxCount + (nonFullMsgTxsCount > 0 ? 1 : 0);
        Transaction[] txs = new Transaction[txCount];

        for (int i = 0; i < txCount; i++)
        {
            txs[i] = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved().TestObject;
        }

        _handler.SendNewTransactions(txs, false);

        _session.Received(messagesCount).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage68>(m => m.Hashes.Count == NewPooledTransactionHashesMessage68.MaxCount || m.Hashes.Count == nonFullMsgTxsCount));
    }

    [TestCase(BlobsSupportMode.InMemory, 1, TestName = "Blob_sized_frame_tx_announcement_is_requested_when_blob_frame_txs_are_admissible")]
    [TestCase(BlobsSupportMode.Disabled, 0, TestName = "Blob_sized_frame_tx_announcement_is_not_requested_when_blobs_are_disabled")]
    [TestCase(BlobsSupportMode.Storage, 1, TestName = "Blob_sized_frame_tx_announcement_is_requested_under_persistent_storage")]
    [TestCase(BlobsSupportMode.StorageWithReorgs, 1, TestName = "Blob_sized_frame_tx_announcement_is_requested_under_the_default_mode")]
    public void Frame_tx_announcement_budget_follows_blob_support(BlobsSupportMode blobsSupport, int expectedRequests)
    {
        TxPoolConfig txPoolConfig = new() { BlobsSupport = blobsSupport };
        _handler = BuildHandler(txPoolConfig, frameTxsEnabled: true);

        // Between MaxTxSize (128 KiB) and MaxBlobTxSize (1 MiB): admissible only where blobs are supported.
        AssertFrameTxAnnouncementRequested((int)txPoolConfig.MaxTxSize! + 1, expectedRequests);
    }

    // The two caps are independently operator-settable, so the blobless one can be the larger; a gate keyed on
    // the blob cap alone would then drop a type-6 hash SizeTxFilter accepts.
    [Test]
    public void Frame_tx_announcement_budget_covers_the_larger_of_the_two_size_caps()
    {
        TxPoolConfig txPoolConfig = new()
        {
            BlobsSupport = BlobsSupportMode.InMemory,
            MaxTxSize = 4.MiB,
            MaxBlobTxSize = 1.MiB
        };
        _handler = BuildHandler(txPoolConfig, frameTxsEnabled: true);

        AssertFrameTxAnnouncementRequested((int)txPoolConfig.MaxBlobTxSize! + 1, expectedRequests: 1);
    }

    // Without the fork a frame tx is rejected at ingress, so requesting one is always wasted bandwidth.
    [TestCase(true, 1, TestName = "Frame_tx_announcement_is_requested_once_the_fork_is_active")]
    [TestCase(false, 0, TestName = "Frame_tx_announcement_is_not_requested_before_the_fork")]
    public void Frame_tx_announcement_request_follows_fork_activation(bool frameTxsEnabled, int expectedRequests)
    {
        TxPoolConfig txPoolConfig = new() { BlobsSupport = BlobsSupportMode.InMemory };
        _handler = BuildHandler(txPoolConfig, frameTxsEnabled);

        AssertFrameTxAnnouncementRequested(announcedSize: 100, expectedRequests);
    }

    // Best-suggested leads the processed head, so a gate keyed on the latter would decline while the pool already accepts.
    [Test]
    public void Frame_tx_announcement_is_requested_as_soon_as_the_ingress_filter_would_accept()
    {
        IReleaseSpec preFork = Substitute.For<IReleaseSpec>();
        preFork.IsEip8141Enabled.Returns(false);
        IReleaseSpec atFork = Substitute.For<IReleaseSpec>();
        atFork.IsEip8141Enabled.Returns(true);

        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        // Best-suggested has crossed activation; the processed head has not.
        specProvider.GetCurrentHeadSpec().Returns(atFork);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(preFork);

        _handler = CreateHandler(new TxPoolConfig { BlobsSupport = BlobsSupportMode.InMemory }, specProvider);

        AssertFrameTxAnnouncementRequested(announcedSize: 100, expectedRequests: 1);
    }

    /// <summary>Runs the status handshake, then announces exactly one frame transaction hash of the given size.</summary>
    private void AssertFrameTxAnnouncementRequested(int announcedSize, int expectedRequests)
    {
        using ArrayPoolList<byte> types = new(1) { (byte)TxType.FrameTx };
        using ArrayPoolList<int> sizes = new(1) { announcedSize };
        using ArrayPoolList<ValueHash256> hashes = new(1) { TestItem.KeccakA.ValueHash256 };

        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(expectedRequests).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
    }

    private Eth68ProtocolHandler BuildHandler(ITxPoolConfig txPoolConfig, bool frameTxsEnabled)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(frameTxsEnabled);
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        specProvider.GetCurrentHeadSpec().Returns(spec);

        return CreateHandler(txPoolConfig, specProvider);
    }

    // One-transaction requests drain 32 per retry tick; the retry cache drops a hash after 48 local refusals, so the
    // largest count stays well inside that bound.
    [Test]
    public async Task should_divide_GetPooledTransactionsMessage_if_max_message_size_is_exceeded([Values(0, 1, 100, 1024)] int numberOfTransactions, [Values(97, TransactionsMessage.MaxPacketSize)] int sizeOfOneTx)
    {
        ManualTimeProvider requestClock = new();
        ReplaceHandler(Substitute.For<ITxPoolConfig>(), new RecordingBackgroundTaskScheduler { QueueCapacity = 8192 }, timeProvider: requestClock);
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

        int maxNumberOfTxsInOneMsg = int.Min(sizeOfOneTx <= TransactionsMessage.MaxPacketSize ? TransactionsMessage.MaxPacketSize / sizeOfOneTx : 256, 256);
        int messagesCount = numberOfTransactions / maxNumberOfTxsInOneMsg + (numberOfTransactions % maxNumberOfTxsInOneMsg == 0 ? 0 : 1);

        using ArrayPoolList<byte> types = new(numberOfTransactions);
        using ArrayPoolList<int> sizes = new(numberOfTransactions);
        using ArrayPoolList<ValueHash256> hashes = new(numberOfTransactions);

        for (int i = 0; i < numberOfTransactions; i++)
        {
            types.Add(0);
            sizes.Add(sizeOfOneTx);
            hashes.Add(new Hash256(i.ToString("X64")));
        }

        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        int announcedRequests = SentRequests().Length;
        Assert.That(announcedRequests, Is.EqualTo(Math.Min(messagesCount, 32)),
            "unanswered requests remain bounded before any reply releases capacity");
        long[] answered = [];
        int hashesPerRetryTick = Math.Min(RetryWorkBatchSize, 32 * maxNumberOfTxsInOneMsg);
        for (int round = 0; round <= (numberOfTransactions + hashesPerRetryTick - 1) / hashesPerRetryTick; round++)
        {
            GetPooledTransactionsMessage[] pending = SentRequests()
                .Where(request => !answered.Contains(request.RequestId)).ToArray();
            foreach (GetPooledTransactionsMessage request in pending)
            {
                using PooledTransactionsMessage response = new(request.RequestId,
                    new Eth65PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
                HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
            }
            answered = SentRequests().Select(request => request.RequestId).ToArray();
            if (SentRequests().Sum(static request => request.EthMessage.Hashes.Count) == numberOfTransactions) break;
            // This checks packet splitting after lease release; the real pool fixtures cover retry cadence and deadlines.
            ProcessRetryTick(retry);
            Assert.That(SentRequests().Length, Is.GreaterThan(answered.Length),
                "original unsent hashes must become requestable after real replies release the leases");
        }

        // The announced-shape cache is lossy, and which shapes it drops depends on the per-process hash seed. A retry
        // splits by size only the hashes whose shape it still holds and requests the rest as plain eth/66 hashes.
        HashSet<ValueHash256> shapeless = hashes.Where(hash => !HasTxShape(_handler, hash)).ToHashSet();
        GetPooledTransactionsMessage[] requests = SentRequests();
        using (Assert.EnterMultipleScope())
        {
            for (int index = 0; index < requests.Length; index++)
            {
                ValueHash256[] requested = requests[index].EthMessage.Hashes.AsSpan().ToArray();
                if (index >= announcedRequests && shapeless.Contains(requested[0]))
                    Assert.That(requested, Is.SubsetOf(shapeless), "a shapeless retry is not mixed with sized hashes");
                else
                    Assert.That(requested, Has.Length.AtMost(maxNumberOfTxsInOneMsg), "a sized request is split by the announced sizes");
            }
            Assert.That(requests.SelectMany(static request => request.EthMessage.Hashes), Is.EquivalentTo(hashes));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }

        GetPooledTransactionsMessage[] SentRequests() => _session.ReceivedCalls()
            .SelectMany(static call => call.GetArguments().OfType<GetPooledTransactionsMessage>()).ToArray();
    }

    private static bool HasTxShape(Eth68ProtocolHandler handler, ValueHash256 hash) =>
        (bool)typeof(Eth68ProtocolHandler)
            .GetMethod("TryGetTxShape", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(handler, [hash, null])!;

    [Test]
    public async Task A_large_announcement_flood_cannot_queue_more_than_thirty_two_replies()
    {
        const int announcements = 2048;
        const int hashesPerAnnouncement = 256;
        RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
        ManualTimeProvider clock = new();
        ReplaceHandler(Substitute.For<ITxPoolConfig>(), scheduler, clock);
        HandleIncomingStatusMessage();
        _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        byte[] hashBytes = new byte[ValueHash256.MemorySize];
        try
        {
            for (int batch = 0; batch < announcements; batch++)
            {
                using ArrayPoolList<byte> types = new(hashesPerAnnouncement);
                using ArrayPoolList<int> sizes = new(hashesPerAnnouncement);
                using ArrayPoolList<ValueHash256> hashes = new(hashesPerAnnouncement);
                for (int index = 0; index < hashesPerAnnouncement; index++)
                {
                    BinaryPrimitives.WriteInt32BigEndian(hashBytes, batch * hashesPerAnnouncement + index + 1);
                    types.Add((byte)TxType.Legacy);
                    sizes.Add(97);
                    hashes.Add(new ValueHash256(hashBytes));
                }
                using NewPooledTransactionHashesMessage68 message = new(types, sizes, hashes);
                HandleZeroMessage(message, Eth68MessageCode.NewPooledTransactionHashes);
                Assert.That(SentRequests().Length, Is.EqualTo(Math.Min(batch + 1, 32)));
                _transactionPool.ClearReceivedCalls();
            }
            GetPooledTransactionsMessage[] outstanding = SentRequests();
            Assert.That(outstanding, Has.Length.EqualTo(32));
            foreach (GetPooledTransactionsMessage request in outstanding)
            {
                Assert.That(request.EthMessage.Hashes, Has.Count.EqualTo(hashesPerAnnouncement));
                using PooledTransactionsMessage response = new(request.RequestId,
                    new Eth65PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
                HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
            }
            Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(32));
            HashSet<long> sentIds = outstanding.Select(static request => request.RequestId).ToHashSet();
            for (long uncorrelatedId = 0; uncorrelatedId < announcements; uncorrelatedId++)
            {
                if (sentIds.Contains(uncorrelatedId)) continue;
                using PooledTransactionsMessage uncorrelated = new(uncorrelatedId,
                    new Eth65PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
                HandleZeroMessage(uncorrelated, Eth66MessageCode.PooledTransactions);
            }
            foreach (GetPooledTransactionsMessage request in outstanding)
            {
                using PooledTransactionsMessage duplicate = new(request.RequestId,
                    new Eth65PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
                HandleZeroMessage(duplicate, Eth66MessageCode.PooledTransactions);
            }
            _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(32));
                Assert.That(SentRequests(), Has.Length.EqualTo(32), "claimed queued responses still own the protected quota");
                _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
                _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            await scheduler.Drain(CancellationToken.None);
            _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
            Assert.That(SentRequests(), Has.Length.EqualTo(33), "completed replies release room for the next legitimate request");
        }
        finally { await scheduler.Drain(new CancellationToken(true)); }

        GetPooledTransactionsMessage[] SentRequests() => _session.ReceivedCalls()
            .SelectMany(static call => call.GetArguments().OfType<GetPooledTransactionsMessage>()).ToArray();
    }

    [Test]
    public async Task A_closing_refused_announcer_does_not_block_an_alternate_source()
    {
        ManualTimeProvider requestClock = new();
        ReplaceHandler(Substitute.For<ITxPoolConfig>(), new RecordingBackgroundTaskScheduler { QueueCapacity = 8192 }, timeProvider: requestClock);
        Eth68ProtocolHandlerTests alternate = new();
        alternate.Setup();
        ManualTimeProvider clock = new();
        await using RetryCache<PooledTransactionRequestMessage, ValueHash256> retry = CreateRetryCache(clock);
        try
        {
            alternate._session.Node.Returns(new Node(TestItem.PublicKeyB, "9.9.9.9", 30303));
            alternate.ReplaceHandler(Substitute.For<ITxPoolConfig>(), timeProvider: requestClock);
            BindRetry(_transactionPool);
            BindRetry(alternate._transactionPool);
            HandleIncomingStatusMessage();
            alternate.HandleIncomingStatusMessage();
            for (int i = 0; i < 32; i++) _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
            Assert.That(Requests(_session), Has.Length.EqualTo(32));
            using NewPooledTransactionHashesMessage68 original = new(
                new ArrayPoolList<byte>(1) { (byte)TxType.Legacy },
                new ArrayPoolList<int>(1) { 100 },
                new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA.ValueHash256 });
            HandleZeroMessage(original, Eth68MessageCode.NewPooledTransactionHashes);
            using NewPooledTransactionHashesMessage68 other = new(
                new ArrayPoolList<byte>(1) { (byte)TxType.Legacy },
                new ArrayPoolList<int>(1) { 100 },
                new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA.ValueHash256 });
            alternate.HandleZeroMessage(other, Eth68MessageCode.NewPooledTransactionHashes);
            Assert.That(Requests(alternate._session), Is.Empty);
            _transactionPool.ClearReceivedCalls();
            _session.IsClosing.Returns(true);
            _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
            _transactionPool.DidNotReceive().TryDeferTransactionRequest(Arg.Any<ValueHash256>(),
                Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
            clock.Advance(TimeSpan.FromMilliseconds(500));
            requestClock.Advance(TimeSpan.FromMilliseconds(500));
            ProcessRetryTick(retry);
            GetPooledTransactionsMessage[] sent = Requests(alternate._session);
            Assert.That(sent, Has.Length.EqualTo(1));
            using PooledTransactionsMessage response = new(sent[0].RequestId,
                new Eth65PooledTransactionsMessage(IOwnedReadOnlyList<Transaction>.Empty));
            alternate.HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sent[0].EthMessage.Hashes, Is.EquivalentTo(new[] { TestItem.KeccakA.ValueHash256 }));
                Assert.That(Requests(_session), Has.Length.EqualTo(32));
                alternate._session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
        }
        finally { alternate.TearDown(); }

        void BindRetry(ITxPool pool)
        {
            pool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
                .Returns(call =>
                {
                    ValueHash256 hash = call.ArgAt<ValueHash256>(0);
                    return retry.Announced(in hash, call.ArgAt<IMessageHandler<PooledTransactionRequestMessage>>(1));
                });
            pool.TryDeferTransactionRequest(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
                .Returns(call => TryDeferRequest(retry, call.ArgAt<ValueHash256>(0),
                    call.ArgAt<IMessageHandler<PooledTransactionRequestMessage>>(1)));
        }

        static GetPooledTransactionsMessage[] Requests(ISession session) => session.ReceivedCalls()
            .SelectMany(static call => call.GetArguments().OfType<GetPooledTransactionsMessage>()).ToArray();
    }

    [Test]
    public void should_divide_batched_retry_GetPooledTransactionsMessage_using_announced_tx_sizes()
    {
        const int numberOfTransactions = 3;
        using ArrayPoolList<byte> types = new(numberOfTransactions);
        using ArrayPoolList<int> sizes = new(numberOfTransactions);
        using ArrayPoolList<ValueHash256> hashes = new(numberOfTransactions);
        ValueHash256[] txHashes = new ValueHash256[numberOfTransactions];

        for (int i = 0; i < numberOfTransactions; i++)
        {
            Hash256 hash = new(i.ToString("X64"));
            types.Add((byte)TxType.EIP1559);
            sizes.Add(TransactionsMessage.MaxPacketSize);
            hashes.Add(hash);
            txHashes[i] = hash;
        }

        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        _session.ClearReceivedCalls();
        _transactionPool.ClearReceivedCalls();

        _handler.HandleMessages(txHashes);

        _session.Received(numberOfTransactions).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == 1));
        _transactionPool.DidNotReceive().NotifyAboutTx(
            Arg.Any<ValueHash256>(),
            Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
    }

    [Test]
    public void should_keep_announced_tx_sizes_for_delayed_batched_retries()
    {
        const int numberOfTransactions = 3;
        using ArrayPoolList<byte> types = new(numberOfTransactions);
        using ArrayPoolList<int> sizes = new(numberOfTransactions);
        using ArrayPoolList<ValueHash256> hashes = new(numberOfTransactions);
        ValueHash256[] txHashes = new ValueHash256[numberOfTransactions];

        for (int i = 0; i < numberOfTransactions; i++)
        {
            Hash256 hash = new(i.ToString("X64"));
            types.Add((byte)TxType.EIP1559);
            sizes.Add(TransactionsMessage.MaxPacketSize);
            hashes.Add(hash);
            txHashes[i] = hash;
        }

        _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(
                AnnounceResult.Delayed,
                AnnounceResult.Delayed,
                AnnounceResult.Delayed,
                AnnounceResult.RequestRequired,
                AnnounceResult.RequestRequired,
                AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        _session.ClearReceivedCalls();
        _transactionPool.ClearReceivedCalls();

        _handler.HandleMessages(txHashes);

        _session.Received(numberOfTransactions).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == 1));
        _transactionPool.DidNotReceive().NotifyAboutTx(
            Arg.Any<ValueHash256>(),
            Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
    }

    [Test]
    public void should_batch_retry_hashes_without_announced_tx_sizes_through_base_paging()
    {
        const int numberOfTransactions = 3;
        ValueHash256[] txHashes = new ValueHash256[numberOfTransactions];

        for (int i = 0; i < numberOfTransactions; i++)
        {
            txHashes[i] = new Hash256(i.ToString("X64"));
        }

        _handler.HandleMessages(txHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == numberOfTransactions));
        _transactionPool.DidNotReceive().NotifyAboutTx(
            Arg.Any<ValueHash256>(),
            Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
    }

    [Test]
    public void should_page_batched_retry_hashes_without_announced_tx_sizes_through_base_paging()
    {
        const int numberOfTransactions = 300;
        ValueHash256[] txHashes = new ValueHash256[numberOfTransactions];

        for (int i = 0; i < numberOfTransactions; i++)
        {
            txHashes[i] = new Hash256(i.ToString("X64"));
        }

        _handler.HandleMessages(txHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == 256));
        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == 44));
        AssertRequestedHashes(txHashes);
    }

    private void AssertRequestedHashes(System.Collections.Generic.IEnumerable<ValueHash256> expected)
    {
        ValueHash256[] actual = _session.ReceivedCalls()
            .SelectMany(static call => call.GetArguments().OfType<GetPooledTransactionsMessage>())
            .SelectMany(static message => message.EthMessage.Hashes)
            .ToArray();
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Should_request_small_oversized_announcements_in_one_message()
    {
        using ArrayPoolList<byte> types = new(3) { 0, 0, 0 };
        using ArrayPoolList<int> sizes = new(3) { 200_000, 200_000, 200_000 };
        using ArrayPoolList<ValueHash256> hashes = new(3)
        {
            TestItem.KeccakA,
            TestItem.KeccakB,
            TestItem.KeccakC
        };
        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
        _session.Received().DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m =>
            m.EthMessage.Hashes.Count == 3 &&
            m.EthMessage.Hashes[0] == TestItem.KeccakA &&
            m.EthMessage.Hashes[1] == TestItem.KeccakB &&
            m.EthMessage.Hashes[2] == TestItem.KeccakC));
    }

    [Test]
    public void Should_split_large_oversized_announcements_by_response_size()
    {
        const int transactionSize = 200_000;
        const int transactionCount = 12;
        int transactionsInFirstRequest = (int)(2.MiB / transactionSize);

        using ArrayPoolList<byte> types = new(transactionCount);
        using ArrayPoolList<int> sizes = new(transactionCount);
        using ArrayPoolList<ValueHash256> hashes = new(transactionCount);

        for (int i = 0; i < transactionCount; i++)
        {
            types.Add((byte)TxType.EIP1559);
            sizes.Add(transactionSize);
            hashes.Add(new Hash256(i.ToString("X64")));
        }

        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == transactionsInFirstRequest));
        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == transactionCount - transactionsInFirstRequest));
    }

    [Test]
    public void Should_not_mix_regular_and_oversized_announcements_in_one_request()
    {
        using ArrayPoolList<byte> types = new(3) { (byte)TxType.EIP1559, (byte)TxType.EIP1559, (byte)TxType.EIP1559 };
        using ArrayPoolList<int> sizes = new(3) { 50_000, 200_000, 50_000 };
        using ArrayPoolList<ValueHash256> hashes = new(3) { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == 1 && m.EthMessage.Hashes[0] == TestItem.KeccakA));
        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == 1 && m.EthMessage.Hashes[0] == TestItem.KeccakB));
        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == 1 && m.EthMessage.Hashes[0] == TestItem.KeccakC));
    }

    [Test]
    public async Task Should_serve_multiple_oversized_pooled_transactions_within_soft_limit()
    {
        const int transactionCount = 12;
        Transaction tx = Build.A.Transaction.WithData(new byte[200_000]).SignedAndResolved().TestObject;
        int transactionsInResponse = (int)(2.MiB / tx.GetLength());
        _transactionPool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });

        ArrayPoolList<ValueHash256> hashes = [with(transactionCount)];
        for (int i = 0; i < transactionCount; i++)
        {
            hashes.Add(new Hash256(i.ToString("X64")));
        }

        using Eth65GetPooledTransactionsMessage request = new(hashes);
        using Eth65PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

        Assert.That(response.Transactions.Count, Is.EqualTo(transactionsInResponse));
    }

    [Test]
    public async Task Should_serve_a_single_transaction_larger_than_the_soft_limit()
    {
        Transaction tx = Build.A.Transaction.WithData(new byte[(int)2.MiB]).SignedAndResolved().TestObject;
        _transactionPool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });

        ArrayPoolList<ValueHash256> hashes = new(1) { TestItem.KeccakA };
        using Eth65GetPooledTransactionsMessage request = new(hashes);
        using Eth65PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

        Assert.That(response.Transactions.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_serve_stored_blob_tx_encoding_as_the_decoded_tx_in_request_order([Values(0, 1, 2)] int blobTxIndex)
    {
        Transaction blobTx = BuildFullBlobTransaction();
        Transaction[] txs = new Transaction[3];
        for (int i = 0; i < txs.Length; i++)
        {
            txs[i] = i == blobTxIndex ? blobTx : Build.A.Transaction.WithNonce((ulong)i + 1).SignedAndResolved().TestObject;
        }

        ReturnStoredEncoding(blobTx);
        _transactionPool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txs.First(tx => tx.Hash!.ValueHash256 == x.ArgAt<ValueHash256>(0));
                return true;
            });

        using Eth65GetPooledTransactionsMessage request = new(txs.Select(static tx => tx.Hash!.ValueHash256).ToArray().ToPooledList());
        using Eth65PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);
        using Eth65PooledTransactionsMessage decodedResponse = new(txs.ToPooledList());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Transactions, Has.Count.EqualTo(txs.Length - 1));
            Assert.That(response.EncodedTransactions, Has.Count.EqualTo(1));
            Assert.That(response.ToString(), Is.EqualTo($"PooledTransactionsMessage({txs.Length})"));
            Assert.That(Serialize(response), Is.EqualTo(Serialize(decodedResponse)));
        }
    }

    [Test]
    public async Task Should_look_up_the_blob_pool_once_for_each_requested_hash()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
        _transactionPool.TryGetPendingTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });

        using Eth65GetPooledTransactionsMessage request = new(new ArrayPoolList<ValueHash256>(1) { tx.Hash! });
        using Eth65PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Transactions, Has.Count.EqualTo(1));
            _transactionPool.Received(1).TryGetPendingBlobCellMaskAndNetworkForm(tx.Hash!, out Arg.Any<BlobCellMask>(), out Arg.Any<IOwnedReadOnlyList<byte>>());
            _transactionPool.DidNotReceive().TryGetPendingBlobCellMask(Arg.Any<ValueHash256>(), out Arg.Any<BlobCellMask>());
        }
    }

    [Test]
    public async Task Should_serve_full_blob_tx_from_memory_when_it_has_no_stored_encoding()
    {
        Transaction blobTx = BuildFullBlobTransaction();
        _transactionPool.TryGetPendingBlobCellMaskAndNetworkForm(blobTx.Hash!, out Arg.Any<BlobCellMask>(), out Arg.Any<IOwnedReadOnlyList<byte>>())
            .Returns(x =>
            {
                x[1] = BlobCellMask.Full;
                x[2] = null;
                return true;
            });
        _transactionPool.TryGetPendingTransaction(blobTx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = blobTx;
                return true;
            });

        using Eth65GetPooledTransactionsMessage request = new(new ArrayPoolList<ValueHash256>(1) { blobTx.Hash! });
        using Eth65PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Transactions, Is.EqualTo(new[] { blobTx }));
            Assert.That(response.EncodedTransactions, Is.Null);
        }
    }

    [Test]
    public async Task Should_count_stored_blob_tx_encodings_against_the_soft_limit()
    {
        const int transactionCount = 32;
        Transaction blobTx = BuildFullBlobTransaction();
        List<ArrayPoolList<byte>> encodings = ReturnStoredEncoding(blobTx, forAnyHash: true);
        int servedCount = (int)(2.MiB / blobTx.GetLength());

        ArrayPoolList<ValueHash256> hashes = [with(transactionCount)];
        for (int i = 0; i < transactionCount; i++)
        {
            hashes.Add(new Hash256(i.ToString("X64")));
        }

        using Eth65GetPooledTransactionsMessage request = new(hashes);
        Eth65PooledTransactionsMessage response = await _handler.FulfillPooledTransactionsRequest(request, CancellationToken.None);

        // A disposed ArrayPoolList returns its array and reports no items. The encoding that does not fit is read,
        // then dropped, and the lookup stops there.
        using (response)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Transactions, Is.Empty);
            Assert.That(response.EncodedTransactions, Has.Count.EqualTo(servedCount));
            Assert.That(encodings, Has.Count.EqualTo(servedCount + 1));
            Assert.That(encodings.Take(servedCount).Select(static e => e.Count), Has.All.Not.Zero, "served encodings are not disposed");
            Assert.That(encodings[^1].Count, Is.Zero, "encoding over the soft limit is disposed");
            _transactionPool.DidNotReceive().TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>());
        }

        Assert.That(encodings.Select(static e => e.Count), Has.All.Zero, "served encodings are disposed with the response");
    }

    private List<ArrayPoolList<byte>> ReturnStoredEncoding(Transaction blobTx, bool forAnyHash = false)
    {
        byte[] encoding = TxDecoder.Instance.Encode(blobTx, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping);
        List<ArrayPoolList<byte>> encodings = [];
        _transactionPool.TryGetPendingBlobCellMaskAndNetworkForm(Arg.Any<ValueHash256>(), out Arg.Any<BlobCellMask>(), out Arg.Any<IOwnedReadOnlyList<byte>>())
            .Returns(x =>
            {
                bool found = forAnyHash || x.ArgAt<ValueHash256>(0) == blobTx.Hash!.ValueHash256;
                ArrayPoolList<byte>? networkForm = found ? [with(encoding)] : null;
                if (networkForm is not null) encodings.Add(networkForm);
                x[1] = found ? BlobCellMask.Full : default;
                x[2] = networkForm;
                return found;
            });
        return encodings;
    }

    private static byte[] Serialize(Eth65PooledTransactionsMessage message)
    {
        Eth65PooledTransactionsMessageSerializer serializer = new();
        byte[] bytes = new byte[serializer.GetLength(message)];
        Assert.That(serializer.Serialize(bytes, message), Is.EqualTo(bytes.Length));
        return bytes;
    }

    [Test]
    public void Should_register_requested_hashes_for_timeout_retry()
    {
        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { (byte)TxType.EIP1559 },
            new ArrayPoolList<int>(1) { 100 },
            new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA });

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        _transactionPool.Received(1).NotifyAboutTx(TestItem.KeccakA, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
    }

    [Test]
    public void Should_skip_known_hashes_in_mixed_announcement()
    {
        ValueHash256 known = TestItem.KeccakA.ValueHash256;
        _transactionPool.IsKnown(in known).Returns(true);
        using NewPooledTransactionHashesMessage68 message = new(
            new ArrayPoolList<byte>(2) { (byte)TxType.EIP1559, (byte)TxType.EIP1559 },
            new ArrayPoolList<int>(2) { 100, 100 },
            new ArrayPoolList<ValueHash256>(2) { TestItem.KeccakA, TestItem.KeccakB });

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m =>
            m.EthMessage.Hashes.Count == 1 && m.EthMessage.Hashes[0] == TestItem.KeccakB));
        _transactionPool.DidNotReceive().NotifyAboutTx(TestItem.KeccakA, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
    }

    [TestCase(0, (byte)TxType.Legacy)]
    [TestCase(100, (byte)TxType.DepositTx)]
    [TestCase(100, byte.MaxValue)]
    public void Should_request_when_announcement_shape_is_invalid(int size, byte type)
    {
        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { type },
            new ArrayPoolList<int>(1) { size },
            new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA });

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.DidNotReceive().DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
        _transactionPool.DidNotReceive().NotifyAboutTx(
            Arg.Any<ValueHash256>(),
            Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());

        _session.ClearReceivedCalls();
        _handler.HandleMessages([TestItem.KeccakA]);

        _session.Received(1).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
    }

    [Test]
    public void Should_validate_retained_shape_for_unrequestable_type([Values] bool correlated)
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).SignedAndResolved().WithHash(TestItem.KeccakA).TestObject;
        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { (byte)TxType.Blob },
            new ArrayPoolList<int>(1) { tx.GetLength() },
            new ArrayPoolList<ValueHash256>(1) { tx.Hash });
        AssertResponseForUnrequestableAnnouncement(tx, hashesMsg, correlated);
    }

    [TestCase(TransactionsMessage.MaxPacketSize / 2 + 1)]
    [TestCase(TransactionsMessage.MaxPacketSize + 1)]
    public void Should_preserve_first_valid_shape_for_conservative_batched_retry(int largeSize)
    {
        _transactionPool.NotifyAboutTx(
                Arg.Any<ValueHash256>(),
                Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.Delayed);

        HandleIncomingStatusMessage();
        Announce(TestItem.KeccakA, largeSize);
        Announce(TestItem.KeccakA, 1);
        Announce(TestItem.KeccakB, largeSize);
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
        _transactionPool.Received(2).NotifyAboutTx(TestItem.KeccakA.ValueHash256, _handler);
        _transactionPool.Received(1).NotifyAboutTx(TestItem.KeccakB.ValueHash256, _handler);
        _session.ClearReceivedCalls();
        _transactionPool.ClearReceivedCalls();

        _handler.HandleMessages([TestItem.KeccakA, TestItem.KeccakB]);

        _session.Received(2).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == 1));
        _transactionPool.DidNotReceive().NotifyAboutTx(
            Arg.Any<ValueHash256>(),
            Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());

        void Announce(Hash256 hash, int size)
        {
            using NewPooledTransactionHashesMessage68 message = new(
                new ArrayPoolList<byte>(1) { (byte)TxType.EIP1559 },
                new ArrayPoolList<int>(1) { size },
                new ArrayPoolList<ValueHash256>(1) { hash });
            HandleZeroMessage(message, Eth68MessageCode.NewPooledTransactionHashes);
        }
    }

    [Test]
    public void Should_size_receipts_with_bloom_for_soft_message_size_limit()
    {
        const int count = 512;
        using GetReceiptsMessage msg = new(1111, new(Enumerable.Repeat(Keccak.Zero, count).ToPooledList(count)));
        TxReceipt[] receipts = SoftLimitTestHelper.CreateLogHeavyReceipts(100);
        int expectedCount = SoftLimitTestHelper.CountReceiptBlocksWithinSoftLimit(
            ReceiptsServingTestHelper.Encode(receipts, Rlp.GetDecoderOrThrow<TxReceipt>(), _specProvider).Length, count);
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(receipts);
        _syncManager.ServeReceipts(_specProvider);

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth66MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage>(r => r.EthMessage.TxReceipts.Count == expectedCount));
    }

    private void HandleIncomingStatusMessage()
    {
        StatusMessage statusMsg = new();
        statusMsg.GenesisHash = _genesisBlock.Hash;
        statusMsg.BestHash = _genesisBlock.Hash;

        using DisposableByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg).AsDisposable();
        statusPacket.ReadByte();
        _handler.HandleMessage(new NettyBufferPacket(statusPacket) { PacketType = 0 });
    }

    private Eth68ProtocolHandler CreateHandler(ITxPoolConfig txPoolConfig, IChainHeadSpecProvider? specProvider = null,
        IBackgroundTaskScheduler? backgroundTaskScheduler = null, TimeProvider? timeProvider = null) =>
        new(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            backgroundTaskScheduler ?? RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            txPoolConfig,
            specProvider ?? Substitute.For<IChainHeadSpecProvider>(),
            _txGossipPolicy, timeProvider);

    private static Transaction BuildFullBlobTransaction() => Build.A.Transaction
        .WithNonce(0UL)
        .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
        .SignedAndResolved()
        .TestObject;

    private static Transaction BuildSparseBlobTransaction() => BuildSparseBlobTransaction(BuildFullBlobTransaction());

    private static Transaction BuildSparseBlobTransaction(Transaction tx)
    {
        Transaction sparseTx = new();
        tx.CopyTo(sparseTx, copyHash: true);
        sparseTx.NetworkWrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper! with { Blobs = [] };
        sparseTx.ClearLengthCache();
        return sparseTx;
    }

    private void SendTransaction(Transaction tx, bool sendBatch)
    {
        if (sendBatch)
        {
            _handler.SendNewTransactions([tx], sendFullTx: false);
        }
        else
        {
            _handler.SendNewTransaction(tx);
        }
    }

    private void ReplaceHandler(ITxPoolConfig txPoolConfig, IBackgroundTaskScheduler? backgroundTaskScheduler = null, TimeProvider? timeProvider = null)
    {
        // Complete the outgoing handler's handshake so its pending protocol-init timeout cannot disconnect the shared session substitute.
        HandleIncomingStatusMessage();
        _handler.Dispose();
        _handler = CreateHandler(txPoolConfig, backgroundTaskScheduler: backgroundTaskScheduler, timeProvider: timeProvider);
        _handler.Init();
    }

    private IRecyclableTxPool ReplaceWithRecyclableTxPool()
    {
        RecyclableTxPool recyclablePool = Substitute.For<RecyclableTxPool, ITxPool>();
        _transactionPool = (ITxPool)recyclablePool;
        ReplaceHandler(Substitute.For<ITxPoolConfig>());
        HandleIncomingStatusMessage();
        return recyclablePool;
    }

    /// <summary>A pool that settles and submits owned transactions without tracking request retries.</summary>
    /// <remarks>
    /// Castle.Core cannot proxy members with span parameters, so the retry members are implemented here
    /// non-virtually and only the submission members are substituted.
    /// </remarks>
    public abstract class RecyclableTxPool : IRecyclableTxPool
    {
        public abstract AcceptTxResult SubmitOwnedTx(Transaction tx, object? peer, out bool canRecycle);

        public abstract bool TrySubmitKnown(in ValueHash256 hash);

        void IRecyclableTxPool.CaptureRequestTokens(ReadOnlySpan<ValueHash256> hashes,
            IMessageHandler<PooledTransactionRequestMessage> sender, Span<RetryRequestToken<ValueHash256>> tokens)
        {
        }

        void IRecyclableTxPool.CompleteRequest(RetryRequestConfirmation? confirmation, bool sent)
        {
        }

        AcceptTxResult IRecyclableTxPool.SubmitOwnedRequestedTx(Transaction tx, IMessageHandler<PooledTransactionRequestMessage> sender,
            in RetryRequestToken<ValueHash256> token, out bool canRecycle) => SubmitOwnedTx(tx, sender, out canRecycle);
    }

    private void AssertResponseForUnrequestableAnnouncement(Transaction tx, NewPooledTransactionHashesMessage68 announcement, bool correlated)
    {
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth68MessageCode.NewPooledTransactionHashes);
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());

        ArrayPoolList<Transaction> transactions = [with(1)];
        using PooledTransactionsMessage response = new(1111, new(transactions));
        if (correlated)
        {
            _handler.HandleMessage(PooledTransactionRequestMessage.New(tx.Hash!));
            response.RequestId = LastPooledRequestId();
        }
        transactions.Add(tx);
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        if (correlated)
            _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
        else
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }

    private long LastPooledRequestId() => _session.ReceivedCalls()
        .SelectMany(static call => call.GetArguments().OfType<GetPooledTransactionsMessage>()).Last().RequestId;

    private void HandleZeroMessage<T>(T msg, byte messageCode) where T : P2PMessage
    {
        using DisposableByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg).AsDisposable();
        getBlockHeadersPacket.ReadByte();
        _handler.HandleMessage(new NettyBufferPacket(getBlockHeadersPacket) { PacketType = messageCode });
    }

    private void RecreateHandlerWithBlobSupport()
    {
        _handler.Dispose();
        ITxPoolConfig txPoolConfig = Substitute.For<ITxPoolConfig>();
        txPoolConfig.BlobsSupport.Returns(BlobsSupportMode.InMemory);
        _handler = CreateHandler(txPoolConfig);
        _handler.Init();
    }

    private void GenerateLists(int txCount, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<ValueHash256> hashes)
    {
        GenerateTxLists(txCount, out types, out sizes, out hashes, out ArrayPoolList<Transaction> txs);
        txs.Dispose();
    }

    private void GenerateTxLists(int txCount, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<ValueHash256> hashes, out ArrayPoolList<Transaction> txs)
    {
        TxDecoder txDecoder = TxDecoder.Instance;
        types = [with(txCount)];
        sizes = [with(txCount)];
        hashes = [with(txCount)];
        txs = [with(txCount)];

        for (int i = 0; i < txCount; ++i)
        {
            Transaction tx = Build.A.Transaction.WithType((TxType)(i % 3)).SignedAndResolved().WithData(new byte[i])
                .WithHash(i % 2 == 0 ? TestItem.KeccakA : TestItem.KeccakB).TestObject;

            types.Add((byte)tx.Type);
            sizes.Add(txDecoder.GetLength(tx, RlpBehaviors.None));
            hashes.Add(tx.Hash);
            txs.Add(tx);
        }
    }
}
