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
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using PooledTransactionsMessage65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;
using PooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;

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
            new EthereumEcdsa(TestBlockchainIds.ChainId),
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
        Transaction unresolved = Build.A.Transaction.WithNonce(9).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
        unresolved.SenderAddress = null;

        _handler.SendNewTransactions([first, unresolved, second], sendFullTx: false);

        NewPooledTransactionHashesMessage73 message = _deliveredMessages.OfType<NewPooledTransactionHashesMessage73>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.Hashes, Is.EqualTo(new[] { first.Hash!.ValueHash256, second.Hash!.ValueHash256 }));
            Assert.That(message.Sources, Is.EqualTo(new[] { TestItem.AddressA, TestItem.AddressB }));
            Assert.That(message.Nonces, Is.EqualTo(new[] { 5UL, 7UL }));
            Assert.That(message.CellMask, Is.EqualTo(BlobCellMask.Empty.ToBytes()));
        }
    }

    [TestCase(false, false, TestName = "Matching announcement: submitted")]
    [TestCase(true, false, TestName = "Wrong announced source: disconnected")]
    [TestCase(false, true, TestName = "Wrong announced nonce: disconnected")]
    public void should_disconnect_when_pooled_tx_does_not_match_announced_source_and_nonce(bool wrongSource, bool wrongNonce)
    {
        Transaction tx = Build.A.Transaction.WithNonce(5).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        bool matches = !wrongSource && !wrongNonce;

        using NewPooledTransactionHashesMessage73 announcement = new(
            [(byte)tx.Type],
            [tx.GetLength()],
            [tx.Hash!.ValueHash256],
            BlobCellMask.Empty.ToBytes(),
            [wrongSource ? TestItem.AddressB : TestItem.AddressA],
            [wrongNonce ? 6UL : 5UL]);
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        long requestId = _deliveredMessages.OfType<GetPooledTransactionsMessage>()
            .Single(m => m.EthMessage.Hashes.Contains(tx.Hash!.ValueHash256))
            .RequestId;
        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { tx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            _transactionPool.Received(matches ? 1 : 0).SubmitTx(
                Arg.Is<Transaction>(t => t.Hash == tx.Hash && t.SenderAddress == TestItem.AddressA),
                Arg.Any<TxHandlingOptions>());
            _session.Received(matches ? 0 : 1).InitiateDisconnect(
                DisconnectReason.BackgroundTaskFailure,
                "pooled tx does not match its announced source or nonce");
        }
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
