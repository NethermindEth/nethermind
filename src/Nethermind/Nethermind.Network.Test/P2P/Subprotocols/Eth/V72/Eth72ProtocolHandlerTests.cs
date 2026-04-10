// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using CkzgLib;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
        _handler.ProtocolCode.Should().Be("eth");
        _handler.Name.Should().Be("eth72");
        _handler.ProtocolVersion.Should().Be(72);
        _handler.MessageIdSpaceSize.Should().Be(20);
        _handler.IncludeInTxPool.Should().BeTrue();
        _handler.ClientId.Should().Be(_session.Node?.ClientId);
        _handler.HeadHash.Should().BeNull();
        _handler.HeadNumber.Should().Be(0);
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
        BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out byte[][] cells).Should().BeTrue();

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
    public void should_request_blob_cells_after_announced_blob_tx_becomes_pending()
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
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetCellsMessage72>());

        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
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
            ((ShardBlobNetworkWrapper)m.EthMessage.Transactions[0].NetworkWrapper!).Blobs.All(static blob => blob.Length == 0)));
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
        BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, availableMask, out byte[][] availableCells).Should().BeTrue();

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

        using GetCellsMessage72 request = new([tx.Hash!], requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.Hashes.SequenceEqual(new[] { tx.Hash! }) &&
            m.CellMask.SequenceEqual(availableMask.ToBytes()) &&
            m.Cells.Length == 1 &&
            m.Cells[0].Length == availableCells.Length &&
            m.Cells[0].Zip(availableCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_merge_buffered_cells_when_blob_tx_becomes_pending()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(UInt256.Zero)
            .SignedAndResolved()
            .TestObject;

        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells).Should().BeTrue();

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

        _transactionPool.DidNotReceive().TryMergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());

        txAvailable = true;
        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _transactionPool.Received(1).TryMergeBlobCells(tx.Hash!, cellMask, Arg.Is<byte[][]>(m =>
            m.Length == cells.Length &&
            m.Zip(cells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    private void HandleIncomingStatusMessage()
    {
        using var statusMsg = new StatusMessage69 { ProtocolVersion = 72, GenesisHash = _genesisBlock.Hash!, LatestBlockHash = _genesisBlock.Hash! };

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
