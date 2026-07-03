// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
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
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using PooledTransactionsMessage65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;
using PooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V72;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class Eth72ProtocolHandlerTests
{
    private ISession _session = null!;
    private IMessageSerializationService _svc = null!;
    private ISyncServer _syncManager = null!;
    private ITxPool _transactionPool = null!;
    private IGossipPolicy _gossipPolicy = null!;
    private ISpecProvider _specProvider = null!;
    private ITxPoolConfig _txPoolConfig = null!;
    private Block _genesisBlock = null!;
    private Eth72ProtocolHandler _handler = null!;
    private ITxGossipPolicy _txGossipPolicy = null!;
    private ITimerFactory _timerFactory = null!;
    private CompositeDisposable _disposables = null!;
    private BlobCustodyTracker _blobCustodyTracker = null!;
    private SparseBlobPoolPeerRegistry _sparseBlobPoolPeerRegistry = null!;
    private List<P2PMessage> _deliveredMessages = null!;
    private IProtectedPrivateKey _nodeKey = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = Substitute.For<ISpecProvider>();
        _svc = Build.A.SerializationService().WithEth72(_specProvider).TestObject;

        NetworkDiagTracer.IsEnabled = true;

        _disposables = [];
        _deliveredMessages = [];
        _session = Substitute.For<ISession>();
        Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
        _session.Node.Returns(node);
        _session.When(static s => s.DeliverMessage(Arg.Any<P2PMessage>()))
            .Do(c =>
            {
                P2PMessage message = c.Arg<P2PMessage>();
                _deliveredMessages.Add(message);
                message.AddTo(_disposables);
            });

        _syncManager = Substitute.For<ISyncServer>();
        _transactionPool = Substitute.For<ITxPool>();
        _gossipPolicy = Substitute.For<IGossipPolicy>();
        _txPoolConfig = Substitute.For<ITxPoolConfig>();
        _txPoolConfig.BlobsSupport.Returns(BlobsSupportMode.InMemory);
        _txPoolConfig.SparseBlobProviderProbabilityPercent.Returns(15);
        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncManager.Head.Returns(_genesisBlock.Header);
        _syncManager.Genesis.Returns(_genesisBlock.Header);
        _syncManager.LowestBlock.Returns(0UL);
        _timerFactory = Substitute.For<ITimerFactory>();
        _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
        _blobCustodyTracker = new BlobCustodyTracker();
        _sparseBlobPoolPeerRegistry = new SparseBlobPoolPeerRegistry(_transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        _nodeKey = Substitute.For<IProtectedPrivateKey>();
        _nodeKey.PublicKey.Returns(TestItem.PublicKeyB);

        _handler = new Eth72ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            _txPoolConfig,
            _specProvider,
            _blobCustodyTracker,
            _nodeKey,
            _sparseBlobPoolPeerRegistry,
            _txGossipPolicy);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _handler?.Dispose();
        _session?.Dispose();
        _syncManager?.Dispose();
        _sparseBlobPoolPeerRegistry?.Dispose();
        _disposables.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        Assert.That(_handler.ProtocolCode, Is.EqualTo("eth"));
        Assert.That(_handler.Name, Is.EqualTo("eth72"));
        Assert.That(_handler.ProtocolVersion, Is.EqualTo(72));
        Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(22));
        Assert.That(_handler.IncludeInTxPool, Is.True);
        Assert.That(_handler.ClientId, Is.EqualTo(_session.Node?.ClientId));
        Assert.That(_handler.HeadHash, Is.Null);
        Assert.That(_handler.HeadNumber, Is.EqualTo(0));
    }

    [Test]
    public void should_send_sparse_blob_tx_announcement_with_cell_mask()
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(0UL)
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .SignedAndResolved()
            .TestObject;

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        BlobCellMask cellMask = BlobCellMask.FromIndices([1, 3]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out byte[][] cells), Is.True);

        tx.NetworkWrapper = wrapper with
        {
            Blobs = [],
            CellMask = cellMask,
            Cells = cells,
        };

        _handler.SendNewTransaction(tx);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(cellMask.ToBytes())));
    }

    [Test]
    public void should_send_non_blob_tx_announcement_with_empty_fixed_cell_mask()
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        _handler.SendNewTransactions([tx], sendFullTx: false);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(BlobCellMask.Empty.ToBytes())));
    }

    [Test]
    public void should_reannounce_blob_tx_when_available_cell_mask_expands()
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(0UL)
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .SignedAndResolved()
            .TestObject;

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        BlobCellMask firstMask = BlobCellMask.FromIndices([1]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, firstMask, out byte[][] firstCells), Is.True);
        BlobCellMask expandedMask = BlobCellMask.FromIndices([1, 3]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, expandedMask, out byte[][] expandedCells), Is.True);

        tx.NetworkWrapper = wrapper with
        {
            Blobs = [],
            CellMask = firstMask,
            Cells = firstCells,
        };
        tx.ClearLengthCache();
        _handler.SendNewTransaction(tx);

        tx.NetworkWrapper = wrapper with
        {
            Blobs = [],
            CellMask = expandedMask,
            Cells = expandedCells,
        };
        tx.ClearLengthCache();
        _handler.SendNewTransaction(tx);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(firstMask.ToBytes())));
        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(expandedMask.ToBytes())));
    }

    [Test]
    public void should_announce_persisted_light_v1_blob_tx_with_elided_network_size()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        Transaction elidedTx = BuildElidedBlobTransaction(tx);
        LightTransaction lightTx = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        _handler.SendNewTransactions([lightTx], sendFullTx: false);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.Sizes[0] == elidedTx.GetLength() &&
            m.Sizes[0] < tx.GetLength()));
    }

    [Test]
    public void should_request_blob_cells_asynchronously_after_announcement()
    {
        RecreateHandler(providerProbabilityPercent: 100);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        BlobCellMask announcementMask = BlobCellMask.Full;
        _blobCustodyTracker.Update(BlobCellMask.FromIndices([2, 7]));

        Hash256 hash = tx.Hash!;
        _transactionPool.NotifyAboutTx(hash, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1024],
            [hash],
            announcementMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m =>
            m.EthMessage.Hashes.Count == 1 &&
            m.EthMessage.Hashes[0] == hash));
        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.PacketType == Eth72MessageCode.GetCells &&
            m.Hashes[0] == hash &&
            m.CellMask.SequenceEqual(announcementMask.ToBytes())));

        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.PacketType == Eth72MessageCode.GetCells &&
            m.Hashes[0] == hash &&
            m.CellMask.SequenceEqual(announcementMask.ToBytes())));
    }

    [Test]
    public void should_reject_blob_announcement_without_cell_mask()
    {
        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1024],
            [Hash256.Zero],
            []);

        HandleIncomingStatusMessage();

        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void should_reject_blob_announcement_with_empty_cell_mask()
    {
        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1024],
            [Hash256.Zero],
            BlobCellMask.Empty.ToBytes());

        HandleIncomingStatusMessage();

        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void should_reject_non_blob_announcement_with_cell_mask()
    {
        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.EIP1559],
            [1024],
            [Hash256.Zero],
            BlobCellMask.FromIndices([1]).ToBytes());

        HandleIncomingStatusMessage();

        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void should_accept_non_blob_announcement_with_empty_fixed_cell_mask()
    {
        Hash256 hash = HashFromInt(1);
        _transactionPool.NotifyAboutTx(hash, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.EIP1559],
            [1024],
            [hash],
            BlobCellMask.Empty.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m =>
            m.EthMessage.Hashes.Count == 1 &&
            m.EthMessage.Hashes[0] == hash));
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetCellsMessage72>());
    }

    [Test]
    public void should_use_canonical_cell_request_code_for_geth_peer()
    {
        RecreateHandler(providerProbabilityPercent: 100);
        _session.Node!.ClientId = "Geth/v1.16.0-unstable/windows-amd64/go1.24.2";
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        BlobCellMask announcementMask = BlobCellMask.Full;
        _blobCustodyTracker.Update(BlobCellMask.FromIndices([2, 7]));
        Hash256 hash = tx.Hash!;
        _transactionPool.NotifyAboutTx(hash, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1024],
            [hash],
            announcementMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.PacketType == Eth72MessageCode.GetCells &&
            m.Hashes[0] == hash &&
            m.CellMask.SequenceEqual(announcementMask.ToBytes())));
    }

    [Test]
    public void normal_mode_should_wait_for_transaction_and_two_provider_announcements_before_sampled_request()
    {
        RecreateHandler(providerProbabilityPercent: 0);
        Transaction tx = BuildSparseBlobTransaction(out _, out _);

        Hash256 hash = tx.Hash!;
        BlobCellMask requestMask = BlobCellMask.FromIndices([2, 7]);
        _blobCustodyTracker.Update(requestMask);
        _transactionPool.NotifyAboutTx(hash, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 firstAnnouncement = new(
            [(byte)TxType.Blob],
            [1024],
            [hash],
            BlobCellMask.Full.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(firstAnnouncement, Eth72MessageCode.NewPooledTransactionHashes);

        _session.DidNotReceive().DeliverMessage(Arg.Any<GetCellsMessage72>());

        Assert.That(_sparseBlobPoolPeerRegistry.RecordTransaction((ISparseBlobPoolPeer)_handler, tx), Is.Null);
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetCellsMessage72>());

        TestSparseBlobPeer secondProvider = new(TestItem.PublicKeyC);
        _sparseBlobPoolPeerRegistry.AddPeer(secondProvider);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(secondProvider, hash, BlobCellMask.Full);
        HandleZeroMessage(firstAnnouncement, Eth72MessageCode.NewPooledTransactionHashes);

        Assert.That(secondProvider.CellRequests, Has.Count.EqualTo(1));
        Assert.That(secondProvider.CellRequests[0], Is.EqualTo((hash, requestMask | ExtraCellMask(hash, requestMask))));
    }

    [Test]
    public void normal_mode_should_request_full_cells_from_full_provider_when_selected_as_provider()
    {
        RecreateHandler(providerProbabilityPercent: 100);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        _blobCustodyTracker.Update(BlobCellMask.FromIndices([2, 7]));
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            BlobCellMask.Full.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(BlobCellMask.Full.ToBytes())));
    }

    [Test]
    public void custody_with_64_columns_should_request_all_announced_sparse_cells()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask announcementMask = BlobCellMask.FromIndices([2, 7]);
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            announcementMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(announcementMask.ToBytes())));
    }

    [Test]
    public void should_elide_blob_payload_in_pooled_transactions_response()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        int fullTxLength = tx.GetLength();

        _transactionPool.TryGetPendingTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });

        using GetPooledTransactionsMessage request = new(new[] { tx.Hash! }.ToPooledList());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth66MessageCode.GetPooledTransactions);

        _session.Received(1).DeliverMessage(Arg.Is<PooledTransactionsMessage>(m =>
            m.EthMessage.Transactions.Count == 1 &&
            m.EthMessage.Transactions[0].NetworkWrapper != null &&
            ((ShardBlobNetworkWrapper)m.EthMessage.Transactions[0].NetworkWrapper!).Blobs.Length == 0 &&
            m.EthMessage.Transactions[0].GetLength() < fullTxLength));
    }

    [Test]
    public void should_not_elide_v0_blob_payload_in_pooled_transactions_response()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Cancun.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        int fullTxLength = tx.GetLength();

        _transactionPool.TryGetPendingTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });

        using GetPooledTransactionsMessage request = new(new[] { tx.Hash! }.ToPooledList());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth66MessageCode.GetPooledTransactions);

        _session.Received(1).DeliverMessage(Arg.Is<PooledTransactionsMessage>(m =>
            m.EthMessage.Transactions.Count == 1 &&
            m.EthMessage.Transactions[0].NetworkWrapper != null &&
            ((ShardBlobNetworkWrapper)m.EthMessage.Transactions[0].NetworkWrapper!).Blobs.Length > 0 &&
            m.EthMessage.Transactions[0].GetLength() == fullTxLength));
    }

    [Test]
    public void should_accept_announced_v0_full_blob_pooled_response_and_ignore_cells()
    {
        RecreateHandler(providerProbabilityPercent: 100);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Cancun.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        AnnounceBlobTransaction(tx.Hash!, tx.GetLength(), TxType.Blob);
        long requestId = GetLastGetCellsRequestId(tx.Hash!, BlobCellMask.Full);

        using PooledTransactionsMessage66 response = new(1111, new PooledTransactionsMessage65(new[] { tx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        _transactionPool.Received(1).SubmitTx(
            Arg.Is<Transaction>(submitted => IsV0BlobTransaction(submitted, tx.Hash!)),
            TxHandlingOptions.None);

        using CellsMessage72 cells = new(
            requestId,
            [tx.Hash!],
            [[[]]],
            BlobCellMask.FromIndices([0]).ToBytes());
        HandleZeroMessage(cells, Eth72MessageCode.Cells);

        _transactionPool.DidNotReceive().TryMergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public void should_announce_sparse_blob_tx_size_matching_elided_pooled_response()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        int fullTxLength = tx.GetLength();
        int elidedTxLength = GetElidedBlobTransactionLength(tx);

        _handler.SendNewTransaction(tx);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.Sizes.Length == 1 &&
            m.Sizes[0] == elidedTxLength &&
            m.Sizes[0] < fullTxLength));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void should_disconnect_if_pooled_blob_tx_shape_differs_from_eth72_announcement(bool wrongSize)
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction elidedTx = BuildElidedBlobTransaction(tx);
        int announcedSize = elidedTx.GetLength();
        TxType announcedType = TxType.Blob;
        if (wrongSize)
        {
            announcedSize++;
        }
        else
        {
            announcedType = TxType.EIP1559;
        }

        AnnounceBlobTransaction(tx.Hash!, announcedSize, announcedType);

        using PooledTransactionsMessage66 response = new(1111, new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        _session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure, "invalid pooled tx type or size");
    }

    [Test]
    public void should_disconnect_if_eth72_pooled_blob_tx_has_empty_sparse_sidecar()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction txWithEmptySidecar = BuildBlobTransactionWithEmptySparseSidecar(tx);

        AnnounceBlobTransaction(tx.Hash!, txWithEmptySidecar.GetLength(), TxType.Blob);

        using PooledTransactionsMessage66 response = new(1111, new PooledTransactionsMessage65(new[] { txWithEmptySidecar }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        _session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure, "invalid pooled tx type or size");
    }

    [Test]
    public void should_disconnect_if_eth72_pooled_blob_tx_commitments_do_not_match_hashes()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction txWithMismatchedCommitment = BuildBlobTransactionWithMismatchedCommitment(tx);

        AnnounceBlobTransaction(tx.Hash!, txWithMismatchedCommitment.GetLength(), TxType.Blob);

        using PooledTransactionsMessage66 response = new(1111, new PooledTransactionsMessage65(new[] { txWithMismatchedCommitment }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        _session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure, "invalid pooled tx type or size");
    }

    [Test]
    public void should_validate_sparse_v1_wrapper_lengths_without_full_blob_array()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out byte[][] cells), Is.True);

        ShardBlobNetworkWrapper sparseWrapper = wrapper with
        {
            Blobs = [],
            CellMask = cellMask,
            Cells = cells,
        };

        IBlobProofsVerifier proofsVerifier = IBlobProofsManager.For(ProofVersion.V1);
        Assert.That(proofsVerifier.ValidateLengths(sparseWrapper), Is.True);
        Assert.That(proofsVerifier.ValidateProofs(sparseWrapper), Is.True);
    }

    [Test]
    public void should_skip_cells_response_when_requested_mask_not_fully_available()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        BlobCellMask requestedMask = BlobCellMask.FromIndices([1, 3]);
        BlobCellMask availableMask = BlobCellMask.FromIndices([3]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, availableMask, out byte[][] availableCells), Is.True);

        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });
        _transactionPool.TryGetBlobCells(tx.Hash!, requestedMask, out Arg.Any<BlobCellMask>(), out Arg.Any<byte[][]>())
            .Returns(x =>
            {
                x[2] = availableMask;
                x[3] = availableCells;
                return true;
            });

        using GetCellsMessage72 request = new(1234, [tx.Hash!], requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == request.RequestId &&
            m.PacketType == Eth72MessageCode.Cells &&
            m.Hashes.Length == 0 &&
            m.CellMask.SequenceEqual(requestedMask.ToBytes()) &&
            m.Cells.Length == 0));
    }

    [Test]
    public void should_include_multiple_hashes_in_cells_response_when_requested_mask_available()
    {
        Transaction firstTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction secondTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(1UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1, 3]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)firstTx.NetworkWrapper!, requestedMask, out byte[][] firstCells), Is.True);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)secondTx.NetworkWrapper!, requestedMask, out byte[][] secondCells), Is.True);
        SetupGetCellsResponse(firstTx, requestedMask, requestedMask, firstCells);
        SetupGetCellsResponse(secondTx, requestedMask, requestedMask, secondCells);

        using GetCellsMessage72 request = new(1234, [firstTx.Hash!, secondTx.Hash!], requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == request.RequestId &&
            m.Hashes.SequenceEqual(new[] { firstTx.Hash!, secondTx.Hash! }) &&
            m.CellMask.SequenceEqual(requestedMask.ToBytes()) &&
            m.Cells.Length == 2 &&
            m.Cells[0].Zip(firstCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal) &&
            m.Cells[1].Zip(secondCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_reject_cells_request_over_hash_count_limit()
    {
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1]);
        Hash256[] hashes = new Hash256[Eth72ProtocolHandler.MaxCellsResponseHashes + 1];
        for (int i = 0; i < hashes.Length; i++)
        {
            Hash256 hash = HashFromInt(i);
            hashes[i] = hash;
            Transaction tx = new()
            {
                Type = TxType.Blob,
                Hash = hash,
                BlobVersionedHashes = [new byte[Hash256.Size]],
            };
            SetupGetCellsResponse(tx, requestedMask, requestedMask, [[(byte)i]]);
        }

        using GetCellsMessage72 request = new(1234, hashes, requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        Assert.That(() => HandleZeroMessage(request, Eth72MessageCode.GetCells), Throws.TypeOf<RlpLimitException>());
    }

    [Test]
    public void should_skip_hashes_without_complete_requested_cells()
    {
        Transaction firstTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction secondTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(1UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1, 3]);
        BlobCellMask firstAvailableMask = BlobCellMask.FromIndices([3]);
        BlobCellMask secondAvailableMask = requestedMask;
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)firstTx.NetworkWrapper!, firstAvailableMask, out byte[][] firstCells), Is.True);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)secondTx.NetworkWrapper!, secondAvailableMask, out byte[][] secondCells), Is.True);
        SetupGetCellsResponse(firstTx, requestedMask, firstAvailableMask, firstCells);
        SetupGetCellsResponse(secondTx, requestedMask, secondAvailableMask, secondCells);

        using GetCellsMessage72 request = new(1234, [firstTx.Hash!, secondTx.Hash!], requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == request.RequestId &&
            m.Hashes.SequenceEqual(new[] { secondTx.Hash! }) &&
            m.CellMask.SequenceEqual(requestedMask.ToBytes()) &&
            m.Cells.Length == 1 &&
            m.Cells[0].Zip(secondCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_merge_only_columns_present_for_all_blobs_from_cells_response()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        BlobCellMask availableMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, availableMask, out byte[][] availableCells), Is.True);
        byte[][] responseCells = new byte[tx.BlobVersionedHashes!.Length * requestedMask.Count][];
        for (int i = 0; i < tx.BlobVersionedHashes.Length; i++)
        {
            responseCells[i * requestedMask.Count] = availableCells[i];
            responseCells[i * requestedMask.Count + 1] = [];
        }

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool pendingTransactionAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = pendingTransactionAvailable ? tx : null!;
                return pendingTransactionAvailable;
            });
        _transactionPool.TryMergeBlobCells(tx.Hash!, availableMask, Arg.Any<byte[][]>()).Returns(true);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [responseCells], requestedMask.ToBytes());
        pendingTransactionAvailable = true;
        HandleZeroMessage(response, Eth72MessageCode.Cells);

        _transactionPool.Received(1).TryMergeBlobCells(tx.Hash!, availableMask, Arg.Is<byte[][]>(m =>
            m.Length == availableCells.Length &&
            m.Zip(availableCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_re_request_missing_cells_after_partial_cells_response()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        BlobCellMask responseMask = BlobCellMask.FromIndices([4]);
        BlobCellMask missingMask = BlobCellMask.FromIndices([9]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, responseMask, out byte[][] responseCells), Is.True);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool pendingTransactionAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = pendingTransactionAvailable ? tx : null!;
                return pendingTransactionAvailable;
            });
        _transactionPool.TryMergeBlobCells(tx.Hash!, responseMask, Arg.Any<byte[][]>()).Returns(true);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [responseCells], responseMask.ToBytes());
        pendingTransactionAvailable = true;
        HandleZeroMessage(response, Eth72MessageCode.Cells);

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(missingMask.ToBytes())));
    }

    [Test]
    public void should_re_request_after_empty_cells_response()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [], [], BlobCellMask.Empty.ToBytes());

        HandleZeroMessage(response, Eth72MessageCode.Cells);

        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(requestedMask.ToBytes())), Is.EqualTo(2));
    }

    [Test]
    public void should_reject_empty_cells_response_mask_with_non_empty_rows()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _sparseBlobPoolPeerRegistry.AddPeer(otherPeer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(otherPeer, tx.Hash!, requestedMask);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [[]], BlobCellMask.Empty.ToBytes());

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, requestedMask)));
    }

    [Test]
    public void should_re_request_from_other_peer_when_cells_response_has_no_requested_hash()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _sparseBlobPoolPeerRegistry.AddPeer(otherPeer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(otherPeer, tx.Hash!, requestedMask);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [], [], requestedMask.ToBytes());

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, requestedMask)));
    }

    [Test]
    public void should_re_request_from_other_peer_when_cells_response_count_mismatches()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _sparseBlobPoolPeerRegistry.AddPeer(otherPeer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(otherPeer, tx.Hash!, requestedMask);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [], requestedMask.ToBytes());

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, requestedMask)));
    }

    [Test]
    public void should_re_request_from_other_peer_when_cells_response_decode_fails_after_request_id()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _sparseBlobPoolPeerRegistry.AddPeer(otherPeer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(otherPeer, tx.Hash!, requestedMask);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [[]], [1, 2]);

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpException>());
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, requestedMask)));
    }

    [Test]
    public void should_re_request_from_other_peer_when_cells_response_has_trailing_bytes()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _sparseBlobPoolPeerRegistry.AddPeer(otherPeer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(otherPeer, tx.Hash!, requestedMask);

        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [], [], BlobCellMask.Empty.ToBytes());
        using DisposableByteBuffer packet = _svc.ZeroSerialize(response).AsDisposable();
        packet.EnsureWritable(1);
        packet.WriteByte(0);
        packet.ReadByte();

        Assert.That(() => _handler.HandleMessage(new ZeroPacket(packet) { PacketType = Eth72MessageCode.Cells }), Throws.TypeOf<IncompleteDeserializationException>());
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, requestedMask)));
    }

    [Test]
    public void should_reject_cells_response_with_no_available_requested_cells()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        byte[][] responseCells = new byte[tx.BlobVersionedHashes!.Length * requestedMask.Count][];
        Array.Fill(responseCells, []);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool pendingTransactionAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = pendingTransactionAvailable ? tx : null!;
                return pendingTransactionAvailable;
            });

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [responseCells], requestedMask.ToBytes());
        pendingTransactionAvailable = true;

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());
        _transactionPool.DidNotReceive().TryMergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());
    }

    [Test]
    public void should_re_request_empty_cells_inside_advertised_mask()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        BlobCellMask availableMask = BlobCellMask.FromIndices([4]);
        BlobCellMask missingMask = BlobCellMask.FromIndices([9]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, requestedMask, out byte[][] responseCells), Is.True);
        responseCells[1] = [];
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, availableMask, out byte[][] availableCells), Is.True);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool pendingTransactionAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = pendingTransactionAvailable ? tx : null!;
                return pendingTransactionAvailable;
            });
        _transactionPool.TryMergeBlobCells(tx.Hash!, availableMask, Arg.Any<byte[][]>()).Returns(true);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [responseCells], requestedMask.ToBytes());
        pendingTransactionAvailable = true;

        HandleZeroMessage(response, Eth72MessageCode.Cells);

        _transactionPool.Received(1).TryMergeBlobCells(tx.Hash!, availableMask, Arg.Is<byte[][]>(m =>
            m.Length == availableCells.Length &&
            m.Zip(availableCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(missingMask.ToBytes())));
    }

    [Test]
    public void should_ignore_cells_response_with_unmatched_request_id()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool txAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txAvailable ? tx : null!;
                return txAvailable;
            });
        _transactionPool.TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(true);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, cellMask) + 1, [tx.Hash!], [cells], cellMask.ToBytes());
        txAvailable = true;
        HandleZeroMessage(response, Eth72MessageCode.Cells);

        _transactionPool.DidNotReceive().TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void should_re_request_cells_without_evicting_tx_when_cells_response_fails_proof_validation()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);
        byte[][] invalidCells = new byte[cells.Length][];
        for (int i = 0; i < cells.Length; i++)
        {
            invalidCells[i] = [.. cells[i]];
        }
        invalidCells[0][0] ^= 0x01;

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool txAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txAvailable ? tx : null!;
                return txAvailable;
            });

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _sparseBlobPoolPeerRegistry.AddPeer(otherPeer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(otherPeer, tx.Hash!, cellMask);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [invalidCells], cellMask.ToBytes());
        txAvailable = true;

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);
        _transactionPool.DidNotReceive().TryMergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());
        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, cellMask)));
    }

    [Test]
    public void should_re_request_cells_without_evicting_tx_when_stored_sparse_proofs_fail_validation()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);
        CorruptSparseBlobProof(tx, cellMask);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool txAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txAvailable ? tx : null!;
                return txAvailable;
            });

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _sparseBlobPoolPeerRegistry.AddPeer(otherPeer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(otherPeer, tx.Hash!, cellMask);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        txAvailable = true;

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);
        _transactionPool.DidNotReceive().TryMergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());
        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        Assert.That(otherPeer.Disconnects, Is.Empty);
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, cellMask)));
    }

    [Test]
    public void should_re_request_cells_without_disconnect_when_merge_loses_txpool_race()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool txAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txAvailable ? tx : null!;
                return txAvailable;
            });

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _sparseBlobPoolPeerRegistry.AddPeer(otherPeer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(otherPeer, tx.Hash!, cellMask);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        txAvailable = true;

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);
        _transactionPool.Received(1).TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, cellMask)));
    }

    [Test]
    public void should_merge_buffered_cells_when_blob_tx_becomes_pending()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);

        BlobCellMask cellMask = BlobCellMask.Full;
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        bool txAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txAvailable ? tx : null!;
                return txAvailable;
            });
        _transactionPool.TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(true);

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(cellMask.ToBytes())));

        using CellsMessage72 message = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        HandleZeroMessage(message, Eth72MessageCode.Cells);

        _transactionPool.DidNotReceive().TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());

        txAvailable = true;
        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _transactionPool.Received(1).TryMergeBlobCells(tx.Hash!, cellMask, Arg.Is<byte[][]>(m =>
            m.Length == cells.Length &&
            m.Zip(cells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_re_request_invalid_buffered_cells_when_blob_tx_becomes_pending()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);
        byte[][] invalidCells = new byte[cells.Length][];
        for (int i = 0; i < cells.Length; i++)
        {
            invalidCells[i] = [.. cells[i]];
        }

        invalidCells[0][0] ^= 0x01;

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        _transactionPool.TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(true);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        bool txAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txAvailable ? tx : null!;
                return txAvailable;
            });

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _sparseBlobPoolPeerRegistry.AddPeer(otherPeer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(otherPeer, tx.Hash!, cellMask);
        using CellsMessage72 message = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [invalidCells], cellMask.ToBytes());
        HandleZeroMessage(message, Eth72MessageCode.Cells);

        txAvailable = true;
        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _transactionPool.DidNotReceive().TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, cellMask)));
    }

    [Test]
    public void should_apply_cells_if_tx_becomes_pending_while_cells_are_buffered()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);

        BlobCellMask cellMask = BlobCellMask.Full;
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        int pendingLookupCount = 0;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                pendingLookupCount++;
                bool txAvailable = pendingLookupCount > 1;
                x[1] = txAvailable ? tx : null!;
                return txAvailable;
            });
        _transactionPool.TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(true);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 message = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        HandleZeroMessage(message, Eth72MessageCode.Cells);

        _transactionPool.Received(1).TryMergeBlobCells(tx.Hash!, cellMask, Arg.Is<byte[][]>(m =>
            m.Length == cells.Length &&
            m.Zip(cells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_not_drop_readded_sent_cell_request_when_trimming_stale_queue_entries()
    {
        const int maxSentCellRequests = 4096;
        Transaction tx = BuildBlobTransaction(fullProvider: true);

        BlobCellMask cellMask = BlobCellMask.Full;
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        _transactionPool.NotifyAboutTx(Arg.Any<Hash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool txAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(Arg.Any<Hash256>(), out Arg.Any<Transaction>())
            .Returns(x =>
            {
                Hash256 hash = x.Arg<Hash256>();
                x[1] = txAvailable && hash == tx.Hash ? tx : null!;
                return txAvailable && hash == tx.Hash;
            });
        _transactionPool.TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(true);

        HandleIncomingStatusMessage();

        using NewPooledTransactionHashesMessage72 firstAnnouncement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());
        HandleZeroMessage(firstAnnouncement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 firstCells = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        txAvailable = true;
        HandleZeroMessage(firstCells, Eth72MessageCode.Cells);
        txAvailable = false;

        for (int i = 0; i < maxSentCellRequests; i++)
        {
            using NewPooledTransactionHashesMessage72 announcement = new(
                [(byte)TxType.Blob],
                [1024],
                [HashFromInt(i)],
                cellMask.ToBytes());
            HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        }

        using NewPooledTransactionHashesMessage72 secondAnnouncement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());
        HandleZeroMessage(secondAnnouncement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 secondCells = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        txAvailable = true;
        HandleZeroMessage(secondCells, Eth72MessageCode.Cells);

        _transactionPool.Received(2).TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void should_buffer_cells_requested_before_tx_becomes_pending()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);

        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        _transactionPool.TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(true);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(cellMask.ToBytes())));

        using CellsMessage72 message = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        HandleZeroMessage(message, Eth72MessageCode.Cells);
        _transactionPool.DidNotReceive().TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());

        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _transactionPool.Received(1).TryMergeBlobCells(tx.Hash!, cellMask, Arg.Is<byte[][]>(m =>
            m.Length == cells.Length &&
            m.Zip(cells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
        Assert.That(_sparseBlobPoolPeerRegistry.TryRequestCells(tx.Hash!, cellMask, TestItem.PublicKeyB), Is.True);
    }

    [Test]
    public void should_ignore_unsolicited_cells()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        bool txAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txAvailable ? tx : null!;
                return txAvailable;
            });
        _transactionPool.TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(true);

        using CellsMessage72 message = new([tx.Hash!], [cells], cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.Cells);

        txAvailable = true;
        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _transactionPool.DidNotReceive().TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void should_reject_cells_with_unrequested_mask()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        BlobCellMask requestedMask = BlobCellMask.FromIndices([4]);
        BlobCellMask responseMask = BlobCellMask.FromIndices([4, 7]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, responseMask, out byte[][] cells), Is.True);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool txAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txAvailable ? tx : null!;
                return txAvailable;
            });

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        using CellsMessage72 message = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [cells], responseMask.ToBytes());
        txAvailable = true;
        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void registry_should_request_cells_from_non_preferred_announcing_peer_when_available()
    {
        SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer preferredPeer = new(TestItem.PublicKeyA);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);

        registry.AddPeer(preferredPeer);
        registry.AddPeer(otherPeer);
        registry.RecordAnnouncement(preferredPeer, hash, cellMask);
        registry.RecordAnnouncement(otherPeer, hash, cellMask);

        Assert.That(registry.TryRequestCells(hash, cellMask, preferredPeer.Id), Is.True);
        Assert.That(preferredPeer.CellRequests, Is.Empty);
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((hash, cellMask)));
    }

    [Test]
    public void registry_should_request_only_expanded_custody_delta()
    {
        SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        BlobCellMask firstMask = BlobCellMask.FromIndices([4]);
        BlobCellMask expandedMask = BlobCellMask.FromIndices([4, 9]);

        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, hash, BlobCellMask.Full);

        Assert.That(registry.RequestCellsForCustodyChange(firstMask, requestAllAnnouncedCells: false), Is.EqualTo(1));
        Assert.That(registry.RequestCellsForCustodyChange(expandedMask, requestAllAnnouncedCells: false), Is.EqualTo(1));

        Assert.That(peer.CellRequests, Has.Count.EqualTo(2));
        Assert.That(peer.CellRequests[0], Is.EqualTo((hash, firstMask)));
        Assert.That(peer.CellRequests[1], Is.EqualTo((hash, BlobCellMask.FromIndices([9]))));
    }

    [Test]
    public void registry_should_remove_tracked_announcements_when_peer_is_removed()
    {
        SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), new CapturingBackgroundTaskScheduler(), LimboLogs.Instance);
        TestSparseBlobPeer removedPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer remainingPeer = new(TestItem.PublicKeyD);
        Hash256 hash = HashFromInt(1);
        BlobCellMask remainingMask = BlobCellMask.FromIndices([4]);

        registry.AddPeer(removedPeer);
        registry.AddPeer(remainingPeer);
        registry.RecordAnnouncement(removedPeer, hash, BlobCellMask.Full);
        registry.RecordAnnouncement(remainingPeer, hash, remainingMask);

        Assert.That(registry.GetFullProviderAnnouncementCount(hash), Is.EqualTo(1));

        registry.RemovePeer(removedPeer.Id);
        registry.RecordAnnouncement(removedPeer, hash, BlobCellMask.Full);

        Assert.That(registry.GetFullProviderAnnouncementCount(hash), Is.Zero);
        Assert.That(registry.RequestCellsForCustodyChange(BlobCellMask.Full, requestAllAnnouncedCells: true), Is.EqualTo(1));
        Assert.That(removedPeer.CellRequests, Is.Empty);
        Assert.That(remainingPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(remainingPeer.CellRequests[0], Is.EqualTo((hash, remainingMask)));
    }

    [Test]
    public void registry_should_submit_sparse_blob_tx_only_after_valid_cells_arrive()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        bool submitted = false;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(_ =>
        {
            submitted = true;
            return AcceptTxResult.Accepted;
        });

        registry.AddPeer(peer);
        AcceptTxResult? initialResult = registry.RecordTransaction(peer, tx);

        Assert.That(initialResult, Is.Null);
        Assert.That(submitted, Is.False);

        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);

        Assert.That(SpinWait.SpinUntil(() => submitted, TimeSpan.FromSeconds(1)), Is.True);
        _transactionPool.Received(1).SubmitTx(
            Arg.Is<Transaction>(submittedTx => HasExpectedSparseCells(submittedTx, tx.Hash!, cellMask, cells.Length)),
            TxHandlingOptions.None);
        Assert.That(peer.Disconnects, Is.Empty);
    }

    [Test]
    public void registry_should_submit_sparse_blob_tx_with_attached_cells()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        tx.NetworkWrapper = ((ShardBlobNetworkWrapper)tx.NetworkWrapper!) with { CellMask = cellMask, Cells = cells };
        tx.ClearLengthCache();

        SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

        registry.AddPeer(peer);
        AcceptTxResult? result = registry.RecordTransaction(peer, tx);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        _transactionPool.Received(1).SubmitTx(
            Arg.Is<Transaction>(submittedTx => HasExpectedSparseCells(submittedTx, tx.Hash!, cellMask, cells.Length)),
            TxHandlingOptions.None);
        Assert.That(peer.Disconnects, Is.Empty);
    }

    [Test]
    public void registry_should_retry_without_disconnect_when_cell_proof_tuple_is_invalid()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        byte[][] invalidCells = new byte[cells.Length][];
        for (int i = 0; i < cells.Length; i++)
        {
            invalidCells[i] = [.. cells[i]];
        }

        invalidCells[0][0] ^= 1;

        SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyA);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

        registry.AddPeer(peer);
        registry.AddPeer(otherPeer);
        registry.RecordAnnouncement(otherPeer, tx.Hash!, cellMask);
        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, invalidCells), Is.True);

        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        Assert.That(peer.Disconnects, Is.Empty);
        Assert.That(otherPeer.Disconnects, Is.Empty);
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, cellMask)));
    }

    [Test]
    public void registry_should_retry_without_disconnect_when_sparse_proof_tuple_is_invalid()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        CorruptSparseBlobProof(tx, cellMask);

        SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer txPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer cellPeer = new(TestItem.PublicKeyA);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

        registry.AddPeer(txPeer);
        registry.AddPeer(cellPeer);
        registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask);
        Assert.That(registry.RecordTransaction(txPeer, tx), Is.Null);
        Assert.That(registry.RecordCells(cellPeer, tx.Hash!, cellMask, cells), Is.True);

        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        Assert.That(txPeer.Disconnects, Is.Empty);
        Assert.That(cellPeer.Disconnects, Is.Empty);
        Assert.That(cellPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(cellPeer.CellRequests[0], Is.EqualTo((tx.Hash!, cellMask)));
    }

    [Test]
    public void registry_should_keep_sparse_tx_tracked_for_saturation_after_submit()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        CapturingBackgroundTaskScheduler scheduler = new();
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            scheduler,
            LimboLogs.Instance,
            saturationTimeout: TimeSpan.Zero,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        bool submitted = false;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(_ =>
        {
            submitted = true;
            return AcceptTxResult.Accepted;
        });

        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, tx.Hash!, BlobCellMask.Full);
        Assert.That(SpinWait.SpinUntil(() => scheduler.LastTimeout is not null, TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(scheduler.LastTimeout, Is.Not.Null);
        Assert.That(scheduler.LastTimeout!.Value, Is.GreaterThan(TimeSpan.FromSeconds(5)));

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);

        Assert.That(SpinWait.SpinUntil(() => submitted, TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(registry.TryRequestCells(tx.Hash!, BlobCellMask.Full, TestItem.PublicKeyA), Is.True);
        Assert.That(peer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(peer.CellRequests[0], Is.EqualTo((tx.Hash!, BlobCellMask.Full)));
    }

    [Test]
    public void registry_should_not_downgrade_full_cells_with_later_sampled_cells()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells, out byte[][] fullCells);
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            saturationTimeout: TimeSpan.Zero,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer fullPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer sampledPeer = new(TestItem.PublicKeyA);
        bool submitted = false;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(_ =>
        {
            submitted = true;
            return AcceptTxResult.Accepted;
        });

        registry.AddPeer(fullPeer);
        registry.AddPeer(sampledPeer);

        Assert.That(registry.RecordCells(fullPeer, tx.Hash!, BlobCellMask.Full, fullCells), Is.True);
        Assert.That(registry.RecordCells(sampledPeer, tx.Hash!, sampledMask, sampledCells), Is.True);
        Assert.That(registry.RecordTransaction(sampledPeer, tx), Is.EqualTo(AcceptTxResult.Accepted));

        Assert.That(submitted, Is.True);
        _transactionPool.Received(1).SubmitTx(
            Arg.Is<Transaction>(submittedTx => HasExpectedSparseCells(submittedTx, tx.Hash!, BlobCellMask.Full, fullCells.Length)),
            TxHandlingOptions.None);
    }

    [Test]
    public void registry_should_request_full_cells_after_sparse_submit_even_with_two_full_providers()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        CapturingBackgroundTaskScheduler scheduler = new();
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            scheduler,
            LimboLogs.Instance,
            saturationTimeout: TimeSpan.Zero,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer firstFullPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer secondFullPeer = new(TestItem.PublicKeyD);
        TestSparseBlobPeer sampledPeer = new(TestItem.PublicKeyA);
        bool submitted = false;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(_ =>
        {
            submitted = true;
            return AcceptTxResult.Accepted;
        });

        registry.AddPeer(firstFullPeer);
        registry.AddPeer(secondFullPeer);
        registry.AddPeer(sampledPeer);
        registry.RecordAnnouncement(firstFullPeer, tx.Hash!, BlobCellMask.Full);
        registry.RecordAnnouncement(secondFullPeer, tx.Hash!, BlobCellMask.Full);

        Assert.That(registry.RecordTransaction(sampledPeer, tx), Is.Null);
        Assert.That(registry.RecordCells(sampledPeer, tx.Hash!, sampledMask, sampledCells), Is.True);
        Assert.That(submitted, Is.True);

        scheduler.RunLast();

        Assert.That(firstFullPeer.CellRequests.Count + secondFullPeer.CellRequests.Count, Is.EqualTo(1));
        bool firstPeerRequestedFull = firstFullPeer.CellRequests.Count == 1
            && firstFullPeer.CellRequests[0] == (tx.Hash!, BlobCellMask.Full);
        bool secondPeerRequestedFull = secondFullPeer.CellRequests.Count == 1
            && secondFullPeer.CellRequests[0] == (tx.Hash!, BlobCellMask.Full);
        Assert.That(firstPeerRequestedFull || secondPeerRequestedFull, Is.True);
    }

    [Test]
    public void registry_should_retry_full_fallback_without_eviction()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        CapturingBackgroundTaskScheduler scheduler = new();
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            scheduler,
            LimboLogs.Instance,
            saturationTimeout: TimeSpan.Zero,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer fullPeer = new(TestItem.PublicKeyC);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

        registry.AddPeer(fullPeer);
        registry.RecordAnnouncement(fullPeer, tx.Hash!, BlobCellMask.Full);
        Assert.That(registry.RecordTransaction(fullPeer, tx), Is.Null);
        Assert.That(registry.RecordCells(fullPeer, tx.Hash!, sampledMask, sampledCells), Is.True);

        for (int i = 1; i <= 12; i++)
        {
            scheduler.RunLast();
            Assert.That(fullPeer.CellRequests, Has.Count.EqualTo(i));
            _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
        }

        scheduler.RunLast();
        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
    }

    [Test]
    public async Task registry_should_not_submit_same_sparse_tx_twice_when_transaction_and_cells_race()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            saturationTimeout: TimeSpan.Zero,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        using ManualResetEventSlim submitEntered = new();
        using ManualResetEventSlim releaseSubmit = new();
        int submitCount = 0;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(_ =>
        {
            Interlocked.Increment(ref submitCount);
            submitEntered.Set();
            if (!releaseSubmit.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting to release sparse tx submit.");
            }

            return AcceptTxResult.Accepted;
        });

        registry.AddPeer(peer);
        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);

        Task<bool> firstSubmit = Task.Run(() => registry.RecordCells(peer, tx.Hash!, sampledMask, sampledCells));
        Assert.That(submitEntered.Wait(TimeSpan.FromSeconds(1)), Is.True);

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(Volatile.Read(ref submitCount), Is.EqualTo(1));

        releaseSubmit.Set();
        Assert.That(await firstSubmit.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(Volatile.Read(ref submitCount), Is.EqualTo(1));
    }

    [Test]
    public void registry_should_not_evict_local_full_tx_from_announcement_only_state()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        CapturingBackgroundTaskScheduler scheduler = new();
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            scheduler,
            LimboLogs.Instance,
            saturationTimeout: TimeSpan.Zero,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        bool fullTxAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = fullTxAvailable ? tx : null!;
                return fullTxAvailable;
            });

        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, tx.Hash!, BlobCellMask.Full);
        fullTxAvailable = true;

        scheduler.RunLast();

        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
    }

    [Test]
    public void registry_should_not_evict_local_full_cell_tx_from_announcement_only_state()
    {
        Transaction tx = BuildSparseBlobTransaction(out _, out _, out byte[][] fullCells);
        tx.NetworkWrapper = ((ShardBlobNetworkWrapper)tx.NetworkWrapper!) with { CellMask = BlobCellMask.Full, Cells = fullCells };
        CapturingBackgroundTaskScheduler scheduler = new();
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            scheduler,
            LimboLogs.Instance,
            saturationTimeout: TimeSpan.Zero,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        bool fullTxAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = fullTxAvailable ? tx : null!;
                return fullTxAvailable;
            });

        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, tx.Hash!, BlobCellMask.Full);
        fullTxAvailable = true;

        scheduler.RunLast();

        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
    }

    private void HandleIncomingStatusMessage()
    {
        using StatusMessage69 statusMsg = new() { ProtocolVersion = 72, GenesisHash = _genesisBlock.Hash!, LatestBlockHash = _genesisBlock.Hash! };

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

    private long GetLastGetCellsRequestId(Hash256 hash, BlobCellMask cellMask)
        => _deliveredMessages
            .OfType<GetCellsMessage72>()
            .Last(m =>
                m.Hashes.Length == 1 &&
                m.Hashes[0] == hash &&
                m.CellMask.SequenceEqual(cellMask.ToBytes()))
            .RequestId;

    private void RecreateHandler(int providerProbabilityPercent = 15)
    {
        _handler.Dispose();
        _txPoolConfig.SparseBlobProviderProbabilityPercent.Returns(providerProbabilityPercent);
        _handler = new Eth72ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            _txPoolConfig,
            _specProvider,
            _blobCustodyTracker,
            _nodeKey,
            _sparseBlobPoolPeerRegistry,
            _txGossipPolicy);
        _handler.Init();
    }

    private static Transaction BuildBlobTransaction(bool fullProvider)
    {
        for (int nonce = 0; nonce < 4096; nonce++)
        {
            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithNonce((ulong)nonce)
                .SignedAndResolved()
                .TestObject;

            if (ShouldFetchFull(tx.Hash!) == fullProvider)
            {
                return tx;
            }
        }

        throw new AssertionException($"Could not build blob transaction with {nameof(fullProvider)}={fullProvider}.");
    }

    private static bool ShouldFetchFull(Hash256 hash)
    {
        Span<byte> input = stackalloc byte[PublicKey.LengthInBytes + 32];
        TestItem.PublicKeyB.Bytes.CopyTo(input);
        hash.Bytes.CopyTo(input[PublicKey.LengthInBytes..]);
        Hash256 sampleHash = Keccak.Compute(input);
        ushort sample = BinaryPrimitives.ReadUInt16BigEndian(sampleHash.Bytes[..2]);
        return sample % 100 < 15;
    }

    private static BlobCellMask ExtraCellMask(Hash256 hash, BlobCellMask alreadyRequested)
    {
        UInt128 candidates = BlobCellMask.Full.Value & ~alreadyRequested.Value;
        Span<int> candidateIndices = stackalloc int[BlobCellMask.CellCount];
        int candidateCount = 0;
        for (int i = 0; i < BlobCellMask.CellCount; i++)
        {
            if ((candidates & (UInt128.One << i)) != 0)
            {
                candidateIndices[candidateCount++] = i;
            }
        }

        Span<byte> input = stackalloc byte[PublicKey.LengthInBytes + 33];
        TestItem.PublicKeyB.Bytes.CopyTo(input);
        hash.Bytes.CopyTo(input[PublicKey.LengthInBytes..^1]);
        input[^1] = 1;
        Hash256 sampleHash = Keccak.Compute(input);
        int selected = sampleHash.Bytes[0] % candidateCount;
        return new BlobCellMask(UInt128.One << candidateIndices[selected]);
    }

    private static BlobCellMask SupernodeCustodyMask()
    {
        UInt128 mask = UInt128.Zero;
        for (int i = 0; i < 64; i++)
        {
            mask |= UInt128.One << i;
        }

        return new BlobCellMask(mask);
    }

    private static Hash256 HashFromInt(int value)
    {
        byte[] bytes = new byte[Hash256.Size];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), value);
        return new Hash256(bytes);
    }

    private static bool HasExpectedSparseCells(Transaction submittedTx, Hash256 hash, BlobCellMask cellMask, int cellsLength) =>
        submittedTx.Hash == hash
            && submittedTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && wrapper.CellMask == cellMask
            && wrapper.Cells is not null
            && wrapper.Cells.Length == cellsLength;

    private static bool IsV0BlobTransaction(Transaction submittedTx, Hash256 hash) =>
        submittedTx.Hash == hash
            && submittedTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && wrapper.Version == ProofVersion.V0;

    private void AnnounceBlobTransaction(Hash256 hash, int announcedSize, TxType announcedType)
    {
        _transactionPool.NotifyAboutTx(hash, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)announcedType],
            [announcedSize],
            [hash],
            announcedType.SupportsBlobs() ? BlobCellMask.Full.ToBytes() : BlobCellMask.Empty.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
    }

    private void SetupGetCellsResponse(Transaction tx, BlobCellMask requestedMask, BlobCellMask availableMask, byte[][] cells)
    {
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });
        _transactionPool.TryGetBlobCells(tx.Hash!, requestedMask, out Arg.Any<BlobCellMask>(), out Arg.Any<byte[][]>())
            .Returns(x =>
            {
                x[2] = availableMask;
                x[3] = cells;
                return true;
            });
    }

    private static Transaction BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells)
        => BuildSparseBlobTransaction(out cellMask, out cells, out _);

    private static Transaction BuildElidedBlobTransaction(Transaction tx)
    {
        Transaction clone = new();
        tx.CopyTo(clone);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        clone.NetworkWrapper = wrapper with { Blobs = [] };
        clone.ClearLengthCache();
        return clone;
    }

    private static Transaction BuildBlobTransactionWithEmptySparseSidecar(Transaction tx)
    {
        Transaction clone = new();
        tx.CopyTo(clone);
        clone.NetworkWrapper = new ShardBlobNetworkWrapper([], [], [], ProofVersion.V1);
        clone.ClearLengthCache();
        return clone;
    }

    private static Transaction BuildBlobTransactionWithMismatchedCommitment(Transaction tx)
    {
        Transaction clone = BuildElidedBlobTransaction(tx);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)clone.NetworkWrapper!;
        byte[][] commitments = new byte[wrapper.Commitments.Length][];
        for (int i = 0; i < commitments.Length; i++)
        {
            commitments[i] = [.. wrapper.Commitments[i]];
        }

        commitments[0] = new byte[commitments[0].Length];
        clone.NetworkWrapper = wrapper with { Commitments = commitments };
        clone.ClearLengthCache();
        return clone;
    }

    private static void CorruptSparseBlobProof(Transaction tx, BlobCellMask cellMask)
    {
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        byte[][] proofs = new byte[wrapper.Proofs.Length][];
        for (int i = 0; i < proofs.Length; i++)
        {
            proofs[i] = [.. wrapper.Proofs[i]];
        }

        foreach (int cellIndex in cellMask.EnumerateSetBits())
        {
            proofs[cellIndex][0] ^= 1;
            break;
        }

        tx.NetworkWrapper = wrapper with { Proofs = proofs };
        tx.ClearLengthCache();
    }

    private static int GetElidedBlobTransactionLength(Transaction tx) => BuildElidedBlobTransaction(tx).GetLength();

    private static Transaction BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells, out byte[][] fullCells)
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, BlobCellMask.Full, out fullCells), Is.True);
        cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out cells), Is.True);

        tx.NetworkWrapper = wrapper with { Blobs = [] };
        return tx;
    }

    private sealed class TestSparseBlobPeer(PublicKey id) : ISparseBlobPoolPeer
    {
        public PublicKey Id { get; } = id;
        public bool IsClosing { get; set; }
        public List<(Hash256 Hash, BlobCellMask CellMask)> CellRequests { get; } = [];
        public List<(DisconnectReason Reason, string Details)> Disconnects { get; } = [];

        public bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask)
        {
            CellRequests.Add((hash, requestMask));
            return true;
        }

        public void DisconnectSparseBlobPeer(DisconnectReason reason, string details) => Disconnects.Add((reason, details));
    }

    private sealed class CapturingBackgroundTaskScheduler : IBackgroundTaskScheduler
    {
        public TimeSpan? LastTimeout { get; private set; }
        private Func<CancellationToken, Task>? LastTask { get; set; }

        public bool TryScheduleTask<TReq>(
            TReq request,
            Func<TReq, CancellationToken, Task> fulfillFunc,
            TimeSpan? timeout = null,
            string? source = null)
        {
            LastTimeout = timeout;
            LastTask = token => fulfillFunc(request, token);
            return true;
        }

        public void RunLast()
        {
            Assert.That(LastTask, Is.Not.Null);
            LastTask!(CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
