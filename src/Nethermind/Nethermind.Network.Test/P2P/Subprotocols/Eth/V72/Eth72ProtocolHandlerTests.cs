// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
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
        _transactionPool.MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>())
            .Returns(BlobCellMergeResult.TransactionUnavailable);
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
        _blobCustodyTracker.Update(BlobCellMask.Full);
        _sparseBlobPoolPeerRegistry = new SparseBlobPoolPeerRegistry(_transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
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
    public void should_send_large_non_blob_tx_single_announcement_with_empty_fixed_cell_mask()
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(0UL)
            .WithData(new byte[5 * 1024])
            .SignedAndResolved()
            .TestObject;

        _handler.SendNewTransaction(tx);

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
    public void should_batch_blob_announcements_with_same_cell_mask()
    {
        Transaction first = BuildBlobTransaction(fullProvider: true);
        Transaction second = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(1UL)
            .SignedAndResolved()
            .TestObject;

        _handler.SendNewTransactions([first, second], sendFullTx: false);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(message =>
            message.Hashes.SequenceEqual(new[] { first.Hash!, second.Hash! })
            && message.CellMask.SequenceEqual(BlobCellMask.Full.ToBytes())));
    }

    [Test]
    public void should_announce_persisted_light_v1_blob_tx_with_consensus_size()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        LightTransaction lightTx = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        _handler.SendNewTransactions([lightTx], sendFullTx: false);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.Sizes[0] == tx.GetLength(shouldCountBlobs: false) &&
            m.Sizes[0] < tx.GetLength()));
    }

    [Test]
    public void should_not_announce_legacy_light_v1_blob_tx_with_unknown_consensus_size()
    {
        // Entries persisted before the consensus-size field cannot make a spec-compliant announcement.
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        LightTransaction legacyLightTx = new(
            timestamp: tx.Timestamp,
            sender: tx.SenderAddress!,
            nonce: (ulong)tx.Nonce,
            hash: tx.Hash!,
            value: tx.Value,
            gasLimit: (ulong)tx.GasLimit,
            gasPrice: tx.GasPrice,
            maxFeePerGas: tx.DecodedMaxFeePerGas,
            maxFeePerBlobGas: tx.MaxFeePerBlobGas!.Value,
            blobVersionHashes: tx.BlobVersionedHashes!,
            poolIndex: tx.PoolIndex,
            size: tx.GetLength(),
            proofVersion: ProofVersion.V1,
            blobCellMask: BlobCellMask.Full,
            sparseBlobNetworkSize: 0);

        _handler.SendNewTransactions([legacyLightTx], sendFullTx: false);

        _session.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage72>());
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
    public void should_preserve_pending_mask_expanded_while_request_is_sent()
    {
        RecreateHandler();
        BlobCellMask initialCustodyMask = BlobCellMask.FromIndices([4]);
        BlobCellMask expandedCustodyMask = BlobCellMask.FromIndices([4, 5, 6]);
        _blobCustodyTracker.Update(initialCustodyMask);
        Transaction tx = BuildSamplerBlobTransaction();
        Hash256 hash = tx.Hash!;
        BlobCellMask initialRequestMask = _sparseBlobPoolPeerRegistry.GetRequestMask(hash, BlobCellMask.Full, 15);
        _transactionPool.IsKnown(hash).Returns(true);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [hash],
            BlobCellMask.Full.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>(), Is.Empty);

        ISparseBlobPoolPeer handlerPeer = _handler;
        Assert.That(_sparseBlobPoolPeerRegistry.RecordTransaction(handlerPeer, BuildElidedBlobTransaction(tx)), Is.Null);
        TestSparseBlobPeer temporaryProvider = new(TestItem.PublicKeyC);
        _sparseBlobPoolPeerRegistry.AddPeer(temporaryProvider);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(temporaryProvider, hash, BlobCellMask.Full);

        int localMaskLookups = 0;
        _transactionPool.TryGetPendingBlobCellMask(hash, out Arg.Any<BlobCellMask>())
            .Returns(call =>
            {
                call[1] = BlobCellMask.Empty;
                if (Interlocked.Increment(ref localMaskLookups) == 1)
                {
                    _sparseBlobPoolPeerRegistry.RemovePeer(temporaryProvider);
                }

                return false;
            });

        BlobCellMask expandedRequestMask = BlobCellMask.Empty;
        int expanded = 0;
        _session.When(s => s.DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 && m.Hashes[0] == hash)))
            .Do(_ =>
            {
                if (Interlocked.Exchange(ref expanded, 1) != 0)
                {
                    return;
                }

                _blobCustodyTracker.Update(expandedCustodyMask);
                expandedRequestMask = _sparseBlobPoolPeerRegistry.GetRequestMask(hash, BlobCellMask.Full, 15);
                using NewPooledTransactionHashesMessage72 expandedAnnouncement = new(
                    [(byte)TxType.Blob],
                    [1024],
                    [hash],
                    BlobCellMask.Full.ToBytes());
                HandleZeroMessage(expandedAnnouncement, Eth72MessageCode.NewPooledTransactionHashes);
            });

        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        BlobCellMask expectedRemainingMask = expandedRequestMask.Except(initialRequestMask);
        Assert.That(expectedRemainingMask.IsEmpty, Is.False);
        TestSparseBlobPeer retryProvider = new(TestItem.PublicKeyD);
        _sparseBlobPoolPeerRegistry.AddPeer(retryProvider);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(retryProvider, hash, BlobCellMask.Full);

        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expanded, Is.EqualTo(1));
            Assert.That(temporaryProvider.CellRequests, Is.Empty);
            Assert.That(retryProvider.CellRequests, Is.EqualTo(new[] { (hash, expectedRemainingMask) }));
        }
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

        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes), Throws.TypeOf<RlpException>());
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

    [TestCase(false)]
    [TestCase(true)]
    public void should_accept_non_blob_announcement_and_ignore_cell_mask(bool hasCellMask)
    {
        Hash256 hash = HashFromInt(1);
        _transactionPool.NotifyAboutTx(hash, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.EIP1559],
            [1024],
            [hash],
            (hasCellMask ? BlobCellMask.FromIndices([1]) : BlobCellMask.Empty).ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m =>
            m.EthMessage.Hashes.Count == 1 &&
            m.EthMessage.Hashes[0] == hash));
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetCellsMessage72>());
    }

    [Test]
    public void should_reject_announcement_above_peer_admission_limit()
    {
        const int count = NewPooledTransactionHashesMessage72.MaxCount + 1;
        byte[] types = new byte[count];
        int[] sizes = new int[count];
        Hash256[] hashes = new Hash256[count];
        Array.Fill(types, (byte)TxType.EIP1559);
        Array.Fill(sizes, 1024);
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = HashFromInt(i);
        }

        using NewPooledTransactionHashesMessage72 message = new(types, sizes, hashes, BlobCellMask.Empty.ToBytes());
        HandleIncomingStatusMessage();

        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes), Throws.TypeOf<RlpLimitException>());
    }

    [Test]
    public void should_not_request_sparse_transaction_when_announcement_quota_is_exhausted()
    {
        const int maxAnnouncementsPerPeer = 2048;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        ISparseBlobPoolPeer announcingPeer = _handler;
        int accepted = 0;
        for (int i = 0; i < maxAnnouncementsPerPeer; i++)
        {
            if (_sparseBlobPoolPeerRegistry.RecordAnnouncement(announcingPeer, HashFromInt(i), cellMask))
            {
                accepted++;
            }
        }

        Assert.That(accepted, Is.EqualTo(maxAnnouncementsPerPeer));

        Hash256 rejectedHash = HashFromInt(maxAnnouncementsPerPeer);
        _transactionPool.NotifyAboutTx(rejectedHash, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1024],
            [rejectedHash],
            cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        _transactionPool.DidNotReceive().NotifyAboutTx(rejectedHash, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
        Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage>().Any(m => m.EthMessage.Hashes.Contains(rejectedHash)), Is.False);
        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Any(m => m.Hashes.Contains(rejectedHash)), Is.False);
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
        BlobCellMask requestMask = BlobCellMask.FromIndices([2, 7]);
        _blobCustodyTracker.Update(requestMask);
        Transaction tx;
        ulong nonce = 0;
        do
        {
            tx = BuildSparseBlobTransaction(out _, out _, out _, nonce++);
        }
        while (_sparseBlobPoolPeerRegistry.GetRequestMask(tx.Hash!, BlobCellMask.Full, 15).IsFull);

        Hash256 hash = tx.Hash!;
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
        _transactionPool.Received(1).ValidateTxForBlobSampling(tx);
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetCellsMessage72>());

        TestSparseBlobPeer secondProvider = new(TestItem.PublicKeyC);
        _sparseBlobPoolPeerRegistry.AddPeer(secondProvider);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(secondProvider, hash, BlobCellMask.Full);
        BlobCellMask sampledMask = _sparseBlobPoolPeerRegistry.GetRequestMask(hash, BlobCellMask.Full, 15);
        Assert.That(_sparseBlobPoolPeerRegistry.TryRequestCells(hash, sampledMask, TestItem.PublicKeyD), Is.True);

        GetCellsMessage72? handlerRequest = _deliveredMessages.OfType<GetCellsMessage72>().LastOrDefault();
        (Hash256 Hash, BlobCellMask CellMask) request = secondProvider.CellRequests.Count == 1
            ? secondProvider.CellRequests[0]
            : (handlerRequest!.Hashes[0], BlobCellMask.FromBytes(handlerRequest.CellMask));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondProvider.CellRequests.Count + (handlerRequest is null ? 0 : 1), Is.EqualTo(1));
            Assert.That(request.Hash, Is.EqualTo(hash));
            Assert.That((sampledMask & requestMask), Is.EqualTo(requestMask));
            Assert.That(sampledMask.Except(requestMask).Count, Is.EqualTo(1));
            Assert.That(request.CellMask, Is.EqualTo(sampledMask));
        }
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
    public void should_elide_v0_blob_payload_in_pooled_transactions_response()
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
            ((ShardBlobNetworkWrapper)m.EthMessage.Transactions[0].NetworkWrapper!).Blobs.Length == 0 &&
            m.EthMessage.Transactions[0].GetLength() < fullTxLength));
    }

    [Test]
    public void should_accept_announced_v0_full_blob_pooled_response_and_reject_late_cells()
    {
        RecreateHandler(providerProbabilityPercent: 100);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Cancun.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        AnnounceBlobTransaction(tx.Hash!, tx.GetLength(shouldCountBlobs: false), TxType.Blob);
        long pooledRequestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);
        long requestId = GetLastGetCellsRequestId(tx.Hash!, BlobCellMask.Full);

        using PooledTransactionsMessage66 response = new(pooledRequestId, new PooledTransactionsMessage65(new[] { tx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        _transactionPool.Received(1).SubmitTx(
            Arg.Is<Transaction>(submitted => IsV0BlobTransaction(submitted, tx.Hash!)),
            TxHandlingOptions.None);

        using CellsMessage72 cells = new(
            requestId,
            [tx.Hash!],
            [[[]]],
            BlobCellMask.FromIndices([0]).ToBytes());
        Assert.That(() => HandleZeroMessage(cells, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());

        _transactionPool.DidNotReceive().MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());
    }

    [Test]
    public void should_announce_sparse_blob_tx_consensus_size()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        int fullTxLength = tx.GetLength();
        int consensusTxLength = tx.GetLength(shouldCountBlobs: false);

        _handler.SendNewTransaction(tx);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.Sizes.Length == 1 &&
            m.Sizes[0] == consensusTxLength &&
            m.Sizes[0] < fullTxLength));
    }

    [Test]
    public void should_reject_unsolicited_pooled_response_before_sampling_validation()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction elidedTx = BuildElidedBlobTransaction(tx);

        HandleIncomingStatusMessage();
        using PooledTransactionsMessage66 response = new(
            1111,
            new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));

        Assert.That(
            () => HandleZeroMessage(response, Eth66MessageCode.PooledTransactions),
            Throws.TypeOf<SubprotocolException>());

        _transactionPool.DidNotReceive().ValidateTxForBlobSampling(Arg.Any<Transaction>());
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }

    [Test]
    public void should_reject_duplicate_pooled_response_before_sampling_validation()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction elidedTx = BuildElidedBlobTransaction(tx);

        AnnounceBlobTransaction(tx.Hash!, elidedTx.GetLength(shouldCountBlobs: false), TxType.Blob);
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);
        using PooledTransactionsMessage66 emptyResponse = new(requestId, new PooledTransactionsMessage65(Array.Empty<Transaction>().ToPooledList()));
        HandleZeroMessage(emptyResponse, Eth66MessageCode.PooledTransactions);
        using PooledTransactionsMessage66 duplicateResponse = new(requestId, new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));

        Assert.That(
            () => HandleZeroMessage(duplicateResponse, Eth66MessageCode.PooledTransactions),
            Throws.TypeOf<SubprotocolException>());

        _transactionPool.DidNotReceive().ValidateTxForBlobSampling(Arg.Any<Transaction>());
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
        int announcedSize = tx.GetLength(shouldCountBlobs: false);
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
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);

        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));
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

        AnnounceBlobTransaction(tx.Hash!, txWithEmptySidecar.GetLength(shouldCountBlobs: false), TxType.Blob);
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);

        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { txWithEmptySidecar }.ToPooledList()));
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

        AnnounceBlobTransaction(tx.Hash!, txWithMismatchedCommitment.GetLength(shouldCountBlobs: false), TxType.Blob);
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);

        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { txWithMismatchedCommitment }.ToPooledList()));
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
    public void should_disconnect_peer_that_requests_cells_without_announcing_transactions()
    {
        HandleIncomingStatusMessage();
        typeof(Eth72ProtocolHandler)
            .GetField("_requestRatioWarmupEndsAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_handler, DateTimeOffset.MinValue);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1]);

        for (int i = 0; i < 4; i++)
        {
            using GetCellsMessage72 request = new(1000 + i, [HashFromInt(i)], requestedMask.ToBytes());
            HandleZeroMessage(request, Eth72MessageCode.GetCells);
        }

        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());

        using GetCellsMessage72 abusiveRequest = new(1004, [HashFromInt(4)], requestedMask.ToBytes());
        HandleZeroMessage(abusiveRequest, Eth72MessageCode.GetCells);

        _session.Received(1).InitiateDisconnect(
            DisconnectReason.UselessPeer,
            Arg.Is<string>(details => details.Contains("request-to-announce ratio exceeded", StringComparison.Ordinal)));
    }

    [Test]
    public void duplicate_blob_announcements_should_not_inflate_cell_request_allowance()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            BlobCellMask.Full.ToBytes());
        HandleIncomingStatusMessage();
        for (int i = 0; i < 10; i++)
        {
            HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        }

        typeof(Eth72ProtocolHandler)
            .GetField("_requestRatioWarmupEndsAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_handler, DateTimeOffset.MinValue);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1]);
        for (int i = 0; i < 5; i++)
        {
            using GetCellsMessage72 request = new(2000 + i, [HashFromInt(i)], requestedMask.ToBytes());
            HandleZeroMessage(request, Eth72MessageCode.GetCells);
        }

        _session.Received(1).InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
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
        _transactionPool.TryGetPendingBlobCellMask(tx.Hash!, out Arg.Any<BlobCellMask>())
            .Returns(x =>
            {
                x[1] = availableMask;
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
    public void should_cap_cells_response_when_request_exceeds_response_hash_limit()
    {
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1]);
        Hash256[] hashes = new Hash256[Eth72ProtocolHandler.MaxCellsResponseHashes * 2];
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
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == 1234 &&
            m.Hashes.Length == Eth72ProtocolHandler.MaxCellsResponseHashes));
    }

    [Test]
    public void should_cap_cells_response_at_soft_byte_limit()
    {
        BlobCellMask requestedMask = BlobCellMask.Full;
        Hash256 firstHash = HashFromInt(1);
        Hash256 secondHash = HashFromInt(2);
        byte[][] blobHashes = new byte[Eip7594Constants.MaxBlobsPerTx][];
        Array.Fill(blobHashes, new byte[Hash256.Size]);
        byte[][] cells = Enumerable.Range(0, BlobCellMask.CellCount * Eip7594Constants.MaxBlobsPerTx)
            .Select(static _ => new byte[CkzgLib.Ckzg.BytesPerCell])
            .ToArray();
        Transaction firstTx = new() { Type = TxType.Blob, Hash = firstHash, BlobVersionedHashes = blobHashes };
        Transaction secondTx = new() { Type = TxType.Blob, Hash = secondHash, BlobVersionedHashes = blobHashes };
        SetupGetCellsResponse(firstTx, requestedMask, requestedMask, cells);
        SetupGetCellsResponse(secondTx, requestedMask, requestedMask, cells);
        using GetCellsMessage72 request = new(1234, [firstHash, secondHash], requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == request.RequestId &&
            m.Hashes.SequenceEqual(new[] { firstHash }) &&
            m.Cells.Length == 1));
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public void should_bound_hash_lookups_for_cells_request()
    {
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1]);
        Hash256[] hashes = Enumerable.Range(0, Eth72ProtocolHandler.MaxCellsRequestHashes + 1)
            .Select(HashFromInt)
            .ToArray();
        using GetCellsMessage72 request = new(1234, hashes, requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _transactionPool.Received(Eth72ProtocolHandler.MaxCellsRequestHashes)
            .TryGetPendingBlobCellMetadata(
                Arg.Any<Hash256>(),
                out Arg.Any<BlobCellMask>(),
                out Arg.Any<int>(),
                out Arg.Any<int>());
        _transactionPool.DidNotReceive()
            .TryGetPendingBlobTransaction(Arg.Any<Hash256>(), out Arg.Any<Transaction>());
        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == request.RequestId &&
            m.Hashes.Length == 0));
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
    public void should_deduplicate_hashes_before_serving_get_cells()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask requestedMask = BlobCellMask.FromIndices([3]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, requestedMask, out byte[][] cells), Is.True);
        SetupGetCellsResponse(tx, requestedMask, requestedMask, cells);

        using GetCellsMessage72 request = new(1234, [tx.Hash!, tx.Hash!], requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == request.RequestId &&
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.Cells.Length == 1));
    }

    [Test]
    public void unavailable_cell_requests_should_not_exhaust_serving_quota()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([3]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, requestedMask, out byte[][] cells), Is.True);
        bool available = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(call =>
            {
                call[1] = tx;
                return true;
            });
        _transactionPool.TryGetPendingBlobCellMask(tx.Hash!, out Arg.Any<BlobCellMask>())
            .Returns(call =>
            {
                call[1] = available ? BlobCellMask.Full : BlobCellMask.Empty;
                return available;
            });
        _transactionPool.TryGetBlobCells(tx.Hash!, requestedMask, out Arg.Any<BlobCellMask>(), out Arg.Any<byte[][]>())
            .Returns(call =>
            {
                call[2] = requestedMask;
                call[3] = cells;
                return true;
            });

        HandleIncomingStatusMessage();
        for (int i = 0; i < 40; i++)
        {
            using GetCellsMessage72 unavailableRequest = new(1000 + i, [tx.Hash!], requestedMask.ToBytes());
            HandleZeroMessage(unavailableRequest, Eth72MessageCode.GetCells);
        }

        available = true;
        using GetCellsMessage72 availableRequest = new(2000, [tx.Hash!], requestedMask.ToBytes());
        HandleZeroMessage(availableRequest, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(message =>
            message.RequestId == availableRequest.RequestId
            && message.Hashes.SequenceEqual(new[] { tx.Hash! })
            && message.Cells.Length == 1));
        _transactionPool.DidNotReceive().TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>());
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
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        bool pendingTransactionAvailable = false;
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = pendingTransactionAvailable ? tx : null!;
                return pendingTransactionAvailable;
            });
        _transactionPool.MergeBlobCells(tx.Hash!, availableMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [availableCells], availableMask.ToBytes());
        pendingTransactionAvailable = true;
        HandleZeroMessage(response, Eth72MessageCode.Cells);

        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, availableMask, Arg.Is<byte[][]>(m =>
            m.Length == availableCells.Length &&
            m.Zip(availableCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_clear_sparse_registry_when_aggregate_local_mask_is_full()
    {
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        BlobCellMask responseMask = BlobCellMask.FromIndices([63]);
        _sparseBlobPoolPeerRegistry.AddPeer(peer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(peer, hash, BlobCellMask.Full);
        _transactionPool.TryGetPendingBlobCellMask(hash, out Arg.Any<BlobCellMask>())
            .Returns(call =>
            {
                call[1] = BlobCellMask.Full;
                return true;
            });

        typeof(Eth72ProtocolHandler)
            .GetMethod("OnPendingCellsApplied", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_handler, [hash, hash.ValueHash256, responseMask, BlobCellMask.Empty]);

        Assert.That(_sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(hash), Is.Zero);
    }

    [Test]
    public void should_back_off_partial_cell_responder()
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
        _transactionPool.MergeBlobCells(tx.Hash!, responseMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, requestedMask), [tx.Hash!], [responseCells], responseMask.ToBytes());
        HandleZeroMessage(response, Eth72MessageCode.Cells);

        _session.DidNotReceive().DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(missingMask.ToBytes())));

        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _session.DidNotReceive().DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(missingMask.ToBytes())));

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(missingMask.ToBytes())));
    }

    [Test]
    public void should_preserve_pending_cell_intent_until_delayed_transaction_arrives()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(BlobCellMask.FromIndices([0]));
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction elidedTx = BuildElidedBlobTransaction(tx);
        BlobCellMask announcementMask = BlobCellMask.FromIndices([0, 1]);
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        _transactionPool.ValidateTxForBlobSampling(elidedTx).Returns(AcceptTxResult.Accepted);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [elidedTx.GetLength(shouldCountBlobs: false)],
            [tx.Hash!],
            announcementMask.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        long pooledRequestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);
        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>(), Is.Empty);

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));
        TestSparseBlobPeer firstProvider = new(TestItem.PublicKeyC);
        TestSparseBlobPeer secondProvider = new(TestItem.PublicKeyD);
        _sparseBlobPoolPeerRegistry.AddPeer(firstProvider);
        _sparseBlobPoolPeerRegistry.AddPeer(secondProvider);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(firstProvider, tx.Hash!, BlobCellMask.Full);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(secondProvider, tx.Hash!, BlobCellMask.Full);

        using PooledTransactionsMessage66 response = new(
            pooledRequestId,
            new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        int requestCount = _deliveredMessages.OfType<GetCellsMessage72>().Count()
            + firstProvider.CellRequests.Count
            + secondProvider.CellRequests.Count;
        Assert.That(requestCount, Is.EqualTo(1));
    }

    [Test]
    public void should_not_re_request_from_sole_announcer_after_empty_cells_response()
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

        // The empty response drops the peer's announcement, so no request loop forms with the sole announcer.
        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(requestedMask.ToBytes())), Is.EqualTo(1));
    }

    [Test]
    public void should_not_request_cells_already_available_in_pool()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        _transactionPool.TryGetPendingBlobCellMask(tx.Hash!, out Arg.Any<BlobCellMask>())
            .Returns(x =>
            {
                x[1] = requestedMask;
                return true;
            });

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>(), Is.Empty);
    }

    [Test]
    public void should_not_duplicate_in_flight_cell_request_on_repeated_announcement()
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
        using NewPooledTransactionHashesMessage72 repeatedAnnouncement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        HandleZeroMessage(repeatedAnnouncement, Eth72MessageCode.NewPooledTransactionHashes);

        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(requestedMask.ToBytes())), Is.EqualTo(1));
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

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpException>());
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

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpException>());
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

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpException>());
        _transactionPool.DidNotReceive().MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());
    }

    [Test]
    public void should_reject_empty_cells_inside_advertised_mask()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = BuildBlobTransaction(fullProvider: false);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, requestedMask, out byte[][] responseCells), Is.True);
        responseCells[1] = [];

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

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpException>());
        _transactionPool.DidNotReceive().MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());
    }

    [Test]
    public void should_reject_cells_response_with_unmatched_request_id()
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
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>())
            .Returns(BlobCellMergeResult.InvalidCells);
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.TransactionUnavailable);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, cellMask) + 1, [tx.Hash!], [cells], cellMask.ToBytes());
        txAvailable = true;
        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());

        _transactionPool.DidNotReceive().MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());
    }

    [Test]
    public void should_reject_duplicate_cells_response()
    {
        RecreateHandler(providerProbabilityPercent: 100);
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        long requestId = GetLastGetCellsRequestId(tx.Hash!, cellMask);
        using CellsMessage72 first = new(requestId, [tx.Hash!], [cells], cellMask.ToBytes());
        using CellsMessage72 duplicate = new(requestId, [tx.Hash!], [cells], cellMask.ToBytes());

        HandleZeroMessage(first, Eth72MessageCode.Cells);

        Assert.That(() => HandleZeroMessage(duplicate, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void should_treat_post_admission_proof_failure_as_ambiguous()
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
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>())
            .Returns(BlobCellMergeResult.InvalidCells);

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
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
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
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>())
            .Returns(BlobCellMergeResult.InvalidCells);

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
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        Assert.That(otherPeer.Disconnects, Is.Empty);
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, cellMask)));
    }

    [Test]
    public void should_retain_cells_without_disconnect_when_merge_loses_txpool_race()
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
        bool mergeAvailable = false;
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>())
            .Returns(_ => mergeAvailable ? BlobCellMergeResult.Accepted : BlobCellMergeResult.TransactionUnavailable);

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
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        Assert.That(otherPeer.CellRequests, Is.Empty);

        mergeAvailable = true;
        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));
        _transactionPool.Received(2).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
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
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>())
            .Returns(_ => txAvailable ? BlobCellMergeResult.Accepted : BlobCellMergeResult.TransactionUnavailable);

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(cellMask.ToBytes())));

        using CellsMessage72 message = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        HandleZeroMessage(message, Eth72MessageCode.Cells);

        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());

        txAvailable = true;
        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _transactionPool.Received(2).MergeBlobCells(tx.Hash!, cellMask, Arg.Is<byte[][]>(m =>
            m.Length == cells.Length &&
            m.Zip(cells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_keep_outstanding_request_when_buffered_cells_are_applied()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);
        TestSparseBlobPeer cellsPeer = new(TestItem.PublicKeyC);
        _sparseBlobPoolPeerRegistry.AddPeer(cellsPeer);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        long requestId = GetLastGetCellsRequestId(tx.Hash!, cellMask);
        Assert.That(_sparseBlobPoolPeerRegistry.RecordCells(cellsPeer, tx.Hash!, cellMask, cells), Is.True);

        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        using CellsMessage72 response = new(requestId, [tx.Hash!], [cells], cellMask.ToBytes());
        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);
    }

    [Test]
    public void should_accept_cells_within_eth_response_timeout()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        long requestId = GetLastGetCellsRequestId(tx.Hash!, cellMask);

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));

        using CellsMessage72 response = new(requestId, [tx.Hash!], [cells], cellMask.ToBytes());
        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);
    }

    [Test]
    public void should_ignore_stale_cells_response_while_new_request_generation_is_active()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        long staleRequestId = GetLastGetCellsRequestId(tx.Hash!, cellMask);

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        long activeRequestId = GetLastGetCellsRequestId(tx.Hash!, cellMask);
        Assert.That(activeRequestId, Is.Not.EqualTo(staleRequestId));

        using CellsMessage72 staleResponse = new(staleRequestId, [tx.Hash!], [cells], cellMask.ToBytes());
        Assert.That(() => HandleZeroMessage(staleResponse, Eth72MessageCode.Cells), Throws.Nothing);
        _transactionPool.DidNotReceive().MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());

        using CellsMessage72 activeResponse = new(activeRequestId, [tx.Hash!], [cells], cellMask.ToBytes());
        HandleZeroMessage(activeResponse, Eth72MessageCode.Cells);
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void should_re_request_ambiguous_buffered_tuple_when_blob_tx_becomes_pending()
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
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.InvalidCells);

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

        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, cellMask)));
    }

    [Test]
    public void should_not_blame_buffered_cell_source_for_ambiguous_proof_tuple()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        BlobCellMask validMask = BlobCellMask.FromIndices([4]);
        BlobCellMask invalidMask = BlobCellMask.FromIndices([5]);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, validMask, out byte[][] validCells), Is.True);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, invalidMask, out byte[][] invalidCells), Is.True);
        TestSparseBlobPeer validPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer invalidPeer = new(TestItem.PublicKeyD);
        _sparseBlobPoolPeerRegistry.AddPeer(validPeer);
        _sparseBlobPoolPeerRegistry.AddPeer(invalidPeer);
        _transactionPool.MergeBlobCells(tx.Hash!, Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>())
            .Returns(call => call.ArgAt<BlobCellMask>(1) == validMask
                ? BlobCellMergeResult.Accepted
                : BlobCellMergeResult.InvalidCells);

        Assert.That(_sparseBlobPoolPeerRegistry.RecordCells(validPeer, tx.Hash!, validMask, validCells), Is.True);
        Assert.That(_sparseBlobPoolPeerRegistry.RecordCells(invalidPeer, tx.Hash!, invalidMask, invalidCells), Is.True);

        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validPeer.Disconnects, Is.Empty);
            Assert.That(invalidPeer.Disconnects, Is.Empty);
        }
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, validMask, Arg.Any<byte[][]>());
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, invalidMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void registry_should_forget_poisoned_transaction_after_ambiguous_failure_limit()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        registry.AddPeer(peer);
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>())
            .Returns(BlobCellMergeResult.InvalidCells);
        _transactionPool.RemoveTransaction(tx.Hash!).Returns(true);

        for (int i = 0; i < 3; i++)
        {
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);
            Assert.That(registry.TryApplyRecordedCells(tx.Hash!), Is.False);
        }

        _transactionPool.Received(1).RemoveTransaction(tx.Hash!);
        _transactionPool.Received(1).ForgetRejectedBlobTransaction(tx.Hash!);
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
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using CellsMessage72 message = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        HandleZeroMessage(message, Eth72MessageCode.Cells);

        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Is<byte[][]>(m =>
            m.Length == cells.Length &&
            m.Zip(cells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_not_drop_readded_sent_cell_request_when_trimming_stale_queue_entries()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);

        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
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
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);

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

        for (int i = 0; i < Eth72ProtocolHandler.MaxSentCellRequests; i++)
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

        _transactionPool.Received(2).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
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
        bool txAvailable = false;
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>())
            .Returns(_ => txAvailable ? BlobCellMergeResult.Accepted : BlobCellMergeResult.TransactionUnavailable);

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
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());

        txAvailable = true;
        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _transactionPool.Received(2).MergeBlobCells(tx.Hash!, cellMask, Arg.Is<byte[][]>(m =>
            m.Length == cells.Length &&
            m.Zip(cells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
        Assert.That(_sparseBlobPoolPeerRegistry.TryRequestCells(tx.Hash!, cellMask, TestItem.PublicKeyB), Is.True);
    }

    [Test]
    public void should_reject_unsolicited_cells()
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
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);

        using CellsMessage72 message = new([tx.Hash!], [cells], cellMask.ToBytes());

        HandleIncomingStatusMessage();
        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());

        txAvailable = true;
        _transactionPool.NewPending += Raise.EventWith(new TxEventArgs(tx));

        _transactionPool.DidNotReceive().MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void should_requeue_claimed_cells_when_background_scheduler_rejects()
    {
        RecreateHandler(backgroundTaskScheduler: new RejectingBackgroundTaskScheduler());
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        long firstRequestId = GetLastGetCellsRequestId(tx.Hash!, cellMask);

        using CellsMessage72 response = new(firstRequestId, [tx.Hash!], [cells], cellMask.ToBytes());
        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);

        GetCellsMessage72[] requests = _deliveredMessages
            .OfType<GetCellsMessage72>()
            .Where(m => m.Hashes.Length == 1 && m.Hashes[0] == tx.Hash)
            .ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Has.Length.EqualTo(2));
            Assert.That(requests[1].RequestId, Is.Not.EqualTo(firstRequestId));
        }
        _transactionPool.DidNotReceive().MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void should_retry_received_cells_after_registry_capacity_rejects_them()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        registry.TryRequestCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<PublicKey>()).Returns(true);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)tx.NetworkWrapper!, cellMask, out byte[][] cells), Is.True);

        HandleIncomingStatusMessage();
        Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(tx.Hash!, cellMask), Is.True);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [tx.Hash!], [cells], cellMask.ToBytes());
        HandleZeroMessage(response, Eth72MessageCode.Cells);
        registry.Received(1).RecordCells(_handler, tx.Hash!, cellMask, Arg.Any<byte[][]>());
        registry.Received(1).RemoveAnnouncement(_handler, tx.Hash!);

        registry.ClearReceivedCalls();
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10));

        registry.Received(1).RecordAnnouncement(_handler, tx.Hash!, cellMask);
        registry.Received(1).TryRequestCells(tx.Hash!, cellMask, Arg.Any<PublicKey>());
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
        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.Cells), Throws.Nothing);
        _session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure, Arg.Any<string>());
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
    public void registry_should_not_reserve_two_concurrent_requests_for_same_transaction_to_same_peer()
    {
        ManualTimerFactory timerFactory = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);
        TestSparseBlobPeer firstPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer secondPeer = new(TestItem.PublicKeyD);
        Hash256 hash = HashFromInt(1);
        BlobCellMask firstMask = BlobCellMask.FromIndices([4]);
        BlobCellMask secondMask = BlobCellMask.FromIndices([9]);
        registry.AddPeer(firstPeer);
        registry.AddPeer(secondPeer);
        registry.RecordAnnouncement(firstPeer, hash, BlobCellMask.Full);
        registry.RecordAnnouncement(secondPeer, hash, BlobCellMask.Full);

        Assert.That(registry.TryRequestCells(hash, firstMask, secondPeer.Id), Is.True);
        Assert.That(registry.TryRequestCells(hash, secondMask, secondPeer.Id), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstPeer.CellRequests, Is.EqualTo(new[] { (hash, firstMask) }));
            Assert.That(secondPeer.CellRequests, Is.EqualTo(new[] { (hash, secondMask) }));
        }
    }

    [Test]
    public async Task registry_should_ignore_stale_failed_send_after_new_reservation()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        using ManualResetEventSlim firstSendEntered = new();
        using ManualResetEventSlim releaseFirstSend = new();
        int sendCount = 0;
        peer.CellRequestHandler = (_, _) =>
        {
            if (Interlocked.Increment(ref sendCount) != 1)
            {
                return true;
            }

            firstSendEntered.Set();
            if (!releaseFirstSend.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting to release the stale cell request send.");
            }

            return false;
        };
        Hash256 hash = HashFromInt(1);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, hash, BlobCellMask.Full);

        Task<bool> staleSend = Task.Run(() => registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA));
        Assert.That(firstSendEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        registry.OnCellsRequestCompleted(hash, cellMask, peer);
        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
        releaseFirstSend.Set();
        Assert.That(await staleSend, Is.False);
        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
        Assert.That(peer.CellRequests, Has.Count.EqualTo(2));

        registry.OnCellsRequestCompleted(hash, cellMask, peer);
        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
        Assert.That(peer.CellRequests, Has.Count.EqualTo(3));
    }

    [Test]
    public void registry_should_retry_another_announcer_when_selected_peer_closes_before_reservation()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer closingPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer fallbackPeer = new(TestItem.PublicKeyD);
        Hash256 hash = HashFromInt(1);
        BlobCellMask fallbackMask = BlobCellMask.FromIndices([4]);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([4, 9]);
        registry.AddPeer(closingPeer);
        registry.AddPeer(fallbackPeer);
        registry.RecordAnnouncement(closingPeer, hash, requestedMask);
        registry.RecordAnnouncement(fallbackPeer, hash, fallbackMask);
        int closingChecks = 0;
        closingPeer.IsClosingHandler = () => Interlocked.Increment(ref closingChecks) > 1;

        Assert.That(registry.TryRequestCells(hash, requestedMask, TestItem.PublicKeyA), Is.False);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(closingPeer.CellRequests, Is.Empty);
            Assert.That(fallbackPeer.CellRequests, Is.EqualTo(new[] { (hash, fallbackMask) }));
        }
    }

    [Test]
    public void registry_should_ignore_stale_completion_from_non_inflight_peer()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer stalePeer = new(TestItem.PublicKeyD);
        Hash256 hash = HashFromInt(1);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, hash, cellMask);

        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
        registry.OnCellsRequestCompleted(hash, cellMask, stalePeer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
            Assert.That(peer.CellRequests, Is.EqualTo(new[] { (hash, cellMask) }));
        }
    }

    [Test]
    public void registry_should_not_evict_valid_state_for_quota_rejected_announcements()
    {
        const int maxAnnouncementsPerPeer = 2048;
        const int rejectedAnnouncementsToReachTrackedStateLimit = 6144;
        ManualTimerFactory timerFactory = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);
        TestSparseBlobPeer trustedPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer quotaPeer = new(TestItem.PublicKeyD);
        Hash256 trustedHash = HashFromInt(int.MinValue);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        registry.AddPeer(trustedPeer);
        registry.AddPeer(quotaPeer);
        Assert.That(registry.RecordAnnouncement(trustedPeer, trustedHash, cellMask), Is.True);

        int accepted = 0;
        for (int i = 0; i < maxAnnouncementsPerPeer; i++)
        {
            if (registry.RecordAnnouncement(quotaPeer, HashFromInt(i), cellMask))
            {
                accepted++;
            }
        }

        int rejected = 0;
        for (int i = maxAnnouncementsPerPeer; i < maxAnnouncementsPerPeer + rejectedAnnouncementsToReachTrackedStateLimit; i++)
        {
            if (!registry.RecordAnnouncement(quotaPeer, HashFromInt(i), cellMask))
            {
                rejected++;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(maxAnnouncementsPerPeer));
            Assert.That(rejected, Is.EqualTo(rejectedAnnouncementsToReachTrackedStateLimit));
        }
        Assert.That(registry.TryRequestCells(trustedHash, cellMask, quotaPeer.Id), Is.True);
        Assert.That(trustedPeer.CellRequests, Is.EqualTo(new[] { (trustedHash, cellMask) }));
    }

    [Test]
    public void registry_should_request_only_expanded_custody_delta()
    {
        BlobCustodyTracker custodyTracker = new();
        SparseBlobPoolPeerRegistry registry = new(_transactionPool, custodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        BlobCellMask firstMask = BlobCellMask.FromIndices([4]);
        BlobCellMask expandedMask = BlobCellMask.FromIndices([4, 9]);

        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, hash, BlobCellMask.Full);

        Assert.That(custodyTracker.Update(firstMask), Is.True);
        registry.OnCellsRequestCompleted(hash, firstMask, peer);
        Assert.That(custodyTracker.Update(expandedMask), Is.True);

        Assert.That(peer.CellRequests, Has.Count.EqualTo(2));
        Assert.That(peer.CellRequests[0], Is.EqualTo((hash, firstMask)));
        Assert.That(peer.CellRequests[1], Is.EqualTo((hash, BlobCellMask.FromIndices([9]))));
    }

    [Test]
    public void registry_should_process_custody_updates_outside_update_caller()
    {
        BlobCustodyTracker custodyTracker = new();
        QueuedBackgroundTaskScheduler scheduler = new();
        ManualTimerFactory timerFactory = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            custodyTracker,
            scheduler,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        BlobCellMask custodyMask = BlobCellMask.FromIndices([4]);
        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, hash, BlobCellMask.Full);

        custodyTracker.Update(custodyMask);

        Assert.That(peer.CellRequests, Is.Empty);
        scheduler.RunNext();
        Assert.That(peer.CellRequests, Is.EqualTo(new[] { (hash, custodyMask) }));
    }

    [Test]
    public async Task registry_should_ignore_stale_out_of_order_custody_callback()
    {
        BlobCustodyTracker custodyTracker = new();
        BlobCellMask staleMask = BlobCellMask.FromIndices([4]);
        BlobCellMask currentMask = BlobCellMask.FromIndices([9]);
        using ManualResetEventSlim staleCallbackEntered = new();
        using ManualResetEventSlim releaseStaleCallback = new();
        custodyTracker.CustodyChanged += (_, mask) =>
        {
            if (mask == staleMask)
            {
                staleCallbackEntered.Set();
                releaseStaleCallback.Wait(TimeSpan.FromSeconds(5));
            }
        };
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            custodyTracker,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, hash, BlobCellMask.Full);

        Task<bool> staleUpdate = Task.Run(() => custodyTracker.Update(staleMask));
        Assert.That(staleCallbackEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        try
        {
            custodyTracker.Update(currentMask);
        }
        finally
        {
            releaseStaleCallback.Set();
        }

        Assert.That(await staleUpdate.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(peer.CellRequests, Is.EqualTo(new[] { (hash, currentMask) }));
    }

    [Test]
    public void registry_should_remove_tracked_announcements_when_peer_is_removed()
    {
        ManualTimerFactory timerFactory = new();
        BlobCustodyTracker custodyTracker = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            custodyTracker,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);
        TestSparseBlobPeer removedPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer remainingPeer = new(TestItem.PublicKeyD);
        Hash256 hash = HashFromInt(1);
        BlobCellMask remainingMask = BlobCellMask.FromIndices([4]);

        registry.AddPeer(removedPeer);
        registry.AddPeer(remainingPeer);
        registry.RecordAnnouncement(removedPeer, hash, BlobCellMask.Full);
        registry.RecordAnnouncement(remainingPeer, hash, remainingMask);

        Assert.That(registry.GetFullProviderAnnouncementCount(hash), Is.EqualTo(1));

        registry.RemovePeer(removedPeer);
        registry.RecordAnnouncement(removedPeer, hash, BlobCellMask.Full);

        Assert.That(registry.GetFullProviderAnnouncementCount(hash), Is.Zero);
        Assert.That(custodyTracker.Update(BlobCellMask.Full), Is.True);
        Assert.That(removedPeer.CellRequests, Is.Empty);
        Assert.That(remainingPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(remainingPeer.CellRequests[0], Is.EqualTo((hash, remainingMask)));
    }

    [Test]
    public void registry_should_not_remove_replacement_connection_when_old_handler_disposes()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer oldPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer replacementPeer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        registry.AddPeer(oldPeer);
        Assert.That(registry.RecordAnnouncement(oldPeer, hash, cellMask), Is.True);

        registry.AddPeer(replacementPeer);
        Assert.That(registry.RecordAnnouncement(replacementPeer, hash, cellMask), Is.True);
        registry.RemovePeer(oldPeer);

        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
        registry.OnCellsRequestCompleted(hash, cellMask, oldPeer);
        registry.RemoveAnnouncement(oldPeer, hash);
        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(oldPeer.CellRequests, Is.Empty);
            Assert.That(replacementPeer.CellRequests, Is.EqualTo(new[] { (hash, cellMask) }));
        }

        registry.OnCellsRequestCompleted(hash, cellMask, replacementPeer);
        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
        Assert.That(replacementPeer.CellRequests, Has.Count.EqualTo(2));
    }

    [Test]
    public void registry_should_release_historical_peer_hash_tracking_when_resources_are_removed()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer trackedPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer retainingPeer = new(TestItem.PublicKeyD);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        registry.AddPeer(trackedPeer);
        registry.AddPeer(retainingPeer);
        Assert.That(registry.RecordAnnouncement(trackedPeer, HashFromInt(0), cellMask), Is.True);

        for (int i = 1; i <= 100; i++)
        {
            Hash256 hash = HashFromInt(i);
            Assert.That(registry.RecordAnnouncement(trackedPeer, hash, cellMask), Is.True);
            Assert.That(registry.RecordAnnouncement(retainingPeer, hash, cellMask), Is.True);
            registry.RemoveAnnouncement(trackedPeer, hash);
        }

        Assert.That(GetTrackedHashCount(registry, trackedPeer.Id), Is.EqualTo(1));
    }

    [Test]
    public void registry_should_reject_sparse_transaction_from_removed_peer()
    {
        Transaction tx = BuildSparseBlobTransaction(out _, out _);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        registry.AddPeer(peer);
        registry.RemovePeer(peer);

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);

        Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.False);
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void registry_should_release_buffered_transaction_when_source_peer_is_removed()
    {
        Transaction tx = BuildSparseBlobTransaction(out _, out _);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.ValidateTxForBlobSampling(tx).Returns(AcceptTxResult.Accepted);
        registry.AddPeer(peer);

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.True);

        registry.RemovePeer(peer);

        Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.False);
    }

    [Test]
    public void registry_should_strip_removed_peer_cells_and_preserve_other_sources()
    {
        Transaction tx = BuildSparseBlobTransaction(out _, out _, out byte[][] fullCells);
        BlobCellMask removedMask = BlobCellMask.FromIndices([4]);
        BlobCellMask retainedMask = BlobCellMask.FromIndices([5]);
        byte[][] removedCells = BlobCellsHelper.SelectFlattenedCells(fullCells, BlobCellMask.Full, removedMask, 1);
        byte[][] retainedCells = BlobCellsHelper.SelectFlattenedCells(fullCells, BlobCellMask.Full, retainedMask, 1);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer removedPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer retainedPeer = new(TestItem.PublicKeyD);
        _transactionPool.ValidateTxForBlobSampling(tx).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        registry.AddPeer(removedPeer);
        registry.AddPeer(retainedPeer);
        Assert.That(registry.RecordCells(removedPeer, tx.Hash!, removedMask, removedCells), Is.True);
        Assert.That(registry.RecordCells(retainedPeer, tx.Hash!, retainedMask, retainedCells), Is.True);

        registry.RemovePeer(removedPeer);
        Assert.That(registry.RecordTransaction(retainedPeer, tx), Is.EqualTo(AcceptTxResult.Accepted));

        _transactionPool.Received(1).SubmitTx(
            Arg.Is<Transaction>(submitted => HasExpectedSparseCells(submitted, tx.Hash!, retainedMask, retainedCells.Length)),
            TxHandlingOptions.None);
    }

    [Test]
    public void registry_should_not_record_transaction_that_fails_sampling_validation()
    {
        Transaction tx = BuildSparseBlobTransaction(out _, out _);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.ValidateTxForBlobSampling(tx).Returns(AcceptTxResult.Invalid);
        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, tx.Hash!, BlobCellMask.Full);

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);

        Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.False);
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        Assert.That(peer.CellRequests, Is.Empty);
    }

    [Test]
    public void registry_should_discard_ordinary_invalid_transaction_without_disconnect()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.ValidateTxForBlobSampling(tx).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Invalid);
        registry.AddPeer(peer);

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);

        Assert.That(peer.Disconnects, Is.Empty);
        Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.False);
    }

    [Test]
    public void registry_should_submit_sparse_blob_tx_only_after_valid_cells_arrive()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        ManualTimerFactory timerFactory = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);
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

        Assert.That(submitted, Is.True);
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
    public void registry_should_reject_null_early_cell()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out _);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        registry.AddPeer(peer);

        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, [null!]), Is.False);
    }

    [Test]
    public void registry_should_disconnect_when_transaction_and_invalid_cells_have_same_source()
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
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.InvalidBlobProofs);

        registry.AddPeer(peer);
        registry.AddPeer(otherPeer);
        registry.RecordAnnouncement(otherPeer, tx.Hash!, cellMask);
        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, invalidCells), Is.True);

        _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        Assert.That(peer.Disconnects, Has.Count.EqualTo(1));
        Assert.That(peer.Disconnects[0].Reason, Is.EqualTo(DisconnectReason.BreachOfProtocol));
        Assert.That(otherPeer.Disconnects, Is.Empty);
        Assert.That(otherPeer.CellRequests, Is.EqualTo(new[] { (tx.Hash!, cellMask) }));
        Assert.That(otherPeer.TransactionRequests, Is.EqualTo(new[] { tx.Hash! }));
        _transactionPool.Received(1).ForgetRejectedBlobTransaction(tx.Hash!);
    }

    [Test]
    public void registry_should_not_invoke_disconnect_callback_under_transaction_state_lock()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        tx.BlobVersionedHashes = [];
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer txPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer cellPeer = new(TestItem.PublicKeyA);
        bool reentrantMutationCompleted = false;
        txPeer.Disconnecting = () =>
        {
            Task<bool> mutation = Task.Run(() => registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask));
            reentrantMutationCompleted = mutation.Wait(TimeSpan.FromSeconds(2));
        };
        registry.AddPeer(txPeer);
        registry.AddPeer(cellPeer);
        registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask);
        Assert.That(registry.RecordTransaction(txPeer, tx), Is.Null);

        Assert.That(registry.RecordCells(cellPeer, tx.Hash!, cellMask, cells), Is.True);

        Assert.That(reentrantMutationCompleted, Is.True);
    }

    [Test]
    public void registry_should_merge_disjoint_early_cells_with_source_provenance()
    {
        Transaction tx = BuildSparseBlobTransaction(out _, out _, out byte[][] fullCells);
        BlobCellMask firstMask = BlobCellMask.FromIndices([4]);
        BlobCellMask secondMask = BlobCellMask.FromIndices([9]);
        byte[][] firstCells = BlobCellsHelper.SelectFlattenedCells(fullCells, BlobCellMask.Full, firstMask, tx.BlobVersionedHashes!.Length);
        byte[][] secondCells = BlobCellsHelper.SelectFlattenedCells(fullCells, BlobCellMask.Full, secondMask, tx.BlobVersionedHashes.Length);
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer firstPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer secondPeer = new(TestItem.PublicKeyA);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

        registry.AddPeer(firstPeer);
        registry.AddPeer(secondPeer);
        Assert.That(registry.RecordCells(firstPeer, tx.Hash!, firstMask, firstCells), Is.True);
        Assert.That(registry.RecordCells(secondPeer, tx.Hash!, secondMask, secondCells), Is.True);

        Assert.That(registry.RecordTransaction(firstPeer, tx), Is.EqualTo(AcceptTxResult.Accepted));
        _transactionPool.Received(1).SubmitTx(
            Arg.Is<Transaction>(submittedTx => HasExpectedSparseCells(
                submittedTx,
                tx.Hash!,
                firstMask | secondMask,
                firstCells.Length + secondCells.Length)),
            TxHandlingOptions.None);
    }

    [Test]
    public void registry_should_retry_without_disconnect_when_sparse_proof_tuple_is_invalid()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        CorruptSparseBlobProof(tx, cellMask);

        SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer txPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer cellPeer = new(TestItem.PublicKeyA);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.InvalidBlobProofs);

        registry.AddPeer(txPeer);
        registry.AddPeer(cellPeer);
        registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask);
        Assert.That(registry.RecordTransaction(txPeer, tx), Is.Null);
        Assert.That(registry.RecordCells(cellPeer, tx.Hash!, cellMask, cells), Is.True);

        _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        Assert.That(txPeer.Disconnects, Is.Empty);
        Assert.That(cellPeer.Disconnects, Is.Empty);
        Assert.That(cellPeer.CellRequests, Has.Count.EqualTo(1));
        Assert.That(cellPeer.CellRequests[0], Is.EqualTo((tx.Hash!, cellMask)));
    }

    [Test]
    public void registry_should_allow_valid_tuple_after_ambiguous_proof_failure()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer txPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer cellPeer = new(TestItem.PublicKeyA);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None)
            .Returns(AcceptTxResult.InvalidBlobProofs, AcceptTxResult.Accepted);
        registry.AddPeer(txPeer);
        registry.AddPeer(cellPeer);
        registry.RecordAnnouncement(txPeer, tx.Hash!, cellMask);
        registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask);

        Assert.That(registry.RecordTransaction(txPeer, tx), Is.Null);
        Assert.That(registry.RecordCells(cellPeer, tx.Hash!, cellMask, cells), Is.True);
        Assert.That(registry.RecordTransaction(cellPeer, tx), Is.Null);
        Assert.That(registry.RecordCells(txPeer, tx.Hash!, cellMask, cells), Is.True);

        _transactionPool.Received(1).ForgetRejectedBlobTransaction(tx.Hash!);
        _transactionPool.Received(2).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void registry_should_retry_already_known_tuple_missing_from_blob_pool()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None)
            .Returns(AcceptTxResult.AlreadyKnown, AcceptTxResult.Accepted);
        registry.AddPeer(peer);

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);

        _transactionPool.Received(1).ForgetRejectedBlobTransaction(tx.Hash!);
        _transactionPool.Received(2).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void registry_should_retain_tuple_when_retry_is_concurrently_re_poisoned()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        ManualTimerFactory timerFactory = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ITimestamper timestamper = Substitute.For<ITimestamper>();
        timestamper.UtcNowOffset.Returns(_ => now);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory,
            timestamper: timestamper);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None)
            .Returns(AcceptTxResult.AlreadyKnown, AcceptTxResult.AlreadyKnown, AcceptTxResult.Accepted);
        registry.AddPeer(peer);

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);
        _transactionPool.Received(2).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        _transactionPool.Received(2).ForgetRejectedBlobTransaction(tx.Hash!);

        now += TimeSpan.FromSeconds(2);
        timerFactory.Timer.Fire();

        _transactionPool.Received(3).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void registry_should_keep_sparse_tx_tracked_without_upgrading_sampler_to_full()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        ManualTimerFactory timerFactory = new();
        BlobCustodyTracker custodyTracker = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            custodyTracker,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        bool submitted = false;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(_ =>
        {
            submitted = true;
            return AcceptTxResult.Accepted;
        });

        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, tx.Hash!, BlobCellMask.Full);

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);

        Assert.That(submitted, Is.True);
        _transactionPool.TryGetPendingBlobCellMask(tx.Hash!, out Arg.Any<BlobCellMask>())
            .Returns(x =>
            {
                x[1] = cellMask;
                return true;
            });
        timerFactory.Timer.Fire();
        Assert.That(peer.CellRequests, Is.Empty);

        BlobCellMask expandedCustody = cellMask | BlobCellMask.FromIndices([9]);
        Assert.That(custodyTracker.Update(expandedCustody), Is.True);
        Assert.That(peer.CellRequests, Is.EqualTo(new[] { (tx.Hash!, BlobCellMask.FromIndices([9])) }));
    }

    [Test]
    public void registry_should_not_expire_submitted_state_after_updated_announcement()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ITimestamper timestamper = Substitute.For<ITimestamper>();
        timestamper.UtcNowOffset.Returns(_ => now);
        ManualTimerFactory timerFactory = new();
        BlobCustodyTracker custodyTracker = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            custodyTracker,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory,
            timestamper: timestamper);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.ValidateTxForBlobSampling(tx).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        _transactionPool.TryGetPendingBlobCellMask(tx.Hash!, out Arg.Any<BlobCellMask>())
            .Returns(call =>
            {
                call[1] = cellMask;
                return true;
            });
        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, tx.Hash!, cellMask);
        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);

        Assert.That(registry.RecordAnnouncement(peer, tx.Hash!, BlobCellMask.Full), Is.True);
        now += TimeSpan.FromMinutes(2);
        timerFactory.Timer.Fire();
        BlobCellMask expandedCustody = cellMask | BlobCellMask.FromIndices([9]);
        Assert.That(custodyTracker.Update(expandedCustody), Is.True);

        Assert.That(peer.CellRequests, Is.EqualTo(new[] { (tx.Hash!, BlobCellMask.FromIndices([9])) }));
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
    public void registry_should_not_request_full_cells_after_sampler_submit()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        ManualTimerFactory timerFactory = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);
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

        timerFactory.Timer.Fire();

        Assert.That(firstFullPeer.CellRequests, Is.Empty);
        Assert.That(secondFullPeer.CellRequests, Is.Empty);
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
        ManualTimerFactory timerFactory = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);
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

        timerFactory.Timer.Fire();

        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
    }

    [Test]
    public void registry_should_not_evict_local_full_cell_tx_from_announcement_only_state()
    {
        Transaction tx = BuildSparseBlobTransaction(out _, out _, out byte[][] fullCells);
        tx.NetworkWrapper = ((ShardBlobNetworkWrapper)tx.NetworkWrapper!) with { CellMask = BlobCellMask.Full, Cells = fullCells };
        ManualTimerFactory timerFactory = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);
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

        timerFactory.Timer.Fire();

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

    private long GetLastGetPooledTransactionsRequestId(Hash256 hash)
        => _deliveredMessages
            .OfType<GetPooledTransactionsMessage>()
            .Last(m => m.EthMessage.Hashes.Contains(hash))
            .RequestId;

    private void RecreateHandler(
        int providerProbabilityPercent = 15,
        IBackgroundTaskScheduler? backgroundTaskScheduler = null,
        ISparseBlobPoolPeerRegistry? sparseBlobPoolPeerRegistry = null)
    {
        _handler.Dispose();
        _txPoolConfig.SparseBlobProviderProbabilityPercent.Returns(providerProbabilityPercent);
        _handler = new Eth72ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            backgroundTaskScheduler ?? RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            _txPoolConfig,
            _specProvider,
            _blobCustodyTracker,
            sparseBlobPoolPeerRegistry ?? _sparseBlobPoolPeerRegistry,
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

    private Transaction BuildSamplerBlobTransaction()
    {
        for (int nonce = 0; nonce < 4096; nonce++)
        {
            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithNonce((ulong)nonce)
                .SignedAndResolved()
                .TestObject;
            if (!_sparseBlobPoolPeerRegistry.GetRequestMask(tx.Hash!, BlobCellMask.Full, 15).IsFull)
            {
                return tx;
            }
        }

        throw new AssertionException("Could not build a sampler blob transaction.");
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

    private static int GetTrackedHashCount(SparseBlobPoolPeerRegistry registry, PublicKey peerId)
    {
        FieldInfo peerUsageField = typeof(SparseBlobPoolPeerRegistry).GetField(
            "_peerUsage",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IDictionary peerUsages = (IDictionary)peerUsageField.GetValue(registry)!;
        object peerUsage = peerUsages[peerId]!;
        PropertyInfo trackedHashesProperty = peerUsage.GetType().GetProperty("TrackedHashes")!;
        return ((ICollection)trackedHashesProperty.GetValue(peerUsage)!).Count;
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
        _transactionPool.TryGetPendingBlobCellMetadata(
                tx.Hash!,
                out Arg.Any<BlobCellMask>(),
                out Arg.Any<int>(),
                out Arg.Any<int>())
            .Returns(x =>
            {
                x[1] = availableMask;
                x[2] = tx.BlobVersionedHashes?.Length ?? 0;
                x[3] = 0;
                return true;
            });
        _transactionPool.TryGetPendingBlobCellMask(tx.Hash!, out Arg.Any<BlobCellMask>())
            .Returns(x =>
            {
                x[1] = availableMask;
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

    private static Transaction BuildSparseBlobTransaction(
        out BlobCellMask cellMask,
        out byte[][] cells,
        out byte[][] fullCells,
        ulong nonce = 0)
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(nonce)
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
        private bool _isClosing;

        public PublicKey Id { get; } = id;
        public bool IsClosing
        {
            get => IsClosingHandler?.Invoke() ?? _isClosing;
            set => _isClosing = value;
        }
        public Func<bool>? IsClosingHandler { get; set; }
        public List<(Hash256 Hash, BlobCellMask CellMask)> CellRequests { get; } = [];
        public Func<Hash256, BlobCellMask, bool>? CellRequestHandler { get; set; }
        public List<Hash256> TransactionRequests { get; } = [];
        public List<(DisconnectReason Reason, string Details)> Disconnects { get; } = [];
        public Action? Disconnecting { get; set; }

        public bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask)
        {
            CellRequests.Add((hash, requestMask));
            return CellRequestHandler?.Invoke(hash, requestMask) ?? true;
        }

        public bool TrySendPooledTransactionRequest(Hash256 hash)
        {
            TransactionRequests.Add(hash);
            return true;
        }

        public void MaintainSparseBlobState(DateTimeOffset now)
        {
        }

        public void DisconnectSparseBlobPeer(DisconnectReason reason, string details)
        {
            Disconnecting?.Invoke();
            Disconnects.Add((reason, details));
        }
    }

    private sealed class RejectingBackgroundTaskScheduler : IBackgroundTaskScheduler
    {
        public bool TryScheduleTask<TReq>(
            TReq request,
            Func<TReq, CancellationToken, Task> fulfillFunc,
            TimeSpan? timeout = null,
            string? source = null) => false;
    }

    private sealed class QueuedBackgroundTaskScheduler : IBackgroundTaskScheduler
    {
        private Action? _next;

        public bool TryScheduleTask<TReq>(
            TReq request,
            Func<TReq, CancellationToken, Task> fulfillFunc,
            TimeSpan? timeout = null,
            string? source = null)
        {
            _next = () => fulfillFunc(request, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }

        public void RunNext()
        {
            Action next = Interlocked.Exchange(ref _next, null)
                ?? throw new InvalidOperationException("No background task is queued.");
            next();
        }
    }

    private sealed class ManualTimerFactory : ITimerFactory
    {
        public ManualTimer Timer { get; } = new();

        public Nethermind.Core.Timers.ITimer CreateTimer(TimeSpan interval)
        {
            Timer.Interval = interval;
            return Timer;
        }
    }

    private sealed class ManualTimer : Nethermind.Core.Timers.ITimer
    {
        public bool AutoReset { get; set; }
        public bool Enabled { get; set; }
        public TimeSpan Interval { get; set; }
        public double IntervalMilliseconds
        {
            get => Interval.TotalMilliseconds;
            set => Interval = TimeSpan.FromMilliseconds(value);
        }

        public event EventHandler? Elapsed;

        public void Start() => Enabled = true;
        public void Stop() => Enabled = false;
        public void Dispose() => Enabled = false;

        public void Fire()
        {
            Assert.That(Enabled, Is.True);
            Elapsed?.Invoke(this, EventArgs.Empty);
        }
    }
}
