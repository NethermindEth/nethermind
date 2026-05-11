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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

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

    [SetUp]
    public void Setup()
    {
        _specProvider = Substitute.For<ISpecProvider>();
        _svc = Build.A.SerializationService().WithEth72(_specProvider).TestObject;

        NetworkDiagTracer.IsEnabled = true;

        _disposables = new();
        _session = Substitute.For<ISession>();
        Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
        _session.Node.Returns(node);
        _session.When(static s => s.DeliverMessage(Arg.Any<P2PMessage>()))
            .Do(c => c.Arg<P2PMessage>().AddTo(_disposables));

        _syncManager = Substitute.For<ISyncServer>();
        _transactionPool = Substitute.For<ITxPool>();
        _gossipPolicy = Substitute.For<IGossipPolicy>();
        _txPoolConfig = Substitute.For<ITxPoolConfig>();
        _txPoolConfig.BlobsSupport.Returns(BlobsSupportMode.InMemory);
        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncManager.Head.Returns(_genesisBlock.Header);
        _syncManager.Genesis.Returns(_genesisBlock.Header);
        _syncManager.LowestBlock.Returns(0);
        _timerFactory = Substitute.For<ITimerFactory>();
        _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
        _blobCustodyTracker = new BlobCustodyTracker();
        _sparseBlobPoolPeerRegistry = new SparseBlobPoolPeerRegistry(_transactionPool, RunImmediatelyScheduler.Instance, LimboLogs.Instance);

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
            TestItem.PublicKeyB,
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
            .WithNonce((UInt256)0)
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .SignedAndResolved()
            .TestObject;

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        BlobCellMask cellMask = BlobCellMask.FromIndices([1, 3]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out byte[][] cells), Is.True);

        byte[][] emptyBlobs = new byte[wrapper.Blobs.Length][];
        for (int i = 0; i < emptyBlobs.Length; i++)
        {
            emptyBlobs[i] = [];
        }

        tx.NetworkWrapper = wrapper with
        {
            Blobs = emptyBlobs,
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
    public void should_reannounce_blob_tx_when_available_cell_mask_expands()
    {
        Transaction tx = Build.A.Transaction
            .WithNonce((UInt256)0)
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .SignedAndResolved()
            .TestObject;

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        BlobCellMask firstMask = BlobCellMask.FromIndices([1]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, firstMask, out byte[][] firstCells), Is.True);
        BlobCellMask expandedMask = BlobCellMask.FromIndices([1, 3]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, expandedMask, out byte[][] expandedCells), Is.True);

        byte[][] emptyBlobs = new byte[wrapper.Blobs.Length][];
        for (int i = 0; i < emptyBlobs.Length; i++)
        {
            emptyBlobs[i] = [];
        }

        tx.NetworkWrapper = wrapper with
        {
            Blobs = emptyBlobs,
            CellMask = firstMask,
            Cells = firstCells,
        };
        tx.ClearLengthCache();
        _handler.SendNewTransaction(tx);

        tx.NetworkWrapper = wrapper with
        {
            Blobs = emptyBlobs,
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
    public void should_request_blob_cells_asynchronously_after_announcement()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(UInt256.Zero)
            .SignedAndResolved()
            .TestObject;

        BlobCellMask announcementMask = BlobCellMask.FromIndices([2, 7]);
        _blobCustodyTracker.Update(announcementMask);

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
    public void should_use_canonical_cell_request_code_for_geth_peer()
    {
        _session.Node!.ClientId = "Geth/v1.16.0-unstable/windows-amd64/go1.24.2";
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(UInt256.Zero)
            .SignedAndResolved()
            .TestObject;

        BlobCellMask announcementMask = BlobCellMask.FromIndices([2, 7]);
        _blobCustodyTracker.Update(announcementMask);
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
    public void should_elide_blob_payload_in_pooled_transactions_response()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(UInt256.Zero)
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
            ((ShardBlobNetworkWrapper)m.EthMessage.Transactions[0].NetworkWrapper!).Blobs.Length == ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Blobs.Length &&
            ((ShardBlobNetworkWrapper)m.EthMessage.Transactions[0].NetworkWrapper!).Blobs.All(static blob => blob.Length == 0) &&
            m.EthMessage.Transactions[0].GetLength() < fullTxLength));
    }

    [Test]
    public void should_truncate_cells_response_to_available_mask()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(UInt256.Zero)
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
            m.Hashes.SequenceEqual(new[] { tx.Hash! }) &&
            m.CellMask.SequenceEqual(availableMask.ToBytes()) &&
            m.Cells.Length == 1 &&
            m.Cells[0].Length == availableCells.Length &&
            m.Cells[0].Zip(availableCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
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

        using CellsMessage72 message = new([tx.Hash!], [cells], cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(cellMask.ToBytes())));

        HandleZeroMessage(message, Eth72MessageCode.Cells);

        _transactionPool.DidNotReceive().TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());

        txAvailable = true;
        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _transactionPool.Received(1).TryMergeBlobCells(tx.Hash!, cellMask, Arg.Is<byte[][]>(m =>
            m.Length == cells.Length &&
            m.Zip(cells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
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
        using CellsMessage72 message = new([tx.Hash!], [cells], cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
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
        using CellsMessage72 firstCells = new([tx.Hash!], [cells], cellMask.ToBytes());
        HandleZeroMessage(firstAnnouncement, Eth72MessageCode.NewPooledTransactionHashes);
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
        using CellsMessage72 secondCells = new([tx.Hash!], [cells], cellMask.ToBytes());
        HandleZeroMessage(secondAnnouncement, Eth72MessageCode.NewPooledTransactionHashes);
        txAvailable = true;
        HandleZeroMessage(secondCells, Eth72MessageCode.Cells);

        _transactionPool.Received(2).TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void should_buffer_cells_requested_before_tx_becomes_pending()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: false);

        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        _blobCustodyTracker.Update(cellMask);
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());
        using CellsMessage72 message = new([tx.Hash!], [cells], cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(cellMask.ToBytes())));

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
            .WithNonce(UInt256.Zero)
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
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(UInt256.Zero)
            .SignedAndResolved()
            .TestObject;

        BlobCellMask requestedMask = BlobCellMask.FromIndices([4]);
        BlobCellMask responseMask = BlobCellMask.FromIndices([4, 7]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, responseMask, out byte[][] cells), Is.True);

        _blobCustodyTracker.Update(requestedMask);
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
        using CellsMessage72 message = new([tx.Hash!], [cells], responseMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        txAvailable = true;
        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void registry_should_request_cells_from_non_preferred_announcing_peer_when_available()
    {
        SparseBlobPoolPeerRegistry registry = new(_transactionPool, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
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
    public void registry_should_submit_sparse_blob_tx_only_after_valid_cells_arrive()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        SparseBlobPoolPeerRegistry registry = new(_transactionPool, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
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

        SparseBlobPoolPeerRegistry registry = new(_transactionPool, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
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
    public void registry_should_disconnect_cell_peer_when_cells_are_invalid()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        byte[][] invalidCells = new byte[cells.Length][];
        for (int i = 0; i < cells.Length; i++)
        {
            invalidCells[i] = [.. cells[i]];
        }

        invalidCells[0][0] ^= 1;

        SparseBlobPoolPeerRegistry registry = new(_transactionPool, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

        registry.AddPeer(peer);
        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, invalidCells), Is.True);

        Assert.That(SpinWait.SpinUntil(() => peer.Disconnects.Count != 0, TimeSpan.FromSeconds(1)), Is.True);
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        Assert.That(peer.Disconnects[0].Reason, Is.EqualTo(DisconnectReason.BreachOfProtocol));
    }

    [Test]
    public void registry_should_keep_sparse_tx_tracked_for_saturation_after_submit()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        CapturingBackgroundTaskScheduler scheduler = new();
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
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
            .WithNonce(UInt256.Zero)
            .SignedAndResolved()
            .TestObject;
        CapturingBackgroundTaskScheduler scheduler = new();
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
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

    private static Transaction BuildBlobTransaction(bool fullProvider)
    {
        for (int nonce = 0; nonce < 4096; nonce++)
        {
            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithNonce((UInt256)(ulong)nonce)
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
        return sample % 10_000 < 1_500;
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

    private static Transaction BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells)
        => BuildSparseBlobTransaction(out cellMask, out cells, out _);

    private static Transaction BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells, out byte[][] fullCells)
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(UInt256.Zero)
            .SignedAndResolved()
            .TestObject;

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, BlobCellMask.Full, out fullCells), Is.True);
        cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out cells), Is.True);

        byte[][] emptyBlobs = new byte[wrapper.Blobs.Length][];
        for (int i = 0; i < emptyBlobs.Length; i++)
        {
            emptyBlobs[i] = [];
        }

        tx.NetworkWrapper = wrapper with { Blobs = emptyBlobs };
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
