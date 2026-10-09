// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
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
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using PooledTransactionsMessage65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;
using PooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;
using TransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.TransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class Eth73ProtocolHandlerTests
{
    private ISession _session = null!;
    private IMessageSerializationService _svc = null!;
    private ISyncServer _syncManager = null!;
    private ITxPool _transactionPool = null!;
    private IChainHeadSpecProvider _specProvider = null!;
    private Block _genesisBlock = null!;
    private SparseBlobPoolPeerRegistry _sparseBlobPoolPeerRegistry = null!;
    private Eth73ProtocolHandler _handler = null!;
    private List<P2PMessage> _deliveredMessages = null!;
    private CompositeDisposable _disposables = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = Substitute.For<IChainHeadSpecProvider>();
        _svc = Build.A.SerializationService().WithEth73(_specProvider).TestObject;

        _disposables = [];
        _deliveredMessages = [];
        _session = Substitute.For<ISession>();
        _session.Node.Returns(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303)));
        _session.When(static s => s.DeliverMessage(Arg.Any<P2PMessage>()))
            .Do(c =>
            {
                P2PMessage message = c.Arg<P2PMessage>();
                _deliveredMessages.Add(message);
                message.AddTo(_disposables);
            });

        _syncManager = Substitute.For<ISyncServer>();
        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncManager.Head.Returns(_genesisBlock.Header);
        _syncManager.Genesis.Returns(_genesisBlock.Header);
        _transactionPool = Substitute.For<ITxPool>();

        ITxPoolConfig txPoolConfig = Substitute.For<ITxPoolConfig>();
        txPoolConfig.BlobsSupport.Returns(BlobsSupportMode.InMemory);
        txPoolConfig.SparseBlobProviderProbabilityPercent.Returns(15);
        ITxGossipPolicy txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
        BlobCustodyTracker blobCustodyTracker = new();
        blobCustodyTracker.Update(BlobCellMask.Full);
        _sparseBlobPoolPeerRegistry = new SparseBlobPoolPeerRegistry(_transactionPool, blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance);

        _handler = new Eth73ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            Substitute.For<IGossipPolicy>(),
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            txPoolConfig,
            _specProvider,
            blobCustodyTracker,
            _sparseBlobPoolPeerRegistry,
            txGossipPolicy);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
        _session.Dispose();
        _syncManager.Dispose();
        _sparseBlobPoolPeerRegistry.Dispose();
        _disposables.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_handler.ProtocolCode, Is.EqualTo("eth"));
            Assert.That(_handler.Name, Is.EqualTo("eth73"));
            Assert.That(_handler.ProtocolVersion, Is.EqualTo(73));
            Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(22));
        }
    }

    [Test]
    public void should_announce_source_and_nonce_of_each_transaction()
    {
        Transaction first = Build.A.Transaction.WithNonce(5).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction second = Build.A.Transaction.WithNonce(7).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        _handler.SendNewTransactions([first, second], sendFullTx: false);

        NewPooledTransactionHashesMessage73 message = _deliveredMessages.OfType<NewPooledTransactionHashesMessage73>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.Hashes, Is.EqualTo(new[] { first.Hash!.ValueHash256, second.Hash!.ValueHash256 }));
            Assert.That(message.Sources, Is.EqualTo(new[] { TestItem.AddressA, TestItem.AddressB }));
            Assert.That(message.Nonces, Is.EqualTo(new[] { 5UL, 7UL }));
            Assert.That(message.CellMask, Is.EqualTo(BlobCellMask.Empty.ToBytes()));
        }
    }

    [TestCase(Delivery.Matching, true, false, TestName = "Matching announcement: submitted")]
    [TestCase(Delivery.WrongNonce, false, true, TestName = "Wrong nonce: disconnected before the pool")]
    [TestCase(Delivery.WrongSourceResolvedByPool, true, true, TestName = "Wrong source resolved by the pool: disconnected after submission")]
    [TestCase(Delivery.WrongSourceRejectedCheaply, true, false, TestName = "Wrong source rejected before sender recovery: no recovery, no disconnect")]
    [TestCase(Delivery.WrongFrameTxSender, false, true, TestName = "Wrong explicit frame tx sender: disconnected before the pool")]
    [TestCase(Delivery.MatchingFrameTx, true, false, TestName = "Matching frame tx: submitted")]
    public void should_check_delivered_tx_against_announced_source_and_nonce(Delivery delivery, bool submitted, bool disconnected)
    {
        bool frameTx = delivery is Delivery.WrongFrameTxSender or Delivery.MatchingFrameTx;
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(frameTx);
        _specProvider.GetCurrentHeadSpec().Returns(spec);
        Transaction tx = (frameTx ? Build.A.Transaction.WithType(TxType.FrameTx).WithTo(TestItem.AddressB) : Build.A.Transaction)
            .WithNonce(5)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithGasLimit(100_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        // The substitute pool stands in for MalformedTxFilter: it resolves the sender, unless it rejects the transaction first.
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(c =>
        {
            Transaction submittedTx = c.Arg<Transaction>();
            if (delivery == Delivery.WrongSourceRejectedCheaply)
            {
                Assert.That(submittedTx.SenderAddress, Is.Null, "the handler must not recover the sender before the pool");
                return AcceptTxResult.FeeTooLow;
            }

            submittedTx.SenderAddress ??= TestItem.AddressA;
            return AcceptTxResult.Accepted;
        });

        bool wrongSource = delivery is Delivery.WrongSourceResolvedByPool or Delivery.WrongSourceRejectedCheaply or Delivery.WrongFrameTxSender;
        using NewPooledTransactionHashesMessage73 announcement = new(
            [(byte)tx.Type],
            [tx.GetLength()],
            [tx.Hash!.ValueHash256],
            BlobCellMask.Empty.ToBytes(),
            [wrongSource ? TestItem.AddressB : TestItem.AddressA],
            [delivery == Delivery.WrongNonce ? 6UL : 5UL]);
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        long requestId = _deliveredMessages.OfType<GetPooledTransactionsMessage>()
            .Single(m => m.EthMessage.Hashes.Contains(tx.Hash!.ValueHash256))
            .RequestId;
        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { tx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            _transactionPool.Received(submitted ? 1 : 0).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            _session.Received(disconnected ? 1 : 0).InitiateDisconnect(
                DisconnectReason.BreachOfProtocol,
                "pooled tx does not match its announced source or nonce");
        }
    }

    [TestCase(false, TestName = "Sparse blob tx with matching source: not disconnected")]
    [TestCase(true, TestName = "Sparse blob tx with wrong source: disconnected after sampling validation")]
    public void should_check_sparse_blob_tx_against_announced_source(bool wrongSource)
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(5)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        tx.NetworkWrapper = wrapper with { Blobs = [], CellMask = default, Cells = null };
        tx.ClearLengthCache();
        tx.SenderAddress = null;

        // The substitute pool stands in for MalformedTxFilter, which sampling validation runs.
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(c =>
        {
            c.Arg<Transaction>().SenderAddress ??= TestItem.AddressA;
            return AcceptTxResult.Accepted;
        });

        using NewPooledTransactionHashesMessage73 announcement = new(
            [(byte)tx.Type],
            [tx.GetLength()],
            [tx.Hash!.ValueHash256],
            BlobCellMask.Full.ToBytes(),
            [wrongSource ? TestItem.AddressB : TestItem.AddressA],
            [5UL]);
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        long requestId = _deliveredMessages.OfType<GetPooledTransactionsMessage>()
            .Single(m => m.EthMessage.Hashes.Contains(tx.Hash!.ValueHash256))
            .RequestId;
        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { tx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            _transactionPool.Received(1).ValidateTxForBlobSampling(Arg.Any<Transaction>());
            _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            _session.Received(wrongSource ? 1 : 0).InitiateDisconnect(
                DisconnectReason.BreachOfProtocol,
                "pooled tx does not match its announced source or nonce");
        }
    }

    [Test]
    public void should_not_remember_announcements_that_are_not_requested()
    {
        Transaction tx = Build.A.Transaction.WithNonce(5).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.Delayed);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(c =>
        {
            c.Arg<Transaction>().SenderAddress ??= TestItem.AddressA;
            return AcceptTxResult.Accepted;
        });

        using NewPooledTransactionHashesMessage73 announcement = new(
            [(byte)tx.Type],
            [tx.GetLength()],
            [tx.Hash!.ValueHash256],
            BlobCellMask.Empty.ToBytes(),
            [TestItem.AddressB],
            [6UL]);
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        using TransactionsMessage broadcast = new(new[] { tx }.ToPooledList());
        HandleZeroMessage(broadcast, Eth62MessageCode.Transactions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage>(), Is.Empty);
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    public enum Delivery
    {
        Matching,
        WrongNonce,
        WrongSourceResolvedByPool,
        WrongSourceRejectedCheaply,
        WrongFrameTxSender,
        MatchingFrameTx,
    }

    private void HandleIncomingStatusMessage()
    {
        using StatusMessage69 statusMsg = new() { ProtocolVersion = 73, GenesisHash = _genesisBlock.Hash!, LatestBlockHash = _genesisBlock.Hash! };

        using DisposableByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg).AsDisposable();
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
    {
        using DisposableByteBuffer packet = _svc.ZeroSerialize(msg).AsDisposable();
        packet.ReadByte();
        _handler.HandleMessage(new ZeroPacket(packet) { PacketType = (byte)messageCode });
    }
}
