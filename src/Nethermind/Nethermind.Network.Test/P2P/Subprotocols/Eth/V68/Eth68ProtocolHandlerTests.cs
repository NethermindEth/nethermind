// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Net;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V68;

public class Eth68ProtocolHandlerTests
{
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
        _handler = new Eth68ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            Substitute.For<ITxPoolConfig>(),
            Substitute.For<ISpecProvider>(),
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
        Assert.That(_handler.Name, Is.EqualTo("eth68"));
        Assert.That(_handler.ProtocolVersion, Is.EqualTo(68));
        Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(17));
        Assert.That(_handler.IncludeInTxPool, Is.True);
        Assert.That(_handler.ClientId, Is.EqualTo(_session.Node?.ClientId));
        Assert.That(_handler.HeadHash, Is.Null);
        Assert.That(_handler.HeadNumber, Is.EqualTo(0));
    }

    [Test]
    public void Can_handle_NewPooledTransactions_message([Values(0, 1, 2, 100)] int txCount, [Values(true, false)] bool canGossipTransactions)
    {
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(canGossipTransactions);

        GenerateLists(txCount, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<Hash256> hashes);

        using NewPooledTransactionHashesMessage68 msg = new(types, sizes, hashes);

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(canGossipTransactions && txCount != 0 ? 1 : 0).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Should_throw_when_sizes_do_not_match(bool removeSize)
    {
        GenerateLists(4, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<Hash256> hashes);

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


    [Test]
    public void Should_disconnect_if_tx_size_is_wrong()
    {
        GenerateTxLists(4, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<Hash256> hashes, out ArrayPoolList<Transaction> txs);
        sizes[0] += 10;
        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        using PooledTransactionsMessage txsMsg = new(1111, new(txs));

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);

        _session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure, "invalid pooled tx type or size");
    }


    [Test]
    public void Should_disconnect_if_tx_type_is_wrong()
    {
        GenerateTxLists(4, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<Hash256> hashes, out ArrayPoolList<Transaction> txs);
        types[0]++;
        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        using PooledTransactionsMessage txsMsg = new(1111, new(txs));

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);

        _session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure, "invalid pooled tx type or size");
    }

    [Test]
    public void Should_process_huge_transaction()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithData(new byte[2 * MemorySizes.MiB])
            .WithHash(TestItem.KeccakA).TestObject;

        using NewPooledTransactionHashesMessage68 msg = new(new ArrayPoolList<byte>(1) { (byte)tx.Type },
            new ArrayPoolList<int>(1) { tx.GetLength() }, new ArrayPoolList<Hash256>(1) { tx.Hash });

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

    [Test]
    public void should_divide_GetPooledTransactionsMessage_if_max_message_size_is_exceeded([Values(0, 1, 100, 10_000)] int numberOfTransactions, [Values(97, TransactionsMessage.MaxPacketSize, 200_000)] int sizeOfOneTx)
    {
        _handler = new Eth68ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            Substitute.For<ITxPoolConfig>(),
            Substitute.For<ISpecProvider>(),
            _txGossipPolicy);

        int maxNumberOfTxsInOneMsg = int.Min(sizeOfOneTx <= TransactionsMessage.MaxPacketSize ? TransactionsMessage.MaxPacketSize / sizeOfOneTx : 256, 256);
        int messagesCount = numberOfTransactions / maxNumberOfTxsInOneMsg + (numberOfTransactions % maxNumberOfTxsInOneMsg == 0 ? 0 : 1);

        using ArrayPoolList<byte> types = new(numberOfTransactions);
        using ArrayPoolList<int> sizes = new(numberOfTransactions);
        using ArrayPoolList<Hash256> hashes = new(numberOfTransactions);

        for (int i = 0; i < numberOfTransactions; i++)
        {
            types.Add(0);
            sizes.Add(sizeOfOneTx);
            hashes.Add(new Hash256(i.ToString("X64")));
        }

        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(messagesCount).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.EthMessage.Hashes.Count == maxNumberOfTxsInOneMsg || m.EthMessage.Hashes.Count == numberOfTransactions % maxNumberOfTxsInOneMsg));
    }

    [Test]
    public void Should_request_oversized_announced_transactions_together()
    {
        using ArrayPoolList<byte> types = new(3) { 0, 0, 0 };
        using ArrayPoolList<int> sizes = new(3) { 200_000, 200_000, 200_000 };
        using ArrayPoolList<Hash256> hashes = new(3)
        {
            TestItem.KeccakA,
            TestItem.KeccakB,
            TestItem.KeccakC
        };
        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m =>
            m.EthMessage.Hashes.Count == 3 &&
            m.EthMessage.Hashes[0] == TestItem.KeccakA &&
            m.EthMessage.Hashes[1] == TestItem.KeccakB &&
            m.EthMessage.Hashes[2] == TestItem.KeccakC));
    }

    [Test]
    public void Should_register_requested_hashes_for_timeout_retry()
    {
        using NewPooledTransactionHashesMessage68 hashesMsg = new(
            new ArrayPoolList<byte>(1) { (byte)TxType.EIP1559 },
            new ArrayPoolList<int>(1) { 100 },
            new ArrayPoolList<Hash256>(1) { TestItem.KeccakA });

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);

        _transactionPool.Received(2).NotifyAboutTx(TestItem.KeccakA, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
    }

    [Test]
    public void Should_track_typed_new_pooled_transaction_metrics_by_peer_client()
    {
        const string client = "reth";
        _session.Node.ClientId = "reth/v1.5.0/linux";
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

        long announcedBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsAnnouncedByClient, client);
        long requestedBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClient, client);
        long returnedBefore = GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsReturnedByClient, client);

        GenerateTxLists(2, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<Hash256> hashes, out ArrayPoolList<Transaction> txs);
        using NewPooledTransactionHashesMessage68 hashesMsg = new(types, sizes, hashes);
        using PooledTransactionsMessage txsMsg = new(1111, new(txs));

        HandleIncomingStatusMessage();
        HandleZeroMessage(hashesMsg, Eth68MessageCode.NewPooledTransactionHashes);
        HandleZeroMessage(txsMsg, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsAnnouncedByClient, client), Is.EqualTo(announcedBefore + 2));
            Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsRequestedByClient, client), Is.EqualTo(requestedBefore + 2));
            Assert.That(GetMetricValue(Nethermind.TxPool.Metrics.NewPooledTransactionsReturnedByClient, client), Is.EqualTo(returnedBefore + 2));
        }
    }

    private static long GetMetricValue(ConcurrentDictionary<string, long> metric, string client)
    {
        metric.TryGetValue(client, out long value);
        return value;
    }

    private void HandleIncomingStatusMessage()
    {
        StatusMessage statusMsg = new();
        statusMsg.GenesisHash = _genesisBlock.Hash;
        statusMsg.BestHash = _genesisBlock.Hash;

        using DisposableByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg).AsDisposable();
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, byte messageCode) where T : MessageBase
    {
        using DisposableByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg).AsDisposable();
        getBlockHeadersPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket) { PacketType = messageCode });
    }

    private void GenerateLists(int txCount, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<Hash256> hashes)
    {
        GenerateTxLists(txCount, out types, out sizes, out hashes, out ArrayPoolList<Transaction> txs);
        txs.Dispose();
    }

    private void GenerateTxLists(int txCount, out ArrayPoolList<byte> types, out ArrayPoolList<int> sizes, out ArrayPoolList<Hash256> hashes, out ArrayPoolList<Transaction> txs)
    {
        TxDecoder txDecoder = TxDecoder.Instance;
        types = new(txCount);
        sizes = new(txCount);
        hashes = new(txCount);
        txs = new(txCount);

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
