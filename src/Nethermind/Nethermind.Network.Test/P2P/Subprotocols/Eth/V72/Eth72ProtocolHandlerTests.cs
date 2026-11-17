// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
using Nethermind.Core.Collections;
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
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.P2P.Utils;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.FluentRlp;
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
using TransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.TransactionsMessage;
using RecordingBackgroundTaskScheduler = Nethermind.Network.Test.P2P.Subprotocols.Eth.V66.Eth66ProtocolHandlerTests.RecordingBackgroundTaskScheduler;
using Autofac;
using DotNetty.Buffers;
using DotNetty.Common.Concurrency;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Config;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Threading;
using Nethermind.Init.Modules;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Test.Rlpx.TestWrappers;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.PooledTransactionRetryCache;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;

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
    private IChainHeadSpecProvider _specProvider = null!;
    private ITxPoolConfig _txPoolConfig = null!;
    private Block _genesisBlock = null!;
    private Eth72ProtocolHandler _handler = null!;
    private ITxGossipPolicy _txGossipPolicy = null!;
    private ITimerFactory _timerFactory = null!;
    private CompositeDisposable _disposables = null!;
    private BlobCustodyTracker _blobCustodyTracker = null!;
    private SparseBlobPoolPeerRegistry _sparseBlobPoolPeerRegistry = null!;
    private const int MaxSamplerTransactionAttempts = 64;
    private TestSparseBlobPeer _idlePeer = null!;
    private List<P2PMessage> _deliveredMessages = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = Substitute.For<IChainHeadSpecProvider>();
        _svc = Build.A.SerializationService().WithEth72(_specProvider).TestObject;

        _disposables = [];
        _deliveredMessages = [];
        _session = Substitute.For<ISession>();
        _session.InboundReceipts.Returns(new PacketReceiptTracker());
        _session.IsChannelWritable.Returns(true);
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
        _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(call =>
        {
            using DisposableByteBuffer encoded = _svc.ZeroSerialize(call.Arg<GetPooledTransactionsMessage>()).AsDisposable();
            return encoded.ReadableBytes;
        });
        _session.DeliverMessage(Arg.Any<GetCellsMessage72>()).Returns(call =>
        {
            using DisposableByteBuffer encoded = _svc.ZeroSerialize(call.Arg<GetCellsMessage72>()).AsDisposable();
            return encoded.ReadableBytes;
        });
        _transactionPool = Substitute.For<ITxPool>();
        _transactionPool.MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>())
            .Returns(BlobCellMergeResult.TransactionUnavailable);
        _transactionPool.MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>())
            .Returns(call => _transactionPool.MergeBlobCells(call.Arg<Hash256>(), call.ArgAt<BlobCellMask>(1), call.Arg<byte[][]>()));
        _gossipPolicy = Substitute.For<IGossipPolicy>();
        _txPoolConfig = Substitute.For<ITxPoolConfig>();
        _txPoolConfig.BlobsSupport.Returns(BlobsSupportMode.InMemory);
        _txPoolConfig.InMemoryBlobPoolSize.Returns(new TxPoolConfig().InMemoryBlobPoolSize);
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
        // Sampling needs at least two peers; tests that need fewer remove this one.
        _idlePeer = new TestSparseBlobPeer(TestItem.PublicKeyF);
        _sparseBlobPoolPeerRegistry.AddPeer(_idlePeer);
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
    public void should_not_allocate_large_blob_gossip_caches_for_an_idle_peer()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        using Eth72ProtocolHandler handler = CreateProbeHandler(_session, _sparseBlobPoolPeerRegistry);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.LessThan(2 * 1024 * 1024));
    }

    [Test]
    public void registry_should_reject_over_quota_announcements_before_allocating_or_evicting()
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            NullTxPool.Instance, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 1);

        const int capacity = 2048;
        for (int i = 0; i < capacity; i++)
        {
            Assert.That(registry.RecordAnnouncement(peers[0], HashFromInt(i), BlobCellMask.Full), Is.True);
        }

        Hash256[] rejectedHashes = Enumerable.Range(capacity, 1024).Select(HashFromInt).ToArray();
        registry.RecordAnnouncement(peers[0], rejectedHashes[0], BlobCellMask.Full);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int accepted = 0;
        foreach (Hash256 hash in rejectedHashes)
        {
            if (registry.RecordAnnouncement(peers[0], hash, BlobCellMask.Full)) accepted++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.Zero);
            Assert.That(allocated, Is.LessThan(65536), "Rejecting new hashes at capacity must not allocate tracking state.");
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(0)), Is.EqualTo(1));
        }

        Assert.That(registry.RecordAnnouncement(peers[0], HashFromInt(0), BlobCellMask.FromIndices([4])), Is.True);
        clock.Add(TimeSpan.FromMinutes(6));
        timers.Timer.Fire();
        Assert.That(registry.RecordAnnouncement(peers[0], rejectedHashes[0], BlobCellMask.Full), Is.True);
    }

    [Test]
    public void registry_should_bound_concurrent_announcement_tracking()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            NullTxPool.Instance, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 16);
        int accepted = 0;
        Parallel.For(0, 65536, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            if (registry.RecordAnnouncement(peers[i % peers.Length], HashFromInt(i), BlobCellMask.Full))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        int retained = Enumerable.Range(0, 65536).Count(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) != 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.GreaterThan(16384));
            Assert.That(retained, Is.InRange(1, 16384));
        }
    }

    private static TestSparseBlobPeer[] CreateSparseBlobPeers(SparseBlobPoolPeerRegistry registry, int count)
    {
        TestSparseBlobPeer[] peers = new TestSparseBlobPeer[count];
        for (int i = 0; i < peers.Length; i++)
        {
            byte[] key = new byte[PublicKey.LengthInBytes];
            BinaryPrimitives.WriteInt32LittleEndian(key, i + 1);
            peers[i] = new TestSparseBlobPeer(new PublicKey(key));
            registry.AddPeer(peers[i]);
        }

        return peers;
    }

    [Test]
    public void Metadata_correct()
    {
        using (Assert.EnterMultipleScope())
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
            m.Hashes.Count == 1 &&
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
            m.Hashes.Count == 1 &&
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
            m.Hashes.Count == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(BlobCellMask.Empty.ToBytes())));
    }

    [Test]
    public void should_remember_announced_blob_masks_up_to_blob_pool_capacity([Values] bool exceedCapacity)
    {
        const int blobPoolSize = 2;
        _txPoolConfig.InMemoryBlobPoolSize.Returns(blobPoolSize);
        Transaction[] txs = Enumerable.Range(0, exceedCapacity ? blobPoolSize + 1 : blobPoolSize)
            .Select(static nonce => Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithNonce((ulong)nonce)
                .SignedAndResolved()
                .TestObject)
            .ToArray();

        foreach (Transaction tx in txs)
        {
            _handler.SendNewTransaction(tx);
        }

        _session.ClearReceivedCalls();
        _handler.SendNewTransaction(txs[0]);

        _session.Received(exceedCapacity ? 1 : 0).DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage72>());
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
            message.Hashes.SequenceEqual(new ValueHash256[] { first.Hash!, second.Hash! })
            && message.CellMask.SequenceEqual(BlobCellMask.Full.ToBytes())));
    }

    [Test]
    public void should_announce_persisted_light_v1_blob_tx_with_elided_network_encoding_size()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        LightTransaction lightTx = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));
        int elidedWireSize = BuildElidedBlobTransaction(tx).GetLength();

        _handler.SendNewTransactions([lightTx], sendFullTx: false);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Count == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.Sizes[0] == elidedWireSize &&
            m.Sizes[0] < tx.GetLength()));
    }

    [Test]
    public void announced_size_matches_the_elided_typed_transaction_encoding()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        Transaction elidedTx = BuildElidedBlobTransaction(tx);

        int announced = 0;
        _session.When(session => session.DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage72>()))
            .Do(call => announced = ((NewPooledTransactionHashesMessage72)call[0]).Sizes[0]);

        _handler.SendNewTransaction(tx);

        // Peers compare the announced size with the decoded typed transaction envelope, excluding the enclosing
        // PooledTransactions list element's RLP string prefix.
        byte[] servedTxBytes = TxDecoder.Instance
            .Encode(elidedTx, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(announced, Is.EqualTo(servedTxBytes.Length));
            Assert.That(announced, Is.GreaterThan(tx.GetLength(shouldCountBlobs: false) + 8));
        }
    }

    [Test]
    public void should_not_announce_legacy_light_v1_blob_tx_with_unknown_network_size()
    {
        // The elided network-encoding size is not present in legacy entries.
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
            sparseBlobNetworkSize: 0,
            type: TxType.Blob);

        _handler.SendNewTransactions([legacyLightTx], sendFullTx: false);

        _session.DidNotReceive().DeliverMessage(Arg.Any<NewPooledTransactionHashesMessage72>());
    }

    [Test]
    public void should_not_announce_blob_tx_without_network_wrapper()
    {
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        tx.NetworkWrapper = null;
        tx.ClearLengthCache();

        _handler.SendNewTransaction(tx);

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

        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.PacketType == Eth72MessageCode.GetCells &&
            m.Hashes[0] == hash &&
            m.CellMask.SequenceEqual(announcementMask.ToBytes())));
    }

    [Test]
    public void should_log_blob_cell_request_at_trace()
    {
        TestLogger logger = new() { IsDebug = false };
        RecreateHandler(logManager: new OneLoggerLogManager(new ILogger(logger)));
        HandleIncomingStatusMessage();

#pragma warning disable NUnit2045 // Keep this state-changing operation separate from its prerequisites, dependent observations, or subsequent mutations.
        Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(HashFromInt(1), BlobCellMask.FromIndices([1])), Is.True);
#pragma warning restore NUnit2045
        Assert.That(logger.LogList, Has.Some.Contains("requesting blob cells"));
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

        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));

        BlobCellMask expectedRemainingMask = expandedRequestMask.Except(initialRequestMask);
        Assert.That(expectedRemainingMask.IsEmpty, Is.False);
        TestSparseBlobPeer retryProvider = new(TestItem.PublicKeyD);
        _sparseBlobPoolPeerRegistry.AddPeer(retryProvider);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(retryProvider, hash, BlobCellMask.Full);

        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));

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

        Assert.That(() => HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes), Throws.TypeOf<RlpReaderException>());
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
    public void should_accept_non_blob_announcement_and_ignore_cell_mask([Values] bool hasCellMask)
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

    // The frame-transaction gate reads the head spec, and an announcement carries up to MaxCount entries.
    [Test]
    public void should_resolve_the_frame_tx_gate_once_per_announcement()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(true);
        _specProvider.GetCurrentHeadSpec().Returns(spec);
        _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        Hash256[] hashes = [HashFromInt(1), HashFromInt(2), HashFromInt(3)];
        using NewPooledTransactionHashesMessage72 message = new(
            [.. Enumerable.Repeat((byte)TxType.FrameTx, hashes.Length)],
            [.. Enumerable.Repeat(1024, hashes.Length)],
            [.. hashes],
            BlobCellMask.Empty.ToBytes());

        HandleIncomingStatusMessage();
        _specProvider.ClearReceivedCalls();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        _specProvider.Received(1).GetCurrentHeadSpec();
        _session.Received(1).DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m =>
            m.EthMessage.Hashes.Count == hashes.Length));
    }

    // Delivery validates every transaction in the packet, so the same gate has to be resolved once there too.
    [Test]
    public void should_resolve_the_frame_tx_gate_once_per_delivered_packet()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(true);
        _specProvider.GetCurrentHeadSpec().Returns(spec);

        Transaction[] transactions = [FrameTx(TestItem.PrivateKeyA), FrameTx(TestItem.PrivateKeyB), FrameTx(TestItem.PrivateKeyC)];
        using TransactionsMessage message = new(transactions.ToPooledList());

        HandleIncomingStatusMessage();
        _specProvider.ClearReceivedCalls();
        HandleZeroMessage(message, Eth62MessageCode.Transactions);

        _specProvider.Received(1).GetCurrentHeadSpec();
        _transactionPool.Received(transactions.Length).SubmitTx(Arg.Is<Transaction>(tx => tx.Type == TxType.FrameTx), Arg.Any<TxHandlingOptions>());
    }

    // The pool charges a peer's share of the per-head validation time to whoever submits, so the handler must
    // identify itself; a share the pool reports spent counts towards this peer's flood limits.
    [Test]
    public void Frame_txs_are_submitted_as_this_peer_for_its_validation_share()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(true);
        _specProvider.GetCurrentHeadSpec().Returns(spec);
        ITxPool pool = Substitute.For<ITxPool, IRecyclableTxPool>();
        ((IRecyclableTxPool)pool).SubmitOwnedTx(Arg.Any<Transaction>(), Arg.Any<object?>(), out Arg.Any<bool>())
            .Returns(AcceptTxResult.FramePeerValidationBudgetSpent);
        _transactionPool = pool;
        RecreateHandler();

        Transaction[] transactions = [FrameTx(TestItem.PrivateKeyA), FrameTx(TestItem.PrivateKeyB)];
        using TransactionsMessage message = new(transactions.ToPooledList());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth62MessageCode.Transactions);

        ((IRecyclableTxPool)pool).Received(transactions.Length).SubmitOwnedTx(Arg.Any<Transaction>(), _handler, out Arg.Any<bool>());
    }

    // This handler has its own submission loop, so it needs the same stop once an invalid transaction closes the session.
    [Test]
    public void should_stop_submitting_the_rest_of_a_packet_after_an_invalid_transaction_requests_a_disconnect()
    {
        _session.When(static s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
            .Do(_ => _session.IsClosing.Returns(true));
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>())
            .Returns(AcceptTxResult.Invalid, AcceptTxResult.Accepted, AcceptTxResult.Accepted);
        using TransactionsMessage message = new(Build.A.Transaction.SignedAndResolved().TestObjectNTimes(3).ToPooledList());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth62MessageCode.Transactions);

        using (Assert.EnterMultipleScope())
        {
            _session.Received(1).InitiateDisconnect(DisconnectReason.InvalidTxReceived, "invalid tx");
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }
    }

    [Test]
    public void should_reject_announcement_above_peer_admission_limit()
    {
        const int count = NewPooledTransactionHashesMessage72.MaxCount + 1;
        byte[] types = new byte[count];
        int[] sizes = new int[count];
        ValueHash256[] hashes = new ValueHash256[count];
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WasPooledTransactionRequested(rejectedHash), Is.False);
            Assert.That(WasCellTransactionRequested(rejectedHash), Is.False);
        }
    }

    [Test]
    public void registry_should_release_announcement_quota_after_sparse_submission()
    {
        const int maxAnnouncementsPerPeer = 2048;
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        registry.AddPeer(peer);
        _transactionPool.ValidateTxForBlobSampling(tx).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

        int accepted = 0;
        for (int i = 0; i < maxAnnouncementsPerPeer - 1; i++)
        {
            if (registry.RecordAnnouncement(peer, HashFromInt(i), cellMask))
            {
                accepted++;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(maxAnnouncementsPerPeer - 1));
            Assert.That(registry.RecordAnnouncement(peer, tx.Hash!, cellMask), Is.True);
            Assert.That(registry.RecordAnnouncement(peer, HashFromInt(maxAnnouncementsPerPeer), cellMask), Is.False);

            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);
        }

        Assert.That(registry.RecordAnnouncement(peer, HashFromInt(maxAnnouncementsPerPeer), cellMask), Is.True);
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
        Transaction tx = BuildSparseBlobTransaction(out _, out _, out _, 0);
        for (ulong nonce = 1; _sparseBlobPoolPeerRegistry.GetRequestMask(tx.Hash!, BlobCellMask.Full, 15).IsFull; nonce++)
        {
            Assert.That(nonce, Is.LessThan(MaxSamplerTransactionAttempts), "Could not build a sampler blob transaction.");
            tx = BuildSparseBlobTransaction(out _, out _, out _, nonce);
        }

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
            : (handlerRequest!.Hashes[0].ToHash256(), BlobCellMask.FromBytes(handlerRequest.CellMask));
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
    public void normal_mode_should_request_full_cells_when_too_few_peers_to_sample([Values] bool fullAnnouncement)
    {
        RecreateHandler();
        _blobCustodyTracker.Update(BlobCellMask.FromIndices([2, 7]));
        Transaction tx = BuildSamplerBlobTransaction();
        _sparseBlobPoolPeerRegistry.RemovePeer(_idlePeer);
        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            (fullAnnouncement ? BlobCellMask.Full : BlobCellMask.FromIndices([2, 7, 9])).ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        if (fullAnnouncement)
        {
            _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
                m.Hashes.Length == 1 &&
                m.Hashes[0] == tx.Hash &&
                m.CellMask.SequenceEqual(BlobCellMask.Full.ToBytes())));
        }
        else
        {
            _session.DidNotReceive().DeliverMessage(Arg.Any<GetCellsMessage72>());
        }
    }

    [Test]
    public void single_peer_should_retry_full_cell_requests_over_in_flight_budget()
    {
        _blobCustodyTracker.Update(BlobCellMask.FromIndices([2, 7]));
        _sparseBlobPoolPeerRegistry.RemovePeer(_idlePeer);
        // A lone peer is a single provider: its in-flight budget is half the global cell work split into at least eight
        // shares, which is four full-mask requests. One more announcement must wait for a slot.
        const int count = 5;
        byte[] types = new byte[count];
        int[] sizes = new int[count];
        ValueHash256[] hashes = new ValueHash256[count];
        for (int i = 0; i < count; i++)
        {
            types[i] = (byte)TxType.Blob;
            sizes[i] = 1024;
            hashes[i] = HashFromInt(i);
        }

        using NewPooledTransactionHashesMessage72 message = new(types, sizes, hashes, BlobCellMask.Full.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        GetCellsMessage72[] requests = [.. _deliveredMessages.OfType<GetCellsMessage72>()];
        Assert.That(requests, Has.Length.EqualTo(count - 1));

        _sparseBlobPoolPeerRegistry.OnCellsRequestCompleted(requests[0].Hashes[0].ToHash256(), BlobCellMask.Full, (ISparseBlobPoolPeer)_handler);
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == hashes[count - 1] &&
            m.CellMask.SequenceEqual(BlobCellMask.Full.ToBytes())));
    }

    [Test]
    public void sampler_should_fetch_full_cells_when_second_provider_wait_ends([Values] bool peerLeaves)
    {
        ManualTimestamper clock = UseManualClock();
        Transaction tx = AnnounceParkedSamplerTransaction();
        if (peerLeaves)
        {
            _sparseBlobPoolPeerRegistry.RemovePeer(_idlePeer);
        }

        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));
        ISparseBlobPoolPeer handler = _handler;
        handler.MaintainSparseBlobState(clock.UtcNowOffset + Eth72ProtocolHandler.SamplerProviderWait / 2);
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetCellsMessage72>());

        handler.MaintainSparseBlobState(clock.UtcNowOffset + Eth72ProtocolHandler.SamplerProviderWait * 2);

        _session.Received(1).DeliverMessage(Arg.Is<GetCellsMessage72>(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(BlobCellMask.Full.ToBytes())));
    }

    [Test]
    public void sampler_should_sample_when_second_provider_announces_before_wait_ends()
    {
        Transaction tx = AnnounceParkedSamplerTransaction();
        Hash256 hash = tx.Hash!;
        TestSparseBlobPeer secondProvider = new(TestItem.PublicKeyC);
        _sparseBlobPoolPeerRegistry.AddPeer(secondProvider);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(secondProvider, hash, BlobCellMask.Full);
        BlobCellMask sampledMask = _sparseBlobPoolPeerRegistry.GetRequestMask(hash, BlobCellMask.Full, 15);

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + Eth72ProtocolHandler.SamplerProviderWait * 2);

        BlobCellMask requestedMask = BlobCellMask.Empty;
        foreach (GetCellsMessage72 request in _deliveredMessages.OfType<GetCellsMessage72>())
        {
            requestedMask |= BlobCellMask.FromBytes(request.CellMask);
        }

        foreach ((Hash256 _, BlobCellMask cellMask) in secondProvider.CellRequests)
        {
            requestedMask |= cellMask;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sampledMask.IsFull, Is.False);
            Assert.That(requestedMask, Is.EqualTo(sampledMask));
        }
    }

    [Test]
    public void sampler_should_not_fetch_full_cells_when_two_providers_wait_for_transaction_body()
    {
        Transaction tx = AnnounceParkedSamplerTransaction(recordBody: false);
        TestSparseBlobPeer secondProvider = new(TestItem.PublicKeyC);
        _sparseBlobPoolPeerRegistry.AddPeer(secondProvider);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(secondProvider, tx.Hash!, BlobCellMask.Full);

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + Eth72ProtocolHandler.SamplerProviderWait * 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>(), Is.Empty);
            Assert.That(secondProvider.CellRequests, Is.Empty);
        }
    }

    /// <summary>Announces a sampler-role transaction in full from the handler peer so its cells wait for a second provider.</summary>
    private Transaction AnnounceParkedSamplerTransaction(bool recordBody = true)
    {
        _blobCustodyTracker.Update(BlobCellMask.FromIndices([2, 7]));
        Transaction tx = BuildSparseBlobTransaction(out _, out _, out _, 0);
        for (ulong nonce = 1; _sparseBlobPoolPeerRegistry.GetRequestMask(tx.Hash!, BlobCellMask.Full, 15).IsFull; nonce++)
        {
            Assert.That(nonce, Is.LessThan(MaxSamplerTransactionAttempts), "Could not build a sampler blob transaction.");
            tx = BuildSparseBlobTransaction(out _, out _, out _, nonce);
        }

        _transactionPool.NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);

        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            BlobCellMask.Full.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);
        if (recordBody)
        {
            Assert.That(_sparseBlobPoolPeerRegistry.RecordTransaction((ISparseBlobPoolPeer)_handler, tx), Is.Null);
        }

        _session.DidNotReceive().DeliverMessage(Arg.Any<GetCellsMessage72>());
        return tx;
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
    public void should_send_elided_v1_blob_payload_in_pooled_transactions_response()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        int fullTxLength = tx.GetLength();

        _transactionPool.TryGetPendingTransactionWithoutBlobs(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = BuildElidedBlobTransaction(tx);
                return true;
            });

        using GetPooledTransactionsMessage request = new(new[] { tx.Hash! }.Select(static h => h.ValueHash256).ToArray().ToPooledList());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth66MessageCode.GetPooledTransactions);

        _transactionPool.Received(1).TryGetPendingTransactionWithoutBlobs(tx.Hash!, out Arg.Any<Transaction>());
        _transactionPool.DidNotReceive().TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>());
        _transactionPool.DidNotReceive().TryGetPendingBlobCellMaskAndNetworkForm(Arg.Any<ValueHash256>(), out Arg.Any<BlobCellMask>(), out Arg.Any<IOwnedReadOnlyList<byte>>());
        _session.Received(1).DeliverMessage(Arg.Is<PooledTransactionsMessage>(m =>
            IsElidedBlobResponse(m, fullTxLength, ProofVersion.V1)));
    }

    [Test]
    public void should_send_elided_v0_blob_payload_in_pooled_transactions_response()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Cancun.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        int fullTxLength = tx.GetLength();

        _transactionPool.TryGetPendingTransactionWithoutBlobs(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = BuildElidedBlobTransaction(tx);
                return true;
            });

        using GetPooledTransactionsMessage request = new(new[] { tx.Hash! }.Select(static h => h.ValueHash256).ToArray().ToPooledList());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth66MessageCode.GetPooledTransactions);

        _transactionPool.Received(1).TryGetPendingTransactionWithoutBlobs(tx.Hash!, out Arg.Any<Transaction>());
        _transactionPool.DidNotReceive().TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>());
        _session.Received(1).DeliverMessage(Arg.Is<PooledTransactionsMessage>(m =>
            IsElidedBlobResponse(m, fullTxLength, ProofVersion.V0)));
    }

    [Test]
    public void should_announce_v0_blob_tx_with_elided_network_encoding_size()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Cancun.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        int elidedWireSize = BuildElidedBlobTransaction(tx).GetLength();

        _handler.SendNewTransaction(tx);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Count == 1
            && m.Hashes[0] == tx.Hash
            && m.Sizes.Count == 1
            && m.Sizes[0] == elidedWireSize
            && m.Sizes[0] < tx.GetLength()));
    }

    [Test]
    public void should_budget_pooled_response_using_elided_blob_size()
    {
        Transaction template = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        int transactionCount = TransactionsMessage.MaxPacketSize / template.GetLength() + 2;
        Dictionary<ValueHash256, Transaction> transactions = [with(transactionCount)];
        Hash256[] hashes = new Hash256[transactionCount];
        for (int i = 0; i < transactionCount; i++)
        {
            Transaction tx = new();
            template.CopyTo(tx);
            tx.Nonce = (ulong)i;
            tx.Hash = tx.CalculateHash();
            transactions.Add(tx.Hash.ValueHash256, BuildElidedBlobTransaction(tx));
            hashes[i] = tx.Hash;
        }

        _transactionPool.TryGetPendingTransactionWithoutBlobs(Arg.Any<ValueHash256>(), out Arg.Any<Transaction>())
            .Returns(call =>
            {
                bool found = transactions.TryGetValue(call.ArgAt<ValueHash256>(0), out Transaction? tx);
                call[1] = tx!;
                return found;
            });
        using GetPooledTransactionsMessage request = new(hashes.Select(static h => h.ValueHash256).ToArray().ToPooledList());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth66MessageCode.GetPooledTransactions);

        _session.Received(1).DeliverMessage(Arg.Is<PooledTransactionsMessage>(message =>
            message.EthMessage.Transactions.Count == transactionCount));
    }

    [Test]
    public void should_reject_reordered_or_duplicate_pooled_response(
        [Values(true, false)] bool duplicate)
    {
        Transaction first = Build.A.Transaction.WithNonce(1UL).SignedAndResolved().TestObject;
        Transaction second = Build.A.Transaction.WithNonce(2UL).SignedAndResolved().TestObject;
        _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)first.Type, (byte)second.Type],
            [first.GetLength(), second.GetLength()],
            [first.Hash!, second.Hash!],
            BlobCellMask.Empty.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        long requestId = GetLastGetPooledTransactionsRequestId(first.Hash!);
        Transaction[] returned = duplicate ? [first, first] : [second, first];
        using PooledTransactionsMessage66 response = new(
            requestId,
            new PooledTransactionsMessage65(returned.ToPooledList()));

        Assert.That(
            () => HandleZeroMessage(response, Eth66MessageCode.PooledTransactions),
            Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void should_not_request_unsupported_transaction_type()
    {
        Hash256 hash = HashFromInt(1);
        using NewPooledTransactionHashesMessage72 announcement = new(
            [byte.MaxValue],
            [100],
            [hash],
            BlobCellMask.Empty.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        _transactionPool.DidNotReceive().NotifyAboutTx(hash, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
        Assert.That(WasPooledTransactionRequested(hash), Is.False);
    }

    [Test]
    public void should_ignore_malformed_unsolicited_pooled_response_without_decoding_payload()
    {
        using PooledTransactionsMessage66 response = new(
            1111,
            new PooledTransactionsMessage65(Array.Empty<Transaction>().ToPooledList()));
        using DisposableByteBuffer packet = _svc.ZeroSerialize(response).AsDisposable();
        packet.EnsureWritable(1);
        packet.WriteByte(0);
        packet.ReadByte();

        HandleIncomingStatusMessage();

        Assert.That(
            () => _handler.HandleMessage(new NettyBufferPacket(packet) { PacketType = Eth66MessageCode.PooledTransactions }),
            Throws.Nothing);
    }

    // One method per case: these read an object back from the process-wide TxDecoder.TxObjectPool, and a
    // [NonParallelizable] on a parameterized method does not keep other fixtures' decoders away (nunit/nunit#3371).
    [Test, NonParallelizable]
    public void should_handle_announced_v0_blob_pooled_response_with_full_blobs_and_reject_late_cells() =>
        AssertAnnouncedV0BlobPooledResponseIsHandled(fullBlobs: true);

    [Test, NonParallelizable]
    public void should_retain_sparse_state_after_an_elided_v0_blob_response() =>
        AssertAnnouncedV0BlobPooledResponseIsHandled(fullBlobs: false);

    private void AssertAnnouncedV0BlobPooledResponseIsHandled(bool fullBlobs)
    {
        RecreateHandler(providerProbabilityPercent: 100);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Cancun.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        Transaction responseTx = fullBlobs ? tx : BuildElidedBlobTransaction(tx);
        AnnounceBlobTransaction(tx.Hash!, responseTx.GetLength(), TxType.Blob);
        long pooledRequestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);
        long requestId = GetLastGetCellsRequestId(tx.Hash!, BlobCellMask.Full);
        TestSparseBlobPeer alternate = new(TestItem.PublicKeyC);
        object? bufferedCells = null;
        if (!fullBlobs)
        {
            _sparseBlobPoolPeerRegistry.AddPeer(alternate);
            _sparseBlobPoolPeerRegistry.RecordAnnouncement(alternate, tx.Hash!, BlobCellMask.Full);
            BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
            Assert.That(_sparseBlobPoolPeerRegistry.RecordCells(alternate, tx.Hash!, sampledMask, sampledCells), Is.True);
            bufferedCells = GetBufferedCells();
        }

        using PooledTransactionsMessage66 response = new(pooledRequestId, new PooledTransactionsMessage65(new[] { responseTx }.ToPooledList()));
        Transaction decoded = TxDecoder.TxObjectPool.Get();
        TxDecoder.TxObjectPool.Return(decoded);
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Hash, Is.EqualTo(fullBlobs ? tx.Hash : null));
            Assert.That(decoded.Signature is not null, Is.EqualTo(fullBlobs));
            Assert.That(decoded.NetworkWrapper is not null, Is.EqualTo(fullBlobs));
        }
        _transactionPool.Received(fullBlobs ? 1 : 0).SubmitTx(
            Arg.Is<Transaction>(submitted => IsV0BlobTransaction(submitted, tx.Hash!)),
            TxHandlingOptions.None);

        if (!fullBlobs)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(alternate.TransactionRequests, Is.EqualTo(new[] { tx.Hash! }));
                Assert.That(_sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(tx.Hash!), Is.EqualTo(2));
                Assert.That(bufferedCells, Is.Not.Null);
                Assert.That(GetBufferedCells(), Is.EqualTo(bufferedCells));
            }
            return;
        }

        using CellsMessage72 cells = new(
            requestId,
            [tx.Hash!],
            [[[]]],
            BlobCellMask.FromIndices([0]).ToBytes());
        Assert.That(() => HandleZeroMessage(cells, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());

        _transactionPool.DidNotReceive().MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());

        object? GetBufferedCells()
        {
            object tracked = GetTrackedState(_sparseBlobPoolPeerRegistry, tx.Hash!);
            return tracked.GetType().GetProperty("Cells")!.GetValue(tracked);
        }
    }

    [Test, NonParallelizable]
    public void should_clear_v0_blob_state_only_for_a_complete_known_body([Values] bool hasCompleteLocalBody)
    {
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None)
            .Returns(call =>
            {
                TxDecoder.TxObjectPool.Return(call.Arg<Transaction>());
                return AcceptTxResult.AlreadyKnown;
            });
        RecreateHandler(providerProbabilityPercent: 100);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Cancun.Instance)
            .SignedAndResolved()
            .TestObject;
        Hash256 hash = tx.Hash!;
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        AnnounceBlobTransaction(hash, tx.GetLength(), TxType.Blob);
        long pooledRequestId = GetLastGetPooledTransactionsRequestId(hash);
        long cellsRequestId = GetLastGetCellsRequestId(hash, BlobCellMask.Full);
        _sparseBlobPoolPeerRegistry.AddPeer(peer);
        _sparseBlobPoolPeerRegistry.RecordAnnouncement(peer, hash, BlobCellMask.Full);
        ValueHash256 valueHash = hash.ValueHash256;
        _transactionPool.TryGetPendingBlobCellMask(valueHash, out Arg.Any<BlobCellMask>()).Returns(call =>
        {
            call[1] = hasCompleteLocalBody ? BlobCellMask.Full : BlobCellMask.Empty;
            return hasCompleteLocalBody;
        });

        using PooledTransactionsMessage66 response = new(pooledRequestId, new PooledTransactionsMessage65(new[] { tx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        int providers = _sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(hash);
        Assert.That(providers == 0, Is.EqualTo(hasCompleteLocalBody));
        if (hasCompleteLocalBody)
        {
            using CellsMessage72 cells = new(cellsRequestId, [hash], [[[]]], BlobCellMask.FromIndices([0]).ToBytes());
            Assert.That(() => HandleZeroMessage(cells, Eth72MessageCode.Cells), Throws.TypeOf<SubprotocolException>());
        }
        _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void should_announce_sparse_blob_tx_with_elided_network_encoding_size()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        int fullTxLength = tx.GetLength();
        int elidedWireSize = BuildElidedBlobTransaction(tx).GetLength();

        _handler.SendNewTransaction(tx);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Count == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.Sizes.Count == 1 &&
            m.Sizes[0] == elidedWireSize &&
            m.Sizes[0] < fullTxLength));
    }

    [Test]
    public void should_ignore_unsolicited_pooled_response_before_sampling_validation()
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
            Throws.Nothing);

        _transactionPool.DidNotReceive().ValidateTxForBlobSampling(Arg.Any<Transaction>());
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }

    [Test]
    public void Rejected_or_malformed_response_releases_budget([Values] bool malformed)
    {
        RecordingBackgroundTaskScheduler scheduler = new() { Reject = true };
        RecreateHandler(backgroundTaskScheduler: scheduler);
        HandleIncomingStatusMessage();
        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
        _handler.HandleMessage(PooledTransactionRequestMessage.New(tx.Hash!));
        using PooledTransactionsMessage66 response = new(GetLastGetPooledTransactionsRequestId(tx.Hash!),
            new PooledTransactionsMessage65(IOwnedReadOnlyList<Transaction>.Empty));
        using DisposableByteBuffer packet = SerializePooledResponse(response, malformed);
        Action receive = () => _handler.HandleMessage(new NettyBufferPacket(packet) { PacketType = Eth66MessageCode.PooledTransactions });
        if (malformed)
            Assert.That(receive, Throws.Exception);
        else
            Assert.That(receive, Throws.Nothing);

        using CompositeDisposable reservations = [];
        for (int i = 0; i < InboundTransactionBudget.GlobalLimit / InboundTransactionBudget.PeerLimit; i++)
        {
            InboundTransactionBudget.Reservation? reservation = new InboundTransactionBudget(scheduler).TryReserve(InboundTransactionBudget.PeerLimit);
            Assert.That(reservation, Is.Not.Null);
            reservation!.AddTo(reservations);
        }
    }

    private DisposableByteBuffer SerializePooledResponse(PooledTransactionsMessage66 response, bool malformed)
    {
        DisposableByteBuffer packet = _svc.ZeroSerialize(response).AsDisposable();
        if (malformed)
        {
            packet.EnsureWritable(1);
            packet.WriteByte(0);
        }
        packet.ReadByte();
        return packet;
    }

    [Test]
    public void should_accept_batched_retry_response_once([Values] bool announced)
    {
        Transaction first = Build.A.Transaction.WithNonce(0).SignedAndResolved().TestObject;
        Transaction second = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;
        ValueHash256[] hashes = [first.Hash!.ValueHash256, second.Hash!.ValueHash256];
        HandleIncomingStatusMessage();
        if (announced)
        {
            using NewPooledTransactionHashesMessage72 announcement = new(
                [(byte)first.Type, (byte)second.Type], [first.GetLength(), second.GetLength()], hashes, BlobCellMask.Empty.ToBytes());
            HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        }
        _handler.HandleMessages(hashes);
        long requestId = GetLastGetPooledTransactionsRequestId(first.Hash!);
        Assert.That(GetLastGetPooledTransactionsRequestId(second.Hash!), Is.EqualTo(requestId));
        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { first, second }.ToPooledList()));

        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            _transactionPool.Received(1).SubmitTx(Arg.Is<Transaction>(tx => tx.Hash == first.Hash), Arg.Any<TxHandlingOptions>());
            _transactionPool.Received(1).SubmitTx(Arg.Is<Transaction>(tx => tx.Hash == second.Hash), Arg.Any<TxHandlingOptions>());
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public void should_ignore_duplicate_pooled_response_before_sampling_validation()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction elidedTx = BuildElidedBlobTransaction(tx);

        AnnounceBlobTransaction(tx.Hash!, elidedTx.GetLength(), TxType.Blob);
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);
        Assert.That(_handler.RequestedPooledTransactionHashes, Is.EqualTo(1));
        using PooledTransactionsMessage66 emptyResponse = new(requestId, new PooledTransactionsMessage65(Array.Empty<Transaction>().ToPooledList()));
        HandleZeroMessage(emptyResponse, Eth66MessageCode.PooledTransactions);
        using PooledTransactionsMessage66 duplicateResponse = new(requestId, new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));

        Assert.That(
            () => HandleZeroMessage(duplicateResponse, Eth66MessageCode.PooledTransactions),
            Throws.Nothing);

        _transactionPool.DidNotReceive().ValidateTxForBlobSampling(Arg.Any<Transaction>());
    }

    [Test, NonParallelizable]
    public void mismatched_pooled_response_should_return_unsubmitted_transactions()
    {
        PooledTransactionsOverrideSerializationService serializer = new(_svc);
        RecreateHandler(serializer: serializer);
        Transaction announced = BuildBlobTransaction(fullProvider: false);
        Transaction first = Build.A.Transaction.SignedAndResolved().TestObject;
        Transaction unprocessed = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;
        first.SetPreHashNoLock([1]);
        RecycledTransactionWitness unprocessedWitness = new(unprocessed, 2);

        AnnounceBlobTransaction(announced.Hash!, announced.GetLength(shouldCountBlobs: false), TxType.Blob);
        long requestId = GetLastGetPooledTransactionsRequestId(announced.Hash!);
        serializer.PooledTransactions = new PooledTransactionsMessage66(
            requestId,
            new PooledTransactionsMessage65(new[] { first, unprocessed }.ToPooledList()));
        using PooledTransactionsMessage66 wireResponse = new(
            requestId,
            new PooledTransactionsMessage65(Array.Empty<Transaction>().ToPooledList()));

        Assert.That(
            () => HandleZeroMessage(wireResponse, Eth66MessageCode.PooledTransactions),
            Throws.TypeOf<SubprotocolException>());

        // The request matcher hashes `first`, which releases its pre-hash, so only the unhashed tail witnesses the return.
        Assert.That(unprocessedWitness.WasRecycled, Is.True);
    }

    [Test, NonParallelizable]
    public void throwing_submission_should_preserve_retained_transaction_and_return_remaining_transactions()
    {
        Transaction submitted = Build.A.Transaction.SignedAndResolved().TestObject;
        Transaction unsubmitted = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;
        Hash256 submittedHash = submitted.Hash!;
        RecycledTransactionWitness unsubmittedWitness = new(unsubmitted, 2);
        ArrayPoolList<Transaction> transactions = new(2, [submitted, unsubmitted]);
        Transaction? retained = null;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            retained = call.Arg<Transaction>();
            throw new InvalidOperationException("Subscriber failed after retaining the transaction.");
        });
        TestEth72ProtocolHandler handler = RecreateTestHandler(new CallbackBackgroundTaskScheduler(() => false));

        Assert.That(() => handler.HandleSlowPublic(transactions, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retained, Is.SameAs(submitted));
            Assert.That(submitted.Hash, Is.EqualTo(submittedHash));
            Assert.That(submitted.Signature, Is.Not.Null);
            Assert.That(unsubmittedWitness.WasRecycled, Is.True);
            Assert.That(() => _ = transactions[0], Throws.TypeOf<ObjectDisposedException>());
        }
    }

    [Test, NonParallelizable]
    public void cancelled_pooled_processing_before_first_transaction_should_return_all_transactions()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
        RecycledTransactionWitness witness = new(tx, 1);
        ArrayPoolList<Transaction> transactions = new(1, [tx]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        TestEth72ProtocolHandler handler = RecreateTestHandler(new CallbackBackgroundTaskScheduler(() =>
            throw new AssertionException("A fully cancelled batch must not be rescheduled.")));

        handler.HandleSlowPublic(transactions, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(witness.WasRecycled, Is.True);
            Assert.That(() => _ = transactions[0], Throws.TypeOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void should_disconnect_peer_when_sparse_transaction_fails_sampling_validation()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction elidedTx = BuildElidedBlobTransaction(tx);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Invalid);

        AnnounceBlobTransaction(tx.Hash!, elidedTx.GetLength(), TxType.Blob);
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);
        using PooledTransactionsMessage66 response = new(
            requestId,
            new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));

        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        _session.Received(1).InitiateDisconnect(DisconnectReason.InvalidTxReceived, "invalid tx");
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }

    // One method per case: these read an object back from the process-wide TxDecoder.TxObjectPool, and a
    // [NonParallelizable] on a parameterized method does not keep other fixtures' decoders away (nunit/nunit#3371).
    [Test, NonParallelizable]
    public void should_disconnect_if_pooled_blob_tx_size_differs_from_eth72_announcement() =>
        AssertDisconnectsIfPooledBlobTxShapeDiffersFromAnnouncement(wrongSize: true);

    [Test, NonParallelizable]
    public void should_disconnect_if_pooled_blob_tx_type_differs_from_eth72_announcement() =>
        AssertDisconnectsIfPooledBlobTxShapeDiffersFromAnnouncement(wrongSize: false);

    private void AssertDisconnectsIfPooledBlobTxShapeDiffersFromAnnouncement(bool wrongSize)
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
            announcedSize += 9;
        }
        else
        {
            announcedType = TxType.EIP1559;
        }

        AnnounceBlobTransaction(tx.Hash!, announcedSize, announcedType);
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);

        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));
        Transaction reusable = TxDecoder.TxObjectPool.Get();
        TxDecoder.TxObjectPool.Return(reusable);
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reusable.Signature, Is.Null);
            Assert.That(reusable.NetworkWrapper, Is.Null);
            Assert.That(reusable.BlobVersionedHashes, Is.Null);
        }

        _session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
    }

    [TestCase(BlobAnnouncementSize.ElidedWire)]
    [TestCase(BlobAnnouncementSize.GethEstimate)]
    [TestCase(BlobAnnouncementSize.Consensus)]
    public void should_accept_compatible_pooled_blob_tx_size_announcements(BlobAnnouncementSize announcementSize)
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        Transaction elidedTx = BuildElidedBlobTransaction(tx);

        int announcedSize = announcementSize switch
        {
            BlobAnnouncementSize.ElidedWire => elidedTx.GetLength(),
            BlobAnnouncementSize.GethEstimate => GetGethBlobTransactionSizeEstimate(elidedTx),
            BlobAnnouncementSize.Consensus => elidedTx.GetLength(shouldCountBlobs: false),
            _ => throw new ArgumentOutOfRangeException(nameof(announcementSize), announcementSize, null)
        };
        AnnounceBlobTransaction(tx.Hash!, announcedSize, TxType.Blob);
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);

        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
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
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);

        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { txWithEmptySidecar }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
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
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);

        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { txWithMismatchedCommitment }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
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
#pragma warning disable NUnit2045 // Length validation is a prerequisite for safe proof validation.
        Assert.That(proofsVerifier.ValidateLengths(sparseWrapper), Is.True);
#pragma warning restore NUnit2045
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

        for (int i = 0; i < Eth72ProtocolHandler.CellRequestBurstAllowance - 1; i++)
        {
            using GetCellsMessage72 request = new(1000 + i, [HashFromInt(i)], requestedMask.ToBytes());
            HandleZeroMessage(request, Eth72MessageCode.GetCells);
        }

        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());

        using GetCellsMessage72 abusiveRequest = new(2000, [HashFromInt(Eth72ProtocolHandler.CellRequestBurstAllowance - 1)], requestedMask.ToBytes());
        HandleZeroMessage(abusiveRequest, Eth72MessageCode.GetCells);

        _session.Received(1).InitiateDisconnect(
            DisconnectReason.UselessPeer,
            Arg.Is<string>(details => details.Contains("request-to-announce ratio exceeded", StringComparison.Ordinal)));
    }

    [Test]
    public void should_refill_cell_request_burst_allowance_over_time([Values] bool batched)
    {
        ManualTimestamper clock = StartCellRequestRatioCheck();
        int hashIndex = 0;

        SendCellRequests(ref hashIndex, Eth72ProtocolHandler.CellRequestBurstAllowance - 1, batched);
        // Much longer than one burst takes to drain; the extra idle time must give no credit, also not to the next batch.
        clock.Add(TimeSpan.FromSeconds(10.0 * Eth72ProtocolHandler.CellRequestBurstAllowance / Eth72ProtocolHandler.CellRequestBurstRefillPerSecond));

        SendCellRequests(ref hashIndex, Eth72ProtocolHandler.CellRequestBurstAllowance - 1, batched);
        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());

        SendCellRequests(ref hashIndex, 1, batched);
        AssertCellRequestRatioDisconnect();
    }

    [Test]
    public void should_keep_fractional_cell_request_burst_refill()
    {
        ManualTimestamper clock = StartCellRequestRatioCheck();
        int hashIndex = 0;
        SendCellRequests(ref hashIndex, Eth72ProtocolHandler.CellRequestBurstAllowance - 1, batched: true);

        // Each step refills 1.5 requests: 10 steps drain 15, so 5 requests more than the steps send fit again.
        const int steps = 10;
        for (int i = 0; i < steps; i++)
        {
            clock.Add(TimeSpan.FromSeconds(1.5 / Eth72ProtocolHandler.CellRequestBurstRefillPerSecond));
            SendCellRequests(ref hashIndex, 1, batched: true);
        }

        SendCellRequests(ref hashIndex, 5, batched: true);
        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());

        SendCellRequests(ref hashIndex, 1, batched: true);
        AssertCellRequestRatioDisconnect();
    }

    /// <summary>Ends the request-ratio warmup and drives the handler from a manual clock.</summary>
    private ManualTimestamper StartCellRequestRatioCheck()
    {
        HandleIncomingStatusMessage();
        ManualTimestamper clock = UseManualClock();
        typeof(Eth72ProtocolHandler)
            .GetField("_requestRatioWarmupEndsAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_handler, clock.UtcNowOffset);
        typeof(Eth72ProtocolHandler)
            .GetField("_cellRequestBurstRefilledAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_handler, clock.UtcNowOffset);
        return clock;
    }

    private ManualTimestamper UseManualClock()
    {
        ManualTimestamper clock = new();
        typeof(SyncPeerProtocolHandlerBase)
            .GetField("_timestamper", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_handler, clock);
        return clock;
    }

    private void SendCellRequests(ref int hashIndex, int count, bool batched)
    {
        byte[] requestedMask = BlobCellMask.FromIndices([1]).ToBytes();
        if (batched)
        {
            ValueHash256[] hashes = new ValueHash256[count];
            for (int i = 0; i < count; i++, hashIndex++)
            {
                hashes[i] = HashFromInt(hashIndex);
            }

            using GetCellsMessage72 request = new(1000 + hashIndex, hashes, requestedMask);
            HandleZeroMessage(request, Eth72MessageCode.GetCells);
            return;
        }

        for (int i = 0; i < count; i++, hashIndex++)
        {
            using GetCellsMessage72 request = new(1000 + hashIndex, [HashFromInt(hashIndex)], requestedMask);
            HandleZeroMessage(request, Eth72MessageCode.GetCells);
        }
    }

    private void AssertCellRequestRatioDisconnect() =>
        _session.Received(1).InitiateDisconnect(
            DisconnectReason.UselessPeer,
            Arg.Is<string>(details => details.Contains("request-to-announce ratio exceeded", StringComparison.Ordinal)));

    [Test]
    public void should_account_for_discarded_get_cells_hashes_when_enforcing_request_ratio()
    {
        HandleIncomingStatusMessage();
        typeof(Eth72ProtocolHandler)
            .GetField("_requestRatioWarmupEndsAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_handler, DateTimeOffset.MinValue);
        typeof(Eth72ProtocolHandler)
            .GetField("_blobAnnouncementsReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_handler, 1L);
        ValueHash256[] hashes = new ValueHash256[Eth72ProtocolHandler.CellRequestBurstAllowance + 5];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = HashFromInt(i);
        }

        using GetCellsMessage72 request = new(1000, hashes, BlobCellMask.FromIndices([1]).ToBytes());
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

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
        for (int i = 0; i < Eth72ProtocolHandler.CellRequestBurstAllowance + 5; i++)
        {
            using GetCellsMessage72 request = new(2000 + i, [HashFromInt(i)], requestedMask.ToBytes());
            HandleZeroMessage(request, Eth72MessageCode.GetCells);
        }

        _session.Received(1).InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
    }

    [Test]
    public void locally_complete_blob_announcements_should_count_toward_cell_request_allowance()
    {
        BlobCellMask fullMask = BlobCellMask.Full;
        _transactionPool.TryGetPendingBlobCellMask(Arg.Any<ValueHash256>(), out Arg.Any<BlobCellMask>())
            .Returns(call =>
            {
                call[1] = fullMask;
                return true;
            });
        HandleIncomingStatusMessage();
        for (int i = 0; i < 2; i++)
        {
            using NewPooledTransactionHashesMessage72 announcement = new(
                [(byte)TxType.Blob],
                [1024],
                [HashFromInt(i)],
                fullMask.ToBytes());
            HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        }

        typeof(Eth72ProtocolHandler)
            .GetField("_requestRatioWarmupEndsAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_handler, DateTimeOffset.MinValue);
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1]);
        for (int i = 0; i < Eth72ProtocolHandler.CellRequestBurstAllowance + 5; i++)
        {
            using GetCellsMessage72 request = new(3000 + i, [HashFromInt(i)], requestedMask.ToBytes());
            HandleZeroMessage(request, Eth72MessageCode.GetCells);
        }

        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
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
#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)firstTx.NetworkWrapper!, requestedMask, out byte[][] firstCells), Is.True);
#pragma warning restore NUnit2045
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)secondTx.NetworkWrapper!, requestedMask, out byte[][] secondCells), Is.True);
        SetupGetCellsResponse(firstTx, requestedMask, requestedMask, firstCells);
        SetupGetCellsResponse(secondTx, requestedMask, requestedMask, secondCells);

        using GetCellsMessage72 request = new(1234, [firstTx.Hash!, secondTx.Hash!], requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == request.RequestId &&
            m.Hashes.SequenceEqual(new ValueHash256[] { firstTx.Hash!, secondTx.Hash! }) &&
            m.CellMask.SequenceEqual(requestedMask.ToBytes()) &&
            m.Cells.Length == 2 &&
            m.Cells[0].Zip(firstCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal) &&
            m.Cells[1].Zip(secondCells, static (left, right) => left.SequenceEqual(right)).All(static equal => equal)));
    }

    [Test]
    public void should_cap_cells_response_when_request_exceeds_response_hash_limit()
    {
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1]);
        ValueHash256[] hashes = new ValueHash256[Eth72ProtocolHandler.MaxCellsResponseHashes * 2];
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
    public void should_bound_hash_lookups_for_cells_request()
    {
        BlobCellMask requestedMask = BlobCellMask.FromIndices([1]);
        ValueHash256[] hashes = Enumerable.Range(0, Eth72ProtocolHandler.MaxCellsRequestHashes + 1)
            .Select(i => HashFromInt(i).ValueHash256)
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
#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)firstTx.NetworkWrapper!, firstAvailableMask, out byte[][] firstCells), Is.True);
#pragma warning restore NUnit2045
        Assert.That(BlobCellsHelper.TryGetFlattenedCells((ShardBlobNetworkWrapper)secondTx.NetworkWrapper!, secondAvailableMask, out byte[][] secondCells), Is.True);
        SetupGetCellsResponse(firstTx, requestedMask, firstAvailableMask, firstCells);
        SetupGetCellsResponse(secondTx, requestedMask, secondAvailableMask, secondCells);

        using GetCellsMessage72 request = new(1234, [firstTx.Hash!, secondTx.Hash!], requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == request.RequestId &&
            m.Hashes.SequenceEqual(new ValueHash256[] { secondTx.Hash! }) &&
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
            && message.Hashes.SequenceEqual(new ValueHash256[] { tx.Hash! })
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
            [elidedTx.GetLength()],
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
    public void should_back_off_before_re_requesting_from_sole_announcer_after_empty_cells_response()
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

        GetCellsMessage72[] requests = _deliveredMessages.OfType<GetCellsMessage72>().Where(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(requestedMask.ToBytes())).ToArray();
        Assert.That(requests, Has.Length.EqualTo(1));

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));

        requests = _deliveredMessages.OfType<GetCellsMessage72>().Where(m =>
            m.Hashes.Length == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(requestedMask.ToBytes())).ToArray();
        Assert.That(requests, Has.Length.EqualTo(2));
        Assert.That(requests[1].RequestId, Is.Not.EqualTo(requests[0].RequestId));
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

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpReaderException>());
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

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpReaderException>());
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

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpReaderException>());
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

        Assert.That(() => _handler.HandleMessage(new NettyBufferPacket(packet) { PacketType = Eth72MessageCode.Cells }), Throws.TypeOf<RlpReaderException>());
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

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpReaderException>());
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

        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.TypeOf<RlpReaderException>());
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherPeer.Disconnects, Is.Empty);
            Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
        }
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
        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));
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
        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));

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

        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));

        using CellsMessage72 response = new(requestId, [tx.Hash!], [cells], cellMask.ToBytes());
        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);
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
        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));

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
#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, validMask, out byte[][] validCells), Is.True);
#pragma warning restore NUnit2045
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, invalidMask, out byte[][] invalidCells), Is.True);
        TestSparseBlobPeer validPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer invalidPeer = new(TestItem.PublicKeyD);
        _sparseBlobPoolPeerRegistry.AddPeer(validPeer);
        _sparseBlobPoolPeerRegistry.AddPeer(invalidPeer);
        _transactionPool.MergeBlobCells(tx.Hash!, Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>())
            .Returns(call => call.ArgAt<BlobCellMask>(1) == validMask
                ? BlobCellMergeResult.Accepted
                : BlobCellMergeResult.InvalidCells);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sparseBlobPoolPeerRegistry.RecordCells(validPeer, tx.Hash!, validMask, validCells), Is.True);
            Assert.That(_sparseBlobPoolPeerRegistry.RecordCells(invalidPeer, tx.Hash!, invalidMask, invalidCells), Is.True);
        }

        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validPeer.Disconnects, Is.Empty);
            Assert.That(invalidPeer.Disconnects, Is.Empty);
            Assert.That(_sparseBlobPoolPeerRegistry.RecordAnnouncement(validPeer, tx.Hash!, validMask), Is.True);
            Assert.That(_sparseBlobPoolPeerRegistry.RecordAnnouncement(invalidPeer, tx.Hash!, invalidMask), Is.False);
        }
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, validMask, Arg.Any<byte[][]>());
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, invalidMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void registry_should_quarantine_repeated_ambiguous_cell_source()
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

#pragma warning disable NUnit2045 // Keep this state-changing operation separate from its prerequisites, dependent observations, or subsequent mutations.
        Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);
#pragma warning restore NUnit2045
        Assert.That(registry.TryApplyRecordedCells(tx.Hash!), Is.False);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.False);
            Assert.That(registry.RecordAnnouncement(peer, tx.Hash!, cellMask), Is.False);
        }

        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
        _transactionPool.DidNotReceive().ForgetRejectedBlobTransaction(tx.Hash!);
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

        _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
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
            Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(HashFromInt(i), cellMask), Is.True);
        }

        Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(tx.Hash!, cellMask), Is.True);
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
        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));

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
        _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(tx));

        _transactionPool.DidNotReceive().MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void should_requeue_claimed_cells_when_background_scheduler_rejects()
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        _blobCustodyTracker.Update(SupernodeCustodyMask());
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        RecreateHandler(backgroundTaskScheduler: new RejectingBackgroundTaskScheduler(),
            sparseBlobPoolPeerRegistry: registry, requestClock: clock);
        HandleIncomingStatusMessage();
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        ISparseBlobPoolPeer peer = _handler;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, tx.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRequestCells(tx.Hash!, cellMask, peer.Id), Is.True);
        }
        long firstRequestId = GetLastGetCellsRequestId(tx.Hash!, cellMask);

        using CellsMessage72 response = new(firstRequestId, [tx.Hash!], [cells], cellMask.ToBytes());
        Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);
        clock.Add(TimeSpan.FromSeconds(4));
        timers.Timer.Fire();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(1));
            Assert.That(registry.GetFullProviderAnnouncementCount(tx.Hash!), Is.EqualTo(1));
        }

        clock.Add(TimeSpan.FromSeconds(1));
        timers.Timer.Fire();
        GetCellsMessage72[] requests = _deliveredMessages.OfType<GetCellsMessage72>().ToArray();
        Assert.That(requests, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests[1].RequestId, Is.Not.EqualTo(firstRequestId));
            Assert.That(requests[1].Hashes, Is.EqualTo(new[] { tx.Hash!.ValueHash256 }));
            Assert.That(BlobCellMask.FromBytes(requests[1].CellMask), Is.EqualTo(cellMask));
            Assert.That(registry.GetFullProviderAnnouncementCount(tx.Hash!), Is.EqualTo(1));
        }
        _transactionPool.DidNotReceive().MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
    }

    // Restoring is one-shot once accepted: a pending request that cannot be placed keeps retrying,
    // but the announcement must not be re-recorded on every maintenance tick.
    [Test]
    public void should_restore_announcement_only_once_when_pending_request_cannot_be_placed()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        ConfigureAnnouncementRestoration(registry);
        registry.TryRestoreAnnouncement(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<Hash256>(), Arg.Any<AnnouncementRestoration>()).Returns(true);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);

        HandleIncomingStatusMessage();
        ParkEmptyCellsResponse(tx.Hash!, cellMask);

        registry.ClearReceivedCalls();
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(7));
        registry.Received(1).TryRequestCells(tx.Hash!, cellMask, Arg.Any<PublicKey>());
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8));

        using (Assert.EnterMultipleScope())
        {
            registry.Received(1).TryRestoreAnnouncement(_handler, tx.Hash!, Arg.Is<AnnouncementRestoration>(ticket => ticket.Mask.IsFull));
            registry.Received(2).TryRequestCells(tx.Hash!, cellMask, Arg.Any<PublicKey>());
        }
    }

    // Local scheduler saturation is our fault: the retry is still throttled, but the peer keeps the
    // announcement that marks it as a provider node-wide.
    [Test]
    public void should_throttle_without_dropping_announcement_when_cells_response_cannot_be_scheduled()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        registry.TryRequestCells(Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>(), Arg.Any<PublicKey>()).Returns(true);
        SourceRejectingBackgroundTaskScheduler rejectingScheduler = new(ClaimedCellsResponseTypeName);
        RecreateHandler(
            backgroundTaskScheduler: rejectingScheduler,
            sparseBlobPoolPeerRegistry: registry);
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
        Assert.That(rejectingScheduler.Rejected, Is.GreaterThan(0), "the cells response must actually have been rejected");

        AssertProviderAnnouncementPreserved(registry, tx.Hash!);
        registry.ClearReceivedCalls();
        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            cellMask.ToBytes());
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        using (Assert.EnterMultipleScope())
        {
            // The announcement is what marks the peer as a provider, so it must survive.
            AssertProviderAnnouncementPreserved(registry, tx.Hash!);
            // ...but a re-announcement must not immediately re-issue the request we just dropped.
            registry.DidNotReceive().TryRequestCells(tx.Hash!, Arg.Any<BlobCellMask>(), Arg.Any<PublicKey>());
        }

        // Past the 5s backoff but inside the 15s pending-request TTL.
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));

        using (Assert.EnterMultipleScope())
        {
            registry.Received(1).TryRequestCells(tx.Hash!, cellMask, Arg.Any<PublicKey>());
            registry.DidNotReceive().RecordAnnouncement(_handler, tx.Hash!, Arg.Any<BlobCellMask>());
        }
    }

    // The restore is recorded outside _cellStateLock, so a park landing in that gap re-strips the
    // announcement and owes a new restore. Clearing by mask could not see that, because the mask the
    // park re-adds is the one just restored; ticket identity distinguishes them.
    [Test]
    public void should_not_clear_a_restore_re_added_while_the_previous_one_was_being_recorded()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        ConfigureAnnouncementRestoration(registry);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        bool reParked = false;
        registry.TryRestoreAnnouncement(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<Hash256>(), Arg.Any<AnnouncementRestoration>())
            .Returns(_ =>
            {
                if (!reParked)
                {
                    // Stand in for a concurrent CellsMessage72: strips the announcement again and
                    // re-parks the very same mask before the maintenance tick retakes the lock.
                    reParked = true;
                    ParkEmptyCellsResponse(tx.Hash!, cellMask);
                }

                return true;
            });
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);

        HandleIncomingStatusMessage();
        ParkEmptyCellsResponse(tx.Hash!, cellMask);
        registry.ClearReceivedCalls();

        // Past the 5s backoff but inside the 15s pending-request TTL, for both the original park
        // and the one the callback interleaves.
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reParked, Is.True);
            registry.Received(2).TryRestoreAnnouncement(_handler, tx.Hash!, Arg.Is<AnnouncementRestoration>(ticket => ticket.Mask.IsFull));
        }
    }

    // The cells were already validated before RecordCells is reached, so a refusal there is almost
    // always local capacity and must not cost the peer its provider standing.
    [Test]
    public void should_keep_announcement_when_registry_refuses_delivered_cells()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        ConfigureAnnouncementRestoration(registry);
        registry.TryRequestCells(Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>(), Arg.Any<PublicKey>()).Returns(true);
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

        using (Assert.EnterMultipleScope())
        {
            registry.Received(1).RecordCells(_handler, tx.Hash!, cellMask, Arg.Any<byte[][]>());
            AssertProviderAnnouncementPreserved(registry, tx.Hash!);
        }

        // The retry is still throttled, so it only goes out once the backoff falls due.
        registry.ClearReceivedCalls();
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));
        registry.Received(1).TryRequestCells(tx.Hash!, cellMask, Arg.Any<PublicKey>());
    }

    // A restore the registry keeps refusing must not be swallowed when the pending entry ages out.
    [Test]
    public void should_attempt_announcement_restore_once_more_when_pending_request_expires()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        ConfigureAnnouncementRestoration(registry);
        registry.TryRestoreAnnouncement(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<Hash256>(), Arg.Any<AnnouncementRestoration>())
            .Returns(false);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);

        HandleIncomingStatusMessage();
        ParkEmptyCellsResponse(tx.Hash!, cellMask);
        registry.ClearReceivedCalls();

        // Past the 15s pending-request TTL, so the entry is dropped on this tick.
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(16));

        registry.Received(1).TryRestoreAnnouncement(_handler, tx.Hash!, Arg.Is<AnnouncementRestoration>(ticket => ticket.Mask.IsFull));
    }

    // Placing the parked cells with another provider removes the pending entry; if the announcement
    // restore was rejected on that same tick, the provider would be lost for good.
    [Test]
    public void should_keep_retrying_rejected_restore_after_cells_are_requested_elsewhere()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        ConfigureAnnouncementRestoration(registry);
        registry.TryRequestCells(Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>(), Arg.Any<PublicKey>()).Returns(true);
        registry.TryRestoreAnnouncement(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<Hash256>(), Arg.Any<AnnouncementRestoration>())
            .Returns(false, true);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);

        HandleIncomingStatusMessage();
        ParkEmptyCellsResponse(tx.Hash!, cellMask);
        registry.ClearReceivedCalls();

        // The restore is refused, but the cells are placed with another provider on the same tick.
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));
        registry.Received(1).TryRequestCells(tx.Hash!, cellMask, Arg.Any<PublicKey>());

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(7));

        registry.Received(1).TryRestoreAnnouncement(_handler, tx.Hash!, Arg.Is<AnnouncementRestoration>(ticket => ticket.Mask.IsFull));
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8));
        registry.Received(2).TryRestoreAnnouncement(_handler, tx.Hash!, Arg.Is<AnnouncementRestoration>(ticket => ticket.Mask.IsFull));
    }

    // A refused restore retains its ticket until a later attempt succeeds.
    [Test]
    public void should_keep_retrying_announcement_restore_until_the_registry_accepts_it()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        ConfigureAnnouncementRestoration(registry);
        registry.TryRestoreAnnouncement(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<Hash256>(), Arg.Any<AnnouncementRestoration>())
            .Returns(false, true);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);

        HandleIncomingStatusMessage();
        ParkEmptyCellsResponse(tx.Hash!, cellMask);
        registry.ClearReceivedCalls();

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(7));
        // The third tick must not restore again now that the second one was accepted.
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8));

        registry.Received(2).TryRestoreAnnouncement(_handler, tx.Hash!, Arg.Is<AnnouncementRestoration>(ticket => ticket.Mask.IsFull));
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
        _session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
    }

    [Test]
    public void registry_should_request_cells_from_non_preferred_announcing_peer_when_available()
    {
        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer preferredPeer = new(TestItem.PublicKeyA);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);

        registry.AddPeer(preferredPeer);
        registry.AddPeer(otherPeer);
        registry.RecordAnnouncement(preferredPeer, hash, cellMask);
        registry.RecordAnnouncement(otherPeer, hash, cellMask);

#pragma warning disable NUnit2045 // Keep this state-changing operation separate from its prerequisites, dependent observations, or subsequent mutations.
        Assert.That(registry.TryRequestCells(hash, cellMask, preferredPeer.Id), Is.True);
#pragma warning restore NUnit2045
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

#pragma warning disable NUnit2045 // Keep this state-changing operation separate from its prerequisites, dependent observations, or subsequent mutations.
        Assert.That(registry.TryRequestCells(hash, firstMask, secondPeer.Id), Is.True);
#pragma warning restore NUnit2045
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
#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
        Assert.That(await staleSend, Is.False);
#pragma warning restore NUnit2045
        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
        Assert.That(peer.CellRequests, Has.Count.EqualTo(2));

        registry.OnCellsRequestCompleted(hash, cellMask, peer);
#pragma warning disable NUnit2045 // Keep this state-changing operation separate from its prerequisites, dependent observations, or subsequent mutations.
        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
#pragma warning restore NUnit2045
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
#pragma warning disable NUnit2045 // Keep this state-changing operation separate from its prerequisites, dependent observations, or subsequent mutations.
        Assert.That(registry.TryRequestCells(trustedHash, cellMask, quotaPeer.Id), Is.True);
#pragma warning restore NUnit2045
        Assert.That(trustedPeer.CellRequests, Is.EqualTo(new[] { (trustedHash, cellMask) }));
    }

    [Test]
    public void registry_should_request_expanded_custody_delta_with_provider_noise()
    {
        BlobCustodyTracker custodyTracker = new();
        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, custodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        BlobCellMask firstMask = BlobCellMask.FromIndices([4]);
        BlobCellMask expandedMask = BlobCellMask.FromIndices([4, 9]);

        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, hash, BlobCellMask.Full);

#pragma warning disable NUnit2045 // The custody update must succeed before checking the resulting cell requests.
        Assert.That(custodyTracker.Update(firstMask), Is.True);
#pragma warning restore NUnit2045
        Assert.That(peer.CellRequests, Has.Count.EqualTo(1));
        AssertCustodyRequest(peer.CellRequests[0], hash, firstMask);
        registry.OnCellsRequestCompleted(hash, peer.CellRequests[0].CellMask, peer);
#pragma warning disable NUnit2045 // The custody update must succeed before checking the resulting cell requests.
        Assert.That(custodyTracker.Update(expandedMask), Is.True);
#pragma warning restore NUnit2045

        Assert.That(peer.CellRequests, Has.Count.EqualTo(2));
        AssertCustodyRequest(peer.CellRequests[1], hash, BlobCellMask.FromIndices([9]));
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
        Assert.That(peer.CellRequests, Has.Count.EqualTo(1));
        AssertCustodyRequest(peer.CellRequests[0], hash, custodyMask);
    }

    [Test]
    public void registry_scheduler_rejection_should_not_dispose_registry([Values] bool rejectCustodyUpdate)
    {
        BlobCustodyTracker custodyTracker = new();
        ManualTimerFactory timerFactory = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            custodyTracker,
            new RejectingBackgroundTaskScheduler(),
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timerFactory: timerFactory);

        if (rejectCustodyUpdate)
        {
            custodyTracker.Update(BlobCellMask.FromIndices([4]));
        }
        else
        {
            timerFactory.Timer.Fire();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(timerFactory.Timer.Enabled, Is.True);
            Assert.That(registry.TryAcquireCellServeWork(1), Is.True);
        }
        registry.ReleaseCellServeWork();
    }

    [Test]
    public void registry_should_retry_custody_update_after_scan_exception()
    {
        BlobCustodyTracker custodyTracker = new();
        QueuedBackgroundTaskScheduler scheduler = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            custodyTracker,
            scheduler,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        Hash256 hash = HashFromInt(1);
        BlobCellMask initialMask = BlobCellMask.FromIndices([4]);
        BlobCellMask expandedMask = BlobCellMask.FromIndices([4, 9]);
        bool throwOnLookup = false;
        _transactionPool.TryGetPendingBlobCellMask(Arg.Any<ValueHash256>(), out Arg.Any<BlobCellMask>())
            .Returns(call =>
            {
                if (throwOnLookup)
                {
                    throw new InvalidOperationException("Injected custody scan failure.");
                }

                call[1] = BlobCellMask.Empty;
                return false;
            });
        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, hash, BlobCellMask.Full);

        throwOnLookup = true;
        custodyTracker.Update(initialMask);
        Assert.That(() => scheduler.RunNext(), Throws.TypeOf<InvalidOperationException>());

        throwOnLookup = false;
        custodyTracker.Update(expandedMask);
        scheduler.RunNext();

        Assert.That(peer.CellRequests, Has.Count.EqualTo(1));
        AssertCustodyRequest(peer.CellRequests[0], hash, expandedMask);
    }

    [Test]
    public void registry_should_stop_cancelled_custody_scan_and_retry_pending_revision()
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
        Hash256 firstHash = HashFromInt(1);
        Hash256 secondHash = HashFromInt(2);
        using CancellationTokenSource cancellation = new();
        bool cancelOnLookup = false;
        _transactionPool.TryGetPendingBlobCellMask(Arg.Any<ValueHash256>(), out Arg.Any<BlobCellMask>())
            .Returns(call =>
            {
                call[1] = BlobCellMask.Empty;
                if (cancelOnLookup)
                {
                    cancelOnLookup = false;
                    cancellation.Cancel();
                }

                return false;
            });
        registry.AddPeer(peer);
        registry.RecordAnnouncement(peer, firstHash, BlobCellMask.Full);
        registry.RecordAnnouncement(peer, secondHash, BlobCellMask.Full);

        cancelOnLookup = true;
        custodyTracker.Update(BlobCellMask.FromIndices([4]));
        scheduler.RunNext(cancellation.Token);

        Assert.That(peer.CellRequests, Has.Count.EqualTo(1));
        registry.OnCellsRequestCompleted(peer.CellRequests[0].Hash, peer.CellRequests[0].CellMask, peer);

        timerFactory.Timer.Fire();
        scheduler.RunNext();
        scheduler.RunNext();

        Assert.That(peer.CellRequests, Has.Count.EqualTo(3));
        Assert.That(peer.CellRequests.Skip(1).Select(static request => request.Hash), Is.EquivalentTo(new[] { firstHash, secondHash }));
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

#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
        Assert.That(await staleUpdate.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
#pragma warning restore NUnit2045
        Assert.That(peer.CellRequests, Has.Count.EqualTo(1));
        AssertCustodyRequest(peer.CellRequests[0], hash, currentMask);
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

#pragma warning disable NUnit2045 // The custody update must succeed before checking the resulting cell requests.
        Assert.That(registry.GetFullProviderAnnouncementCount(hash), Is.Zero);
#pragma warning restore NUnit2045
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
        registry.RemoveAnnouncementPermanently(oldPeer, hash);
        Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(oldPeer.CellRequests, Is.Empty);
            Assert.That(replacementPeer.CellRequests, Is.EqualTo(new[] { (hash, cellMask) }));
        }

        registry.OnCellsRequestCompleted(hash, cellMask, replacementPeer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryRequestCells(hash, cellMask, TestItem.PublicKeyA), Is.True);
            Assert.That(replacementPeer.CellRequests, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task registry_should_finish_old_generation_cleanup_before_publishing_replacement()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out _);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer oldPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer replacementPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer retryPeer = new(TestItem.PublicKeyD);
        Hash256 secondHash = HashFromInt(2);
        using ManualResetEventSlim retryEntered = new();
        using ManualResetEventSlim releaseRetry = new();
        retryPeer.TransactionRequestHandler = _ =>
        {
            retryEntered.Set();
            return releaseRetry.Wait(TimeSpan.FromSeconds(5));
        };
        _transactionPool.ValidateTxForBlobSampling(tx).Returns(AcceptTxResult.Accepted);
        registry.AddPeer(oldPeer);
        registry.AddPeer(retryPeer);
        registry.RecordAnnouncement(retryPeer, tx.Hash!, cellMask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(oldPeer, tx), Is.Null);
            Assert.That(registry.RecordAnnouncement(oldPeer, secondHash, cellMask), Is.True);
        }

        Task replacement = Task.Run(() => registry.AddPeer(replacementPeer));
        Assert.That(retryEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        bool recorded;
        try
        {
            recorded = registry.RecordAnnouncement(replacementPeer, secondHash, cellMask);
        }
        finally
        {
            releaseRetry.Set();
        }

        await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded, Is.True);
            Assert.That(registry.TryRequestCells(secondHash, cellMask, retryPeer.Id), Is.True);
            Assert.That(replacementPeer.CellRequests, Is.EqualTo(new[] { (secondHash, cellMask) }));
        }
    }

    [Test]
    public void registry_should_try_another_peer_after_per_peer_in_flight_capacity_is_reached()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer saturatedPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer fallbackPeer = new(TestItem.PublicKeyD);
        registry.AddPeer(saturatedPeer);
        registry.AddPeer(fallbackPeer);
        for (int i = 0; i < 8; i++)
        {
            Hash256 hash = HashFromInt(i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(saturatedPeer, hash, BlobCellMask.Full), Is.True);
                Assert.That(registry.RecordAnnouncement(fallbackPeer, hash, BlobCellMask.Full), Is.True);
                Assert.That(registry.TryRequestCells(hash, BlobCellMask.Full, fallbackPeer.Id), Is.True);
            }
        }

        Hash256 targetHash = HashFromInt(100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(saturatedPeer, targetHash, BlobCellMask.Full), Is.True);
            Assert.That(registry.RecordAnnouncement(fallbackPeer, targetHash, BlobCellMask.Full), Is.True);

            Assert.That(registry.TryRequestCells(targetHash, BlobCellMask.Full, fallbackPeer.Id), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(saturatedPeer.CellRequests, Has.Count.EqualTo(8));
            Assert.That(fallbackPeer.CellRequests, Is.EqualTo(new[] { (targetHash, BlobCellMask.Full) }));
        }
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(trackedPeer, hash, cellMask), Is.True);
                Assert.That(registry.RecordAnnouncement(retainingPeer, hash, cellMask), Is.True);
            }
            registry.RemoveAnnouncementPermanently(trackedPeer, hash);
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);

            Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.False);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.True);
        }

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(removedPeer, tx.Hash!, removedMask, removedCells), Is.True);
            Assert.That(registry.RecordCells(retainedPeer, tx.Hash!, retainedMask, retainedCells), Is.True);
        }

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.EqualTo(AcceptTxResult.Invalid));

            Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.False);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);

            Assert.That(peer.Disconnects, Is.Empty);
            Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.False);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initialResult, Is.Null);
            Assert.That(submitted, Is.False);

            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);
        }

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

        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
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
    public void registry_should_forget_invalid_blob_proofs_verdict_before_submitting_full_blob_tx()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.ForgetInvalidBlobProofs(tx.Hash!).Returns(true);
        _transactionPool.SubmitTx(tx, TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);

        registry.AddPeer(peer);
        AcceptTxResult? result = registry.RecordTransaction(peer, tx);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        Received.InOrder(() =>
        {
            _transactionPool.ForgetInvalidBlobProofs(tx.Hash!);
            _transactionPool.SubmitTx(tx, TxHandlingOptions.None);
        });
        _transactionPool.Received(1).SubmitTx(tx, TxHandlingOptions.None);
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

        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyA);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.InvalidBlobProofs);

        registry.AddPeer(peer);
        registry.AddPeer(otherPeer);
        registry.RecordAnnouncement(otherPeer, tx.Hash!, cellMask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, invalidCells), Is.True);
        }

        _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        Assert.That(peer.Disconnects, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Disconnects[0].Reason, Is.EqualTo(DisconnectReason.BreachOfProtocol));
            Assert.That(otherPeer.Disconnects, Is.Empty);
            Assert.That(otherPeer.CellRequests, Is.EqualTo(new[] { (tx.Hash!, cellMask) }));
            Assert.That(otherPeer.TransactionRequests, Is.EqualTo(new[] { tx.Hash! }));
        }
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
            reentrantMutationCompleted = mutation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        };
        registry.AddPeer(txPeer);
        registry.AddPeer(cellPeer);
        registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(txPeer, tx), Is.Null);

            Assert.That(registry.RecordCells(cellPeer, tx.Hash!, cellMask, cells), Is.True);

            Assert.That(reentrantMutationCompleted, Is.True);
        }
    }

    [Test]
    public void registry_should_merge_disjoint_early_cells_with_source_provenance()
    {
        Transaction tx = BuildSparseBlobTransaction(out _, out _, out byte[][] fullCells);
        BlobCellMask firstMask = BlobCellMask.FromIndices([4]);
        BlobCellMask secondMask = BlobCellMask.FromIndices([9]);
        byte[][] firstCells = BlobCellsHelper.SelectFlattenedCells(fullCells, BlobCellMask.Full, firstMask, tx.BlobVersionedHashes!.Length);
        byte[][] secondCells = BlobCellsHelper.SelectFlattenedCells(fullCells, BlobCellMask.Full, secondMask, tx.BlobVersionedHashes.Length);
        using SparseBlobPoolPeerRegistry registry = new(
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(firstPeer, tx.Hash!, firstMask, firstCells), Is.True);
            Assert.That(registry.RecordCells(secondPeer, tx.Hash!, secondMask, secondCells), Is.True);

            Assert.That(registry.RecordTransaction(firstPeer, tx), Is.EqualTo(AcceptTxResult.Accepted));
        }
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

        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer txPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer cellPeer = new(TestItem.PublicKeyA);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.InvalidBlobProofs);

        registry.AddPeer(txPeer);
        registry.AddPeer(cellPeer);
        registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(txPeer, tx), Is.Null);
            Assert.That(registry.RecordCells(cellPeer, tx.Hash!, cellMask, cells), Is.True);
        }

        _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(txPeer.Disconnects, Is.Empty);
            Assert.That(cellPeer.Disconnects, Is.Empty);
            Assert.That(cellPeer.CellRequests, Is.Empty);
            Assert.That(cellPeer.TransactionRequests, Is.Empty);
            Assert.That(registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask), Is.False);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);
        }

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);

            Assert.That(submitted, Is.True);
        }
        _transactionPool.TryGetPendingBlobCellMask(tx.Hash!, out Arg.Any<BlobCellMask>())
            .Returns(x =>
            {
                x[1] = cellMask;
                return true;
            });
        timerFactory.Timer.Fire();
        Assert.That(peer.CellRequests, Is.Empty);

        BlobCellMask expandedCustody = cellMask | BlobCellMask.FromIndices([9]);
#pragma warning disable NUnit2045 // The custody update must succeed before checking the resulting cell requests.
        Assert.That(custodyTracker.Update(expandedCustody), Is.True);
#pragma warning restore NUnit2045
        Assert.That(peer.CellRequests, Has.Count.EqualTo(1));
        AssertCustodyRequest(peer.CellRequests[0], tx.Hash!, BlobCellMask.FromIndices([9]));
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);

            Assert.That(registry.RecordAnnouncement(peer, tx.Hash!, BlobCellMask.Full), Is.True);
        }
        now += TimeSpan.FromMinutes(2);
        timerFactory.Timer.Fire();
        BlobCellMask expandedCustody = cellMask | BlobCellMask.FromIndices([9]);
#pragma warning disable NUnit2045 // The custody update must succeed before checking the resulting cell requests.
        Assert.That(custodyTracker.Update(expandedCustody), Is.True);
#pragma warning restore NUnit2045

        Assert.That(peer.CellRequests, Has.Count.EqualTo(1));
        AssertCustodyRequest(peer.CellRequests[0], tx.Hash!, BlobCellMask.FromIndices([9]));
    }

    [Test]
    public void registry_should_not_downgrade_full_cells_with_later_sampled_cells()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells, out byte[][] fullCells);
        using SparseBlobPoolPeerRegistry registry = new(
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(fullPeer, tx.Hash!, BlobCellMask.Full, fullCells), Is.True);
            Assert.That(registry.RecordCells(sampledPeer, tx.Hash!, sampledMask, sampledCells), Is.True);
            Assert.That(registry.RecordTransaction(sampledPeer, tx), Is.EqualTo(AcceptTxResult.Accepted));

            Assert.That(submitted, Is.True);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(sampledPeer, tx), Is.Null);
            Assert.That(registry.RecordCells(sampledPeer, tx.Hash!, sampledMask, sampledCells), Is.True);
            Assert.That(submitted, Is.True);
        }

        timerFactory.Timer.Fire();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstFullPeer.CellRequests, Is.Empty);
            Assert.That(secondFullPeer.CellRequests, Is.Empty);
        }
    }

    [Test]
    public async Task registry_should_not_submit_same_sparse_tx_twice_when_transaction_and_cells_race()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells, out byte[][] fullCells);
        BlobCellMask additionalMask = BlobCellMask.FromIndices([9]);
        byte[][] additionalCells = BlobCellsHelper.SelectFlattenedCells(fullCells, BlobCellMask.Full, additionalMask, tx.BlobVersionedHashes!.Length);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyD);
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
        registry.AddPeer(otherPeer);
        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);

        Task<bool> firstSubmit = Task.Run(() => registry.RecordCells(peer, tx.Hash!, sampledMask, sampledCells));
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(submitEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
#pragma warning restore NUnit2045

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
        Assert.That(registry.RecordCells(otherPeer, tx.Hash!, additionalMask, additionalCells), Is.False);
        Assert.That(Volatile.Read(ref submitCount), Is.EqualTo(1));

        releaseSubmit.Set();
#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
        Assert.That(await firstSubmit.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
#pragma warning restore NUnit2045
        Assert.That(Volatile.Read(ref submitCount), Is.EqualTo(1));
    }

    [Test]
    public void registry_should_not_reapply_cells_from_synchronous_pending_callback_during_submission()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            _transactionPool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(call.Arg<Transaction>()));
            return AcceptTxResult.Accepted;
        });
        registry.AddPeer(peer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);

            Assert.That(registry.RecordCells(peer, tx.Hash!, sampledMask, sampledCells), Is.True);
        }

        _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        _transactionPool.DidNotReceive().MergeBlobCells(
            Arg.Any<Hash256>(),
            Arg.Any<BlobCellMask>(),
            Arg.Any<byte[][]>());
    }

    [Test]
    public async Task registry_should_reject_new_cells_while_buffered_cells_are_being_applied()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells, out byte[][] fullCells);
        BlobCellMask additionalMask = BlobCellMask.FromIndices([9]);
        byte[][] additionalCells = BlobCellsHelper.SelectFlattenedCells(
            fullCells,
            BlobCellMask.Full,
            additionalMask,
            tx.BlobVersionedHashes!.Length);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer firstPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer secondPeer = new(TestItem.PublicKeyD);
        using ManualResetEventSlim mergeEntered = new();
        using ManualResetEventSlim releaseMerge = new();
        int mergeCount = 0;
        _transactionPool.MergeBlobCells(tx.Hash!, Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref mergeCount) != 1)
            {
                return BlobCellMergeResult.Accepted;
            }

            mergeEntered.Set();
            if (!releaseMerge.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting to release sparse cell application.");
            }

            return BlobCellMergeResult.InvalidCells;
        });
        registry.AddPeer(firstPeer);
        registry.AddPeer(secondPeer);
        Assert.That(registry.RecordCells(firstPeer, tx.Hash!, sampledMask, sampledCells), Is.True);

        Task<bool> firstApplication = Task.Run(() => registry.TryApplyRecordedCells(tx.Hash!));
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(mergeEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(registry.RecordCells(secondPeer, tx.Hash!, additionalMask, additionalCells), Is.False);
            }
        }
        finally
        {
            releaseMerge.Set();
        }

#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
        Assert.That(await firstApplication.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
#pragma warning restore NUnit2045
        Assert.That(registry.RecordCells(secondPeer, tx.Hash!, additionalMask, additionalCells), Is.True);
        Assert.That(registry.TryApplyRecordedCells(tx.Hash!), Is.True);
        Assert.That(Volatile.Read(ref mergeCount), Is.EqualTo(2));
    }

    [Test]
    public async Task registry_should_not_attach_transaction_cells_while_buffered_cells_are_being_applied()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells, out byte[][] fullCells);
        BlobCellMask additionalMask = BlobCellMask.FromIndices([9]);
        byte[][] additionalCells = BlobCellsHelper.SelectFlattenedCells(
            fullCells,
            BlobCellMask.Full,
            additionalMask,
            tx.BlobVersionedHashes!.Length);
        Transaction txWithAdditionalCells = BuildElidedBlobTransaction(tx);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)txWithAdditionalCells.NetworkWrapper!;
        txWithAdditionalCells.NetworkWrapper = wrapper with { CellMask = additionalMask, Cells = additionalCells };
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        TestSparseBlobPeer firstPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer secondPeer = new(TestItem.PublicKeyD);
        using ManualResetEventSlim mergeEntered = new();
        using ManualResetEventSlim releaseMerge = new();
        int mergeCount = 0;
        _transactionPool.MergeBlobCells(tx.Hash!, Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>()).Returns(_ =>
        {
            Interlocked.Increment(ref mergeCount);
            mergeEntered.Set();
            if (!releaseMerge.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting to release sparse cell application.");
            }

            return BlobCellMergeResult.Accepted;
        });
        registry.AddPeer(firstPeer);
        registry.AddPeer(secondPeer);
        Assert.That(registry.RecordCells(firstPeer, tx.Hash!, sampledMask, sampledCells), Is.True);

        Task<bool> application = Task.Run(() => registry.TryApplyRecordedCells(tx.Hash!));
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(mergeEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(registry.RecordTransaction(secondPeer, txWithAdditionalCells), Is.Null);
            }
        }
        finally
        {
            releaseMerge.Set();
        }

#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
        Assert.That(await application.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
#pragma warning restore NUnit2045
        Assert.That(Volatile.Read(ref mergeCount), Is.EqualTo(1));
    }

    [Test]
    public async Task registry_should_submit_transaction_recorded_while_buffered_cells_are_being_applied()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        using ManualResetEventSlim mergeEntered = new();
        using ManualResetEventSlim releaseMerge = new();
        _transactionPool.MergeBlobCells(tx.Hash!, Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>()).Returns(_ =>
        {
            mergeEntered.Set();
            if (!releaseMerge.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting to release sparse cell application.");
            }

            return BlobCellMergeResult.TransactionUnavailable;
        });
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        registry.AddPeer(peer);
        Assert.That(registry.RecordCells(peer, tx.Hash!, sampledMask, sampledCells), Is.True);

        Task<bool> application = Task.Run(() => registry.TryApplyRecordedCells(tx.Hash!));
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(mergeEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            }
        }
        finally
        {
            releaseMerge.Set();
        }

        Assert.That(await application.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void registry_should_not_stage_late_transaction_response_after_submission()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        _transactionPool.ValidateTxForBlobSampling(tx).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        registry.AddPeer(peer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.RecordCells(peer, tx.Hash!, sampledMask, sampledCells), Is.True);
            Assert.That(GetTrackedTransactionBytes(registry), Is.Zero);
        }

        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetTrackedTransactionBytes(registry), Is.Zero);
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        }
    }

    [Test]
    public async Task registry_should_defer_clear_until_sparse_transaction_submission_finishes()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        using ManualResetEventSlim submitEntered = new();
        using ManualResetEventSlim releaseSubmit = new();
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(_ =>
        {
            submitEntered.Set();
            if (!releaseSubmit.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting to release sparse tx submit.");
            }

            return AcceptTxResult.Accepted;
        });
        registry.AddPeer(peer);
        Assert.That(registry.RecordTransaction(peer, tx), Is.Null);

        Task<bool> submission = Task.Run(() => registry.RecordCells(peer, tx.Hash!, sampledMask, sampledCells));
        Assert.That(submitEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        try
        {
            registry.Clear(tx.Hash!);
            Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.True);
        }
        finally
        {
            releaseSubmit.Set();
        }

        Assert.That(await submission.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        _transactionPool.Received(1).RemoveTransaction(tx.Hash!);
        Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.False);
    }

    [Test]
    public async Task registry_should_honor_deferred_clear_when_sparse_transaction_submission_fails()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer transactionPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer cellsPeer = new(TestItem.PublicKeyD);
        using ManualResetEventSlim submitEntered = new();
        using ManualResetEventSlim releaseSubmit = new();
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(_ =>
        {
            submitEntered.Set();
            if (!releaseSubmit.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting to release sparse tx submit.");
            }

            return AcceptTxResult.InvalidBlobProofs;
        });
        registry.AddPeer(transactionPeer);
        registry.AddPeer(cellsPeer);
        Assert.That(registry.RecordTransaction(transactionPeer, tx), Is.Null);

        Task<bool> submission = Task.Run(() => registry.RecordCells(cellsPeer, tx.Hash!, sampledMask, sampledCells));
        Assert.That(submitEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        try
        {
            registry.Clear(tx.Hash!);
            Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.True);
        }
        finally
        {
            releaseSubmit.Set();
        }

        Assert.That(await submission.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.HasRecordedTransaction(tx.Hash!), Is.False);
            Assert.That(transactionPeer.TransactionRequests, Is.Empty);
            Assert.That(cellsPeer.TransactionRequests, Is.Empty);
        }
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
        _handler.HandleMessage(new NettyBufferPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : P2PMessage
    {
        using DisposableByteBuffer packet = _svc.ZeroSerialize(msg).AsDisposable();
        packet.ReadByte();
        _handler.HandleMessage(new NettyBufferPacket(packet) { PacketType = (byte)messageCode });
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
    {
        for (int i = _deliveredMessages.Count - 1; i >= 0; i--)
        {
            if (_deliveredMessages[i] is GetPooledTransactionsMessage message
                && message.EthMessage.Hashes.Contains(hash.ValueHash256))
            {
                return message.RequestId;
            }
        }

        throw new InvalidOperationException($"No pooled transaction request found for {hash}.");
    }

    private bool WasPooledTransactionRequested(Hash256 hash)
    {
        for (int i = 0; i < _deliveredMessages.Count; i++)
        {
            if (_deliveredMessages[i] is GetPooledTransactionsMessage message
                && message.EthMessage.Hashes.Contains(hash.ValueHash256))
            {
                return true;
            }
        }

        return false;
    }

    private bool WasCellTransactionRequested(Hash256 hash)
    {
        for (int i = 0; i < _deliveredMessages.Count; i++)
        {
            if (_deliveredMessages[i] is GetCellsMessage72 message
                && ContainsHash(message.Hashes, hash))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsHash(IReadOnlyList<ValueHash256> hashes, Hash256 expected)
    {
        for (int i = 0; i < hashes.Count; i++)
        {
            if (hashes[i] == expected)
            {
                return true;
            }
        }

        return false;
    }

    private TestEth72ProtocolHandler RecreateTestHandler(IBackgroundTaskScheduler backgroundTaskScheduler)
    {
        _handler.Dispose();
        TestEth72ProtocolHandler handler = new(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            backgroundTaskScheduler,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            _txPoolConfig,
            _specProvider,
            _blobCustodyTracker,
            _sparseBlobPoolPeerRegistry,
            _txGossipPolicy);
        _handler = handler;
        handler.Init();
        return handler;
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
        for (int nonce = 0; nonce < MaxSamplerTransactionAttempts; nonce++)
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

    // EIP-8141: a type-6 carrying blobs is not TxType.SupportsBlobs, so it takes the plain eth/72 path end to
    // end - announced at its full network size with no cell mask, then served, delivered and submitted whole
    // over a correlated response, with the sparse cell protocol staying type-3 only throughout.
    // Both wrapper versions, because the sidecar validator accepts either from the wire regardless of the
    // head's own proof version, and the per-blob proof count it must check differs between them.
    [TestCase(ProofVersion.V0)]
    [TestCase(ProofVersion.V1)]
    public void should_carry_a_blob_bearing_frame_tx_over_the_plain_announcement_path(ProofVersion proofVersion)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(true);
        _specProvider.GetCurrentHeadSpec().Returns(spec);
        _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        HandleIncomingStatusMessage();

        Transaction tx = BlobCarryingFrameTx(proofVersion);
        int announcedSize = tx.GetLength();
        _deliveredMessages.Clear();
        _handler.SendNewTransaction(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveredMessages.OfType<TransactionsMessage>(), Is.Empty,
                "a blob-carrying frame tx must never be broadcast with its sidecar");
            _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
                m.Types.Count == 1
                && m.Types[0] == (byte)TxType.FrameTx
                && m.Sizes[0] == announcedSize
                && BlobCellMask.FromBytes(m.CellMask).IsEmpty));
        }

        // The pooled response has to match that announcement, so a type-6 is served whole rather than from
        // the sidecar-free record a type-3 response is elided to.
        _transactionPool.TryGetPendingTransactionWithoutBlobs(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = BuildElidedBlobTransaction(tx);
                return true;
            });
        _transactionPool.TryGetPendingTransaction(tx.Hash!, out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = tx;
                return true;
            });

        _deliveredMessages.Clear();
        using GetPooledTransactionsMessage serveRequest = new(new[] { tx.Hash!.ValueHash256 }.ToPooledList());
        HandleZeroMessage(serveRequest, Eth66MessageCode.GetPooledTransactions);

        Transaction servedTx = _deliveredMessages.OfType<PooledTransactionsMessage>().Single().EthMessage.Transactions[0];
        Assert.That(servedTx.NetworkWrapper, Is.TypeOf<ShardBlobNetworkWrapper>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(((ShardBlobNetworkWrapper)servedTx.NetworkWrapper!).HasFullBlobs(), Is.True,
                "an elided type-6 could never be completed - cells are requested for type-3 only");
            Assert.That(servedTx.GetLength(), Is.EqualTo(announcedSize),
                "the served size must match the announced one");
        }

        _deliveredMessages.Clear();
        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.FrameTx], [announcedSize], [tx.Hash!], BlobCellMask.Empty.ToBytes());
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage>().Count(), Is.EqualTo(1));
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>(), Is.Empty,
                "cells are requested for type-3 only");
        }

        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);
        using PooledTransactionsMessage66 response = new(
            requestId, new PooledTransactionsMessage65(new[] { servedTx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            _transactionPool.Received(1).SubmitTx(
                Arg.Is<Transaction>(submitted => submitted.Hash == tx.Hash && submitted.CarriesBlobs),
                Arg.Any<TxHandlingOptions>());
        }
    }

    // The type-6 sidecar is verified on arrival because nothing else will: SupportsBlobs is type-3 only, so
    // gating on it admits a blobless carrier that no later Cells response can complete.
    [Test]
    public void should_reject_a_blob_bearing_frame_tx_delivered_without_its_sidecar()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(true);
        _specProvider.GetCurrentHeadSpec().Returns(spec);
        _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        HandleIncomingStatusMessage();

        Transaction tx = BlobCarryingFrameTx();
        Transaction elidedTx = BuildElidedBlobTransaction(tx);

        // Announced at the elided size, so the shape check passes and sidecar validation is what must reject it.
        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.FrameTx], [elidedTx.GetLength()], [tx.Hash!], BlobCellMask.Empty.ToBytes());
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);

        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);
        using PooledTransactionsMessage66 response = new(
            requestId, new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
            _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }
    }

    private static Transaction BlobCarryingFrameTx(ProofVersion version = ProofVersion.V1)
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(0UL)
            .WithShardBlobTxTypeAndFields(spec: version is ProofVersion.V1 ? Osaka.Instance : Cancun.Instance)
            .SignedAndResolved()
            .TestObject;
        tx.Type = TxType.FrameTx;
        tx.Frames = [];
        tx.FrameSignatures = [];
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static Transaction FrameTx(PrivateKey signer) => Build.A.Transaction
        .WithType(TxType.FrameTx)
        .WithNonce(0)
        .WithMaxFeePerGas(1.GWei)
        .WithMaxPriorityFeePerGas(1.GWei)
        .WithGasLimit(100_000)
        .WithTo(TestItem.AddressB)
        .SignedAndResolved(signer).TestObject;

    private static Transaction FrameTx(PrivateKey signer, ulong validationGas)
    {
        Transaction tx = FrameTx(signer);
        tx.Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, validationGas, value: default, Array.Empty<byte>())];
        return tx;
    }

    private static void AssertCustodyRequest(
        (Hash256 Hash, BlobCellMask CellMask) request,
        Hash256 expectedHash,
        BlobCellMask custodyDelta)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Hash, Is.EqualTo(expectedHash));
            Assert.That((request.CellMask & custodyDelta), Is.EqualTo(custodyDelta));
            Assert.That(request.CellMask.Count, Is.EqualTo(custodyDelta.Count + 1));
        }
    }

    [Test]
    public void registry_should_make_room_for_a_peer_within_its_share_when_other_peers_fill_the_transaction_budget()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer[] fillingPeers = FillTransactionBudget(registry);
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peer, tx.Hash!, sampledMask, sampledCells), Is.True);
            _transactionPool.Received(1).SubmitTx(Arg.Is<Transaction>(t => t.Hash == tx.Hash), TxHandlingOptions.None);
            Assert.That(GetTrackedTransactionBytes(registry), Is.LessThanOrEqualTo(FillingPeerCount * FillingPeerBytes));
            Assert.That(fillingPeers.Sum(p => GetTrackedHashCount(registry, p.Id)), Is.EqualTo(FillingPeerCount * FillingTransactionsPerPeer - 1));
            Assert.That(GetTrackedStateCount(registry), Is.EqualTo(FillingPeerCount * FillingTransactionsPerPeer));
        }
    }

    [Test]
    public void registry_should_not_make_room_for_a_peer_at_its_share_when_the_transaction_budget_is_full()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer[] fillingPeers = FillTransactionBudget(registry);

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)BuildSparseBlobTransaction(out _, out _).NetworkWrapper!;
        registry.RecordTransaction(fillingPeers[0], BuildFillingTransaction(FillingPeerCount * FillingTransactionsPerPeer, wrapper));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetTrackedTransactionBytes(registry), Is.EqualTo(FillingPeerCount * FillingPeerBytes));
            foreach (TestSparseBlobPeer fillingPeer in fillingPeers)
            {
                Assert.That(GetTrackedHashCount(registry, fillingPeer.Id), Is.EqualTo(FillingTransactionsPerPeer));
            }
        }
    }

    [Test]
    public void registry_should_request_an_evicted_transaction_again_from_another_announcer()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer announcer = new(TestItem.PublicKeys[FillingPeerCount + 1]);
        registry.AddPeer(announcer);
        FillTransactionBudget(registry, announcer);
        announcer.TransactionRequests.Clear();
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, BuildSparseBlobTransaction(out _, out _));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcer.TransactionRequests, Has.Count.EqualTo(1));
            Assert.That(GetTrackedTransactionBytes(registry), Is.LessThanOrEqualTo(FillingPeerCount * FillingPeerBytes));
            Assert.That(GetTrackedStateCount(registry), Is.EqualTo(FillingPeerCount * FillingTransactionsPerPeer + 1));
        }
    }

    [Test]
    public void registry_should_make_room_for_cells_of_a_recorded_transaction_when_other_peers_fill_the_cell_budget()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        TestSparseBlobPeer[] fillingPeers = FillEarlyCellsBudget(registry);
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);
        registry.RecordTransaction(peer, tx);

        bool recorded = registry.RecordCells(peer, tx.Hash!, sampledMask, sampledCells);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded, Is.True);
            _transactionPool.Received(1).SubmitTx(Arg.Is<Transaction>(t => t.Hash == tx.Hash), TxHandlingOptions.None);
            Assert.That(GetEarlyCellsBytes(registry), Is.LessThanOrEqualTo(FillingPeerCount * FillingPeerBytes));
            Assert.That(fillingPeers.Sum(p => GetTrackedHashCount(registry, p.Id)), Is.EqualTo(FillingPeerCount * FillingCellEntriesPerPeer - 1));
        }
    }

    [Test]
    public void registry_should_not_make_room_for_cells_of_a_peer_at_its_share_when_the_cell_budget_is_full()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer[] fillingPeers = FillEarlyCellsBudget(registry);

        bool recorded = registry.RecordCells(fillingPeers[0], TestItem.Keccaks[FillingPeerCount * FillingCellEntriesPerPeer], BlobCellMask.Full, BuildFillingCells());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded, Is.False);
            Assert.That(GetEarlyCellsBytes(registry), Is.EqualTo(FillingPeerCount * FillingPeerBytes));
            foreach (TestSparseBlobPeer fillingPeer in fillingPeers)
            {
                Assert.That(GetTrackedHashCount(registry, fillingPeer.Id), Is.EqualTo(FillingCellEntriesPerPeer));
            }
        }
    }

    [Test]
    public void registry_should_make_room_from_another_peer_when_the_largest_holder_has_only_protected_transactions()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.FromHours(1),
            timestamper: new ManualTimestamper(DateTime.UnixEpoch));
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells).NetworkWrapper!;
        int[] transactionsPerPeer = [FillingTransactionsPerPeer, FillingTransactionsPerPeer - 1, FillingTransactionsPerPeer - 1, FillingTransactionsPerPeer - 1, 3];
        TestSparseBlobPeer[] fillingPeers = new TestSparseBlobPeer[transactionsPerPeer.Length];
        int nonce = 0;
        for (int i = 0; i < transactionsPerPeer.Length; i++)
        {
            fillingPeers[i] = new TestSparseBlobPeer(TestItem.PublicKeys[i]);
            registry.AddPeer(fillingPeers[i]);
            for (int j = 0; j < transactionsPerPeer[i]; j++)
            {
                Transaction filler = BuildFillingTransaction(nonce++, wrapper);
                registry.RecordTransaction(fillingPeers[i], filler);
                if (i == 0)
                {
                    Assert.That(registry.RecordCells(fillingPeers[i], filler.Hash!, cellMask, cells), Is.True);
                }
            }
        }

        Assert.That(GetTrackedTransactionBytes(registry), Is.EqualTo(FillingPeerCount * FillingPeerBytes));
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[transactionsPerPeer.Length]);
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, BuildFillingTransaction(nonce, wrapper));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetTrackedHashCount(registry, peer.Id), Is.EqualTo(1));
            Assert.That(GetTrackedHashCount(registry, fillingPeers[0].Id), Is.EqualTo(FillingTransactionsPerPeer));
            Assert.That(GetTrackedTransactionBytes(registry), Is.EqualTo(FillingPeerCount * FillingPeerBytes));
        }
    }

    [Test]
    public void registry_should_not_make_room_for_cells_that_are_already_recorded()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer[] fillingPeers = FillEarlyCellsBudget(registry);
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);

        bool recorded = registry.RecordCells(peer, TestItem.Keccaks[0], BlobCellMask.Full, BuildFillingCells());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded, Is.True);
            Assert.That(GetEarlyCellsBytes(registry), Is.EqualTo(FillingPeerCount * FillingPeerBytes));
            foreach (TestSparseBlobPeer fillingPeer in fillingPeers)
            {
                Assert.That(GetTrackedHashCount(registry, fillingPeer.Id), Is.EqualTo(FillingCellEntriesPerPeer));
            }
        }
    }

    [Test]
    public void registry_should_request_an_evicted_transaction_from_the_next_announcer_when_the_first_does_not_answer()
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            TimeSpan.Zero,
            cellRequestTimeout: TimeSpan.FromSeconds(1),
            timerFactory: timers,
            timestamper: clock);
        TestSparseBlobPeer firstAnnouncer = new(TestItem.PublicKeys[FillingPeerCount + 1]);
        TestSparseBlobPeer secondAnnouncer = new(TestItem.PublicKeys[FillingPeerCount + 2]);
        registry.AddPeer(firstAnnouncer);
        registry.AddPeer(secondAnnouncer);
        FillTransactionBudget(registry, firstAnnouncer, secondAnnouncer);
        firstAnnouncer.TransactionRequests.Clear();
        secondAnnouncer.TransactionRequests.Clear();
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, BuildSparseBlobTransaction(out _, out _));
        int requestsBeforeTimeout = firstAnnouncer.TransactionRequests.Count + secondAnnouncer.TransactionRequests.Count;
        clock.Add(TimeSpan.FromSeconds(2));
        timers.Timer.Fire();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requestsBeforeTimeout, Is.EqualTo(1));
            Assert.That(firstAnnouncer.TransactionRequests, Has.Count.EqualTo(1));
            Assert.That(secondAnnouncer.TransactionRequests, Has.Count.EqualTo(1));
            Assert.That(secondAnnouncer.TransactionRequests[0], Is.EqualTo(firstAnnouncer.TransactionRequests[0]));
        }
    }

    [Test]
    public void registry_should_not_retry_an_evicted_transaction_while_a_body_request_is_pending()
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            TimeSpan.Zero,
            cellRequestTimeout: TimeSpan.FromSeconds(1),
            timerFactory: timers,
            timestamper: clock);
        TestSparseBlobPeer firstAnnouncer = new(TestItem.PublicKeys[FillingPeerCount + 1]);
        TestSparseBlobPeer secondAnnouncer = new(TestItem.PublicKeys[FillingPeerCount + 2]);
        TestSparseBlobPeer thirdAnnouncer = new(TestItem.PublicKeys[FillingPeerCount + 3]);
        registry.AddPeer(firstAnnouncer);
        registry.AddPeer(secondAnnouncer);
        registry.AddPeer(thirdAnnouncer);
        FillTransactionBudget(registry, firstAnnouncer, secondAnnouncer, thirdAnnouncer);
        firstAnnouncer.TransactionRequests.Clear();
        secondAnnouncer.TransactionRequests.Clear();
        thirdAnnouncer.TransactionRequests.Clear();
        BodyRequestAttempt? firstAttempt = null;
        firstAnnouncer.TransactionRequestHandler = hash =>
        {
            firstAttempt = registry.CreateBodyRequestAttempt(firstAnnouncer, hash)!;
            firstAttempt.ConfirmSent();
            return true;
        };
        secondAnnouncer.TransactionRequestHandler = hash =>
        {
            registry.CreateBodyRequestAttempt(secondAnnouncer, hash)!.ConfirmSent();
            return true;
        };
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, BuildSparseBlobTransaction(out _, out _));
        firstAttempt!.Complete(unanswered: true);
        clock.Add(TimeSpan.FromSeconds(2));
        timers.Timer.Fire();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstAnnouncer.TransactionRequests, Has.Count.EqualTo(1));
            Assert.That(secondAnnouncer.TransactionRequests, Is.EqualTo(firstAnnouncer.TransactionRequests));
            Assert.That(thirdAnnouncer.TransactionRequests, Is.Empty);
        }
    }

    [Test]
    public void registry_should_retry_an_evicted_transaction_when_the_announcer_becomes_writable()
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            TimeSpan.Zero,
            timerFactory: timers,
            timestamper: clock);
        TestSparseBlobPeer announcer = new(TestItem.PublicKeys[FillingPeerCount + 1]);
        registry.AddPeer(announcer);
        FillTransactionBudget(registry, announcer);
        announcer.TransactionRequests.Clear();
        announcer.IsTransactionChannelWritable = false;
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, BuildSparseBlobTransaction(out _, out _));
        int requestsWhileUnwritable = announcer.TransactionRequests.Count;
        announcer.IsTransactionChannelWritable = true;
        clock.Add(TimeSpan.FromSeconds(6));
        timers.Timer.Fire();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requestsWhileUnwritable, Is.Zero);
            Assert.That(announcer.TransactionRequests, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void registry_should_resume_an_eviction_retry_after_the_last_peer_that_refused()
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance, LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer[] announcers = CreateSparseBlobPeers(registry, 4);
        announcers[0].TransactionRequestHandler = static _ => false;
        announcers[1].TransactionRequestHandler = static _ => false;
        announcers[2].TransactionRequestHandler = static _ => false;
        FillTransactionBudget(registry, announcers);
        foreach (TestSparseBlobPeer announcer in announcers) announcer.TransactionRequests.Clear();
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, BuildSparseBlobTransaction(out _, out _));
        int[] beforeMaintenance = announcers.Select(announcer => announcer.TransactionRequests.Count).ToArray();
        clock.Add(Timeouts.Eth + TimeSpan.FromSeconds(1));
        timers.Timer.Fire();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeMaintenance, Is.EqualTo(new[] { 1, 1, 1, 0 }));
            Assert.That(announcers.Select(announcer => announcer.TransactionRequests.Count), Is.EqualTo(new[] { 1, 1, 1, 1 }));
        }
    }

    [Test]
    public void registry_should_prefer_the_cell_supplier_after_a_successful_eviction_retry_times_out()
    {
        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance, LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] announcers = CreateSparseBlobPeers(registry, 4);
        TestSparseBlobPeer refusingPeer = announcers[0];
        TestSparseBlobPeer bodyPeer = announcers[1];
        TestSparseBlobPeer otherAnnouncer = announcers[2];
        TestSparseBlobPeer cellsPeer = announcers[3];
        FillTransactionBudget(registry, announcers);
        foreach (TestSparseBlobPeer announcer in announcers) announcer.TransactionRequests.Clear();

        refusingPeer.TransactionRequestHandler = static _ => false;
        BodyRequestAttempt? bodyAttempt = null;
        bodyPeer.TransactionRequestHandler = requestedHash =>
        {
            bodyAttempt = registry.CreateBodyRequestAttempt(bodyPeer, requestedHash)!;
            bodyAttempt.ConfirmSent();
            return true;
        };
        TestSparseBlobPeer newPeer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(newPeer);
        Transaction incoming = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);

        registry.RecordTransaction(newPeer, incoming);
        Hash256 hash = bodyPeer.TransactionRequests.Single();
        bool cellsRecorded = registry.RecordCells(cellsPeer, hash, mask, cells);
        bodyAttempt!.Complete(unanswered: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusingPeer.TransactionRequests, Is.EqualTo(new[] { hash }));
            Assert.That(cellsRecorded, Is.True);
            Assert.That(cellsPeer.TransactionRequests, Is.EqualTo(new[] { hash }));
            Assert.That(otherAnnouncer.TransactionRequests, Is.Empty);
        }
    }

    [Test]
    public void registry_should_skip_an_eviction_retry_when_the_body_arrives_during_peer_selection()
    {
        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance, LimboLogs.Instance, TimeSpan.Zero);
        TestSparseBlobPeer announcer = new(TestItem.PublicKeys[FillingPeerCount + 1]);
        registry.AddPeer(announcer);
        FillTransactionBudget(registry, announcer);
        announcer.TransactionRequests.Clear();
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)BuildSparseBlobTransaction(out _, out _).NetworkWrapper!;
        Transaction body = BuildFillingTransaction(0, wrapper);
        announcer.CanRequestTransactionsHandler = () =>
        {
            announcer.CanRequestTransactionsHandler = null;
            registry.RecordTransaction(announcer, body);
            return true;
        };
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, BuildSparseBlobTransaction(out _, out _));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcer.CanRequestTransactionsHandler is null, Is.True);
            Assert.That(announcer.TransactionRequests, Does.Not.Contain(body.Hash!));
        }
    }

    [Test]
    public void registry_should_request_an_evicted_transaction_from_the_next_announcer_when_the_first_refuses()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer firstAnnouncer = new(TestItem.PublicKeys[FillingPeerCount + 1]) { TransactionRequestHandler = static _ => false };
        TestSparseBlobPeer secondAnnouncer = new(TestItem.PublicKeys[FillingPeerCount + 2]);
        registry.AddPeer(firstAnnouncer);
        registry.AddPeer(secondAnnouncer);
        FillTransactionBudget(registry, firstAnnouncer, secondAnnouncer);
        firstAnnouncer.TransactionRequests.Clear();
        secondAnnouncer.TransactionRequests.Clear();
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, BuildSparseBlobTransaction(out _, out _));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstAnnouncer.TransactionRequests, Has.Count.EqualTo(1));
            Assert.That(secondAnnouncer.TransactionRequests, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void registry_should_keep_a_recorded_transaction_when_peers_fill_the_tracked_state_limit_with_announcements()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            TimeSpan.Zero,
            timerFactory: new ManualTimerFactory());
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[0]);
        registry.AddPeer(peer);
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask sampledMask, out byte[][] sampledCells);
        TestSparseBlobPeer[] announcingPeers = CreateSparseBlobPeers(registry, 9);
        Assert.That(registry.RecordAnnouncement(announcingPeers[0], tx.Hash!.ValueHash256, BlobCellMask.Full), Is.True);
        registry.RecordTransaction(peer, tx);
        for (int i = 0; i < announcingPeers.Length * 2048 - 1; i++)
        {
            registry.RecordAnnouncement(announcingPeers[(i + 1) / 2048], HashFromInt(i), BlobCellMask.Full);
        }

        bool recorded = registry.RecordCells(peer, tx.Hash!, sampledMask, sampledCells);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded, Is.True);
            _transactionPool.Received(1).SubmitTx(Arg.Is<Transaction>(t => t.Hash == tx.Hash), TxHandlingOptions.None);
            Assert.That(GetTrackedStateCount(registry), Is.LessThanOrEqualTo(16384));
        }
    }

    [Test]
    public void registry_should_make_room_again_when_another_reservation_takes_the_freed_bytes()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer announcer = new(TestItem.PublicKeys[FillingPeerCount + 1]);
        registry.AddPeer(announcer);
        FillTransactionBudget(registry, announcer);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeys[FillingPeerCount + 2]);
        registry.AddPeer(otherPeer);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)BuildSparseBlobTransaction(out _, out _).NetworkWrapper!;
        Transaction otherTransaction = BuildFillingTransaction(FillingPeerCount * FillingTransactionsPerPeer, wrapper);
        bool evicted = false;
        bool otherRecorded = false;
        announcer.TransactionRequestHandler = _ =>
        {
            evicted = true;
            return true;
        };
        TestSparseBlobPeer peer = new(TestItem.PublicKeys[FillingPeerCount]);
        peer.IsClosingHandler = () =>
        {
            if (evicted && !otherRecorded)
            {
                otherRecorded = true;
                registry.RecordTransaction(otherPeer, otherTransaction);
            }

            return false;
        };
        registry.AddPeer(peer);

        registry.RecordTransaction(peer, BuildSparseBlobTransaction(out _, out _));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherRecorded, Is.True);
            Assert.That(GetTrackedHashCount(registry, otherPeer.Id), Is.EqualTo(1));
            Assert.That(GetTrackedHashCount(registry, peer.Id), Is.EqualTo(1));
            Assert.That(GetTrackedTransactionBytes(registry), Is.LessThanOrEqualTo(FillingPeerCount * FillingPeerBytes));
        }
    }

    private const int FillingCellBlobsPerEntry = 8;
    private const int FillingCellEntriesPerPeer = 8;

    private static TestSparseBlobPeer[] FillEarlyCellsBudget(SparseBlobPoolPeerRegistry registry)
    {
        byte[][] cells = BuildFillingCells();
        TestSparseBlobPeer[] peers = new TestSparseBlobPeer[FillingPeerCount];
        for (int i = 0; i < FillingPeerCount; i++)
        {
            peers[i] = new TestSparseBlobPeer(TestItem.PublicKeys[i]);
            registry.AddPeer(peers[i]);
            for (int j = 0; j < FillingCellEntriesPerPeer; j++)
            {
                Assert.That(registry.RecordCells(peers[i], TestItem.Keccaks[i * FillingCellEntriesPerPeer + j], BlobCellMask.Full, cells), Is.True);
            }
        }

        Assert.That(GetEarlyCellsBytes(registry), Is.EqualTo(FillingPeerCount * FillingPeerBytes));
        return peers;
    }

    private static byte[][] BuildFillingCells()
    {
        byte[] cell = new byte[CkzgLib.Ckzg.BytesPerCell];
        byte[][] cells = new byte[FillingCellBlobsPerEntry * BlobCellMask.Full.Count][];
        Array.Fill(cells, cell);
        return cells;
    }

    private static int GetTrackedStateCount(SparseBlobPoolPeerRegistry registry) =>
        (int)typeof(SparseBlobPoolPeerRegistry)
            .GetField("_trackedTransactionCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(registry)!;

    private static long GetEarlyCellsBytes(SparseBlobPoolPeerRegistry registry) =>
        (long)typeof(SparseBlobPoolPeerRegistry)
            .GetField("_earlyCellsBytes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(registry)!;

    private const int FillingPeerCount = 4;
    private const int FillingTransactionsPerPeer = 16;
    private const int FillingTransactionBytes = 1024 * 1024;
    private const long FillingPeerBytes = (long)FillingTransactionsPerPeer * FillingTransactionBytes;

    private TestSparseBlobPeer[] FillTransactionBudget(SparseBlobPoolPeerRegistry registry, params TestSparseBlobPeer[] announcers)
    {
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)BuildSparseBlobTransaction(out _, out _).NetworkWrapper!;
        TestSparseBlobPeer[] peers = new TestSparseBlobPeer[FillingPeerCount];
        for (int i = 0; i < FillingPeerCount; i++)
        {
            peers[i] = new TestSparseBlobPeer(TestItem.PublicKeys[i]);
            registry.AddPeer(peers[i]);
            for (int j = 0; j < FillingTransactionsPerPeer; j++)
            {
                Transaction filler = BuildFillingTransaction(i * FillingTransactionsPerPeer + j, wrapper);
                foreach (TestSparseBlobPeer announcer in announcers)
                {
                    Assert.That(registry.RecordAnnouncement(announcer, filler.Hash!.ValueHash256, BlobCellMask.Full), Is.True);
                }

                registry.RecordTransaction(peers[i], filler);
            }
        }

        Assert.That(GetTrackedTransactionBytes(registry), Is.EqualTo(FillingPeerCount * FillingPeerBytes));
        return peers;
    }

    private static Transaction BuildFillingTransaction(int nonce, ShardBlobNetworkWrapper wrapper)
    {
        int dataLength = FillingTransactionBytes;
        Transaction tx = BuildFillingTransaction(nonce, dataLength, wrapper);
        for (int attempt = 0; attempt < 4 && tx.GetLength() != FillingTransactionBytes; attempt++)
        {
            dataLength += FillingTransactionBytes - tx.GetLength();
            tx = BuildFillingTransaction(nonce, dataLength, wrapper);
        }

        Assert.That(tx.GetLength(), Is.EqualTo(FillingTransactionBytes));
        return tx;
    }

    private static Transaction BuildFillingTransaction(int nonce, int dataLength, ShardBlobNetworkWrapper wrapper)
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Blob)
            .WithBlobVersionedHashes(1)
            .WithMaxFeePerBlobGas(1)
            .WithNonce((ulong)nonce)
            .WithData(new byte[dataLength])
            .SignedAndResolved()
            .TestObject;
        tx.NetworkWrapper = wrapper;
        return tx;
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

    private static long GetTrackedTransactionBytes(SparseBlobPoolPeerRegistry registry) =>
        (long)typeof(SparseBlobPoolPeerRegistry)
            .GetField("_trackedTransactionBytes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(registry)!;

    private static bool HasExpectedSparseCells(Transaction submittedTx, Hash256 hash, BlobCellMask cellMask, int cellsLength) =>
        submittedTx.Hash == hash
            && submittedTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && wrapper.CellMask == cellMask
            && wrapper.Cells is not null
            && wrapper.Cells.Length == cellsLength;

    private static bool IsElidedBlobResponse(
        PooledTransactionsMessage response,
        int fullTransactionLength,
        ProofVersion expectedVersion)
    {
        if (response.EthMessage.Transactions.Count != 1
            || response.EthMessage.Transactions[0] is not { NetworkWrapper: ShardBlobNetworkWrapper wrapper } transaction)
        {
            return false;
        }

        return wrapper.Version == expectedVersion
            && wrapper.Blobs.Length == 0
            && transaction.GetLength() < fullTransactionLength;
    }

    private static bool IsV0BlobTransaction(Transaction submittedTx, Hash256 hash) =>
        submittedTx.Hash == hash
            && submittedTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && wrapper.Version == ProofVersion.V0;

    public enum BlobAnnouncementSize
    {
        ElidedWire,
        GethEstimate,
        Consensus
    }

    private static int GetGethBlobTransactionSizeEstimate(Transaction transaction)
    {
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)transaction.NetworkWrapper!;
        int sidecarContentLength = Rlp.LengthOf(wrapper.Blobs)
            + Rlp.LengthOf(wrapper.Commitments)
            + Rlp.LengthOf(wrapper.SplitProofs());
        return transaction.GetLength(shouldCountBlobs: false) + Rlp.LengthOfSequence(sidecarContentLength);
    }

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

    private static Transaction BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells)
        => BuildSparseBlobTransaction(out cellMask, out cells, out _);

    private static Transaction BuildElidedBlobTransaction(Transaction tx)
    {
        Transaction clone = new();
        tx.CopyTo(clone, copyHash: true);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        clone.NetworkWrapper = wrapper with { Blobs = [], CellMask = default, Cells = null };
        clone.ClearLengthCache();
        return clone;
    }

    private static Transaction BuildBlobTransactionWithEmptySparseSidecar(Transaction tx)
    {
        Transaction clone = new();
        tx.CopyTo(clone, copyHash: true);
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
        byte[] proofs = [.. wrapper.Proofs];
        foreach (int cellIndex in cellMask.EnumerateSetBits())
        {
            proofs[cellIndex * ShardBlobNetworkWrapper.BytesPerProof] ^= 1;
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

    private sealed class TestEth72ProtocolHandler(
        ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ITxPool txPool,
        IGossipPolicy gossipPolicy,
        IForkInfo forkInfo,
        ILogManager logManager,
        ITxPoolConfig txPoolConfig,
        IChainHeadSpecProvider specProvider,
        IBlobCustodyTracker blobCustodyTracker,
        ISparseBlobPoolPeerRegistry sparseBlobPoolPeerRegistry,
        ITxGossipPolicy? transactionsGossipPolicy)
        : Eth72ProtocolHandler(
            session,
            serializer,
            nodeStatsManager,
            syncServer,
            backgroundTaskScheduler,
            txPool,
            gossipPolicy,
            forkInfo,
            logManager,
            txPoolConfig,
            specProvider,
            blobCustodyTracker,
            sparseBlobPoolPeerRegistry,
            transactionsGossipPolicy)
    {
        public void HandleSlowPublic(IOwnedReadOnlyList<Transaction> transactions, CancellationToken cancellationToken, int startIndex = 0)
        {
            PeerWorkQuota quota = new(16);
            Assert.That(quota.TryReserve(Session.Node.Id, PeerWorkQuota.WorkKind.Ordinary, null, out PeerWorkQuota.Reservation? lease), Is.True);
            using (lease) HandleSlow(new TransactionsRequest(transactions, startIndex) { WorkReservation = lease }, cancellationToken).GetAwaiter().GetResult();
        }
    }

    private sealed class CallbackBackgroundTaskScheduler(Func<bool> trySchedule) : IBackgroundTaskScheduler
    {
        public bool TryScheduleTask<TReq>(
            TReq request,
            Func<TReq, CancellationToken, Task> fulfillFunc,
            TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq> => trySchedule();
    }

    private sealed class PooledTransactionsOverrideSerializationService(IMessageSerializationService inner)
        : IMessageSerializationService
    {
        public PooledTransactionsMessage66? PooledTransactions { get; set; }

        public ArrayPoolSpan<byte> Serialize<T>(T message)
            where T : MessageBase => inner.Serialize(message);

        public void Serialize<T>(T message, IBufferWriter<byte> writer)
            where T : MessageBase => inner.Serialize(message, writer);

        public T Deserialize<T>(ArraySegment<byte> bytes)
            where T : MessageBase => GetOverride<T>() ?? inner.Deserialize<T>(bytes);

        public T Deserialize<T>(ZeroPacket packet)
            where T : MessageBase => GetOverride<T>() ?? inner.Deserialize<T>(packet);

        private T? GetOverride<T>() where T : MessageBase =>
            typeof(T) == typeof(PooledTransactionsMessage66) && PooledTransactions is not null
                ? (T)(MessageBase)PooledTransactions
                : null;
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
        public bool IsTransactionChannelWritable { get; set; } = true;
        public Func<bool>? CanRequestTransactionsHandler { get; set; }
        public bool CanRequestTransactions => !IsClosing && (CanRequestTransactionsHandler?.Invoke() ?? IsTransactionChannelWritable);
        public Func<bool>? IsClosingHandler { get; set; }
        public void RecordUsefulCells(ValueHash256 hash) { }
        public void RecordUsefulBody(ValueHash256 hash) { }
        public List<(Hash256 Hash, BlobCellMask CellMask)> CellRequests { get; } = [];
        public Func<Hash256, BlobCellMask, bool>? CellRequestHandler { get; set; }
        public Func<(IDisposable Lease, Action<BlobCellMask> Complete)>? ClaimCellsReservation { get; private set; }
        public List<Hash256> TransactionRequests { get; } = [];
        public Func<Hash256, bool>? TransactionRequestHandler { get; set; }
        public List<(DisconnectReason Reason, string Details)> Disconnects { get; } = [];
        public Action? Disconnecting { get; set; }

        public bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask, Func<(IDisposable Lease, Action<BlobCellMask> Complete)> claimReservation)
        {
            ClaimCellsReservation = claimReservation;
            return TrySendGetCells(hash, requestMask);
        }

        public bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask)
        {
            CellRequests.Add((hash, requestMask));
            return CellRequestHandler?.Invoke(hash, requestMask) ?? true;
        }

        public bool TrySendPooledTransactionRequest(Hash256 hash)
        {
            TransactionRequests.Add(hash);
            return TransactionRequestHandler?.Invoke(hash) ?? true;
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

    /// <summary>Requests cells and answers with an empty response, parking them with a restore owed.</summary>
    private void ParkEmptyCellsResponse(Hash256 hash, BlobCellMask cellMask)
    {
        Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(hash, cellMask), Is.True);
        using CellsMessage72 empty = new(GetLastGetCellsRequestId(hash, cellMask), [], [], BlobCellMask.Empty.ToBytes());
        HandleZeroMessage(empty, Eth72MessageCode.Cells);
    }

    /// <summary>Lets one cells request go unanswered, then runs maintenance past the retry backoff.</summary>
    private void MissCellsRequest(Hash256 hash, BlobCellMask cellMask, bool timedOut)
    {
        if (timedOut)
        {
            Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(hash, cellMask), Is.True);
            ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));
        }
        else
        {
            ParkEmptyCellsResponse(hash, cellMask);
        }

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));
    }

    /// <summary>The handler wraps a cells response in its private ClaimedCellsResponse before scheduling it.</summary>
    private const string ClaimedCellsResponseTypeName = "ClaimedCellsResponse";

    /// <summary>
    /// Runs every background task inline except those wrapping <paramref name="rejectedRequest"/>.
    /// </summary>
    /// <remarks>
    /// Matches on the wrapped request's own <see cref="Type.Name"/> rather than the scheduler's reported
    /// name, which qualifies on collision and so is not a stable key. <see cref="Rejected"/> lets a test
    /// assert the rejection actually happened instead of silently exercising the scheduled path.
    /// </remarks>
    private sealed class SourceRejectingBackgroundTaskScheduler(string rejectedRequest) : IBackgroundTaskScheduler
    {
        public int Rejected { get; private set; }

        public bool TryScheduleTask<TReq>(
            TReq request,
            Func<TReq, CancellationToken, Task> fulfillFunc,
            TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            foreach (Type wrapped in typeof(TReq).GenericTypeArguments)
            {
                if (wrapped.Name == rejectedRequest)
                {
                    Rejected++;
                    return false;
                }
            }

            return RunImmediatelyScheduler.Instance.TryScheduleTask(request, fulfillFunc, timeout);
        }
    }

    private sealed class RejectingBackgroundTaskScheduler : IBackgroundTaskScheduler
    {
        public bool TryScheduleTask<TReq>(
            TReq request,
            Func<TReq, CancellationToken, Task> fulfillFunc,
            TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            if (request is IDisposable disposable)
            {
                disposable.Dispose();
            }

            return false;
        }
    }

    private sealed class QueuedBackgroundTaskScheduler : IBackgroundTaskScheduler
    {
        private Func<CancellationToken, Task>? _next;

        public bool HasPendingTask => _next is not null;

        public bool TryScheduleTask<TReq>(
            TReq request,
            Func<TReq, CancellationToken, Task> fulfillFunc,
            TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            _next = cancellationToken => fulfillFunc(request, cancellationToken);
            return true;
        }

        public void RunNext(CancellationToken cancellationToken = default)
        {
            Func<CancellationToken, Task> next = Interlocked.Exchange(ref _next, null)
                ?? throw new InvalidOperationException("No background task is queued.");
            next(cancellationToken).GetAwaiter().GetResult();
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
    [Test]
    public async Task Only_solicited_full_transaction_acceptance_renews_source_backlog([Values] bool solicited)
    {
        HandleIncomingStatusMessage();
        ManualTimeProvider clock = new();
        await using IContainer container = CreateRequestedProgressPool(clock);
        _transactionPool = container.Resolve<ITxPool>();
        TxPool.TxPool pool = (TxPool.TxPool)_transactionPool;
        RecreateHandler(pooledRequestClock: clock);
        HandleIncomingStatusMessage();
        ValueHash256 backlog = TestItem.KeccakF.ValueHash256;
        pool.RetryCache.Announced(backlog, _handler);
        Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.True);
        clock.Advance(TimeSpan.FromSeconds(32));
        Transaction transaction = Build.A.Transaction.WithGasPrice(2.GWei).WithGasLimit(21000)
            .SignedAndResolved(new EthereumEcdsa(1), TestItem.PrivateKeyA).TestObject;
        pool.RetryCache.Announced(transaction.Hash!.ValueHash256, _handler);
        _handler.HandleMessage(PooledTransactionRequestMessage.New(transaction.Hash));
        if (solicited)
        {
            long id = GetLastGetPooledTransactionsRequestId(transaction.Hash);
            using PooledTransactionsMessage66 response = new(id,
                new PooledTransactionsMessage65(new[] { transaction }.ToPooledList()));
            HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
        }
        else
        {
            using TransactionsMessage broadcast = new(new[] { transaction }.ToPooledList());
            HandleZeroMessage(broadcast, Eth62MessageCode.Transactions);
        }
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.EqualTo(solicited));
        if (solicited)
        {
            clock.Advance(TimeSpan.FromSeconds(34));
            Assert.That(pool.RetryCache.TryDeferRequest(backlog, _handler), Is.False);
        }
    }

    [Test]
    public void Ignored_pooled_reply_releases_the_only_reply_slot()
    {
        RecreateHandler(backgroundTaskScheduler: new RecordingBackgroundTaskScheduler { QueueCapacity = 48 });
        HandleIncomingStatusMessage();
        _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
        long first = GetLastGetPooledTransactionsRequestId(TestItem.KeccakA);

        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(false);
        using PooledTransactionsMessage66 reply = new(first, new PooledTransactionsMessage65(IOwnedReadOnlyList<Transaction>.Empty));
        HandleZeroMessage(reply, Eth66MessageCode.PooledTransactions);

        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
        Assert.That(GetLastGetPooledTransactionsRequestId(TestItem.KeccakB), Is.Not.EqualTo(first));
    }

    [Test]
    public void Sending_after_expiry_releases_reply_slots_without_a_timer_or_response()
    {
        ManualTimeProvider clock = new();
        ManualTimestamper receiptClock = new();
        RecordingBackgroundTaskScheduler scheduler = new() { QueueCapacity = 8192 };
        RecreateHandler(backgroundTaskScheduler: scheduler, pooledRequestClock: clock, requestClock: receiptClock);
        HandleIncomingStatusMessage();
        for (int index = 0; index < 32; index++)
            _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA));
        Assert.That(_session.ReceivedCalls().Count(call => call.GetArguments().OfType<GetPooledTransactionsMessage>().Any()), Is.EqualTo(32));
        clock.Advance(Timeouts.Eth);
        receiptClock.Add(Timeouts.Eth);
        _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakB));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_session.ReceivedCalls().Count(call => call.GetArguments().OfType<GetPooledTransactionsMessage>().Any()), Is.EqualTo(33));
            _transactionPool.DidNotReceive().TryDeferTransactionRequest(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public void Body_request_reports_only_a_successful_send([Range(0, 1)] int refusal)
    {
        RecordingBackgroundTaskScheduler scheduler = new() { QueueCapacity = 8192 };
        RecreateHandler(backgroundTaskScheduler: scheduler);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = _handler;
        int accepted = refusal == 0 ? 32 : 1;
        for (int index = 0; index < accepted; index++)
        {
            Assert.That(peer.TrySendPooledTransactionRequest(new Hash256(index.ToString("X64"))), Is.True);
        }

        if (refusal == 1) _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(0);
        Hash256 refusedHash = new((accepted + 1).ToString("X64"));
        bool sent = peer.TrySendPooledTransactionRequest(refusedHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent, Is.False);
            _session.Received(accepted + (refusal == 1 ? 1 : 0)).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        _transactionPool.Received(1).TryDeferTransactionRequest(refusedHash.ValueHash256, _handler);
    }

    [Test]
    public void Registry_body_retry_advances_only_after_a_successful_handler_send([Values] bool sent)
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        RecordingBackgroundTaskScheduler scheduler = new() { QueueCapacity = 8192 };
        RecreateHandler(backgroundTaskScheduler: scheduler, sparseBlobPoolPeerRegistry: registry, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer handlerPeer = _handler;
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 3);
        foreach (TestSparseBlobPeer peer in peers) peer.CellRequestHandler = (_, _) => false;
        _session.DeliverMessage(Arg.Any<GetCellsMessage72>()).Returns(0);
        Transaction valid = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        Transaction bad = new();
        valid.CopyTo(bad, copyHash: true);
        CorruptBodyProof(bad);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.InvalidBlobProofs);
        foreach (ISparseBlobPoolPeer peer in new ISparseBlobPoolPeer[] { peers[0], peers[1], handlerPeer, peers[2] })
            Assert.That(registry.RecordAnnouncement(peer, valid.Hash!, mask), Is.True);
        if (!sent) _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peers[0], bad), Is.Null);
            Assert.That(registry.RecordCells(peers[1], valid.Hash!, mask, cells), Is.True);
        }
        _session.Received(1).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
        Assert.That(peers[2].TransactionRequests, Is.Empty);

        clock.Add(Timeouts.Eth);
        timers.Timer.Fire();
        using (Assert.EnterMultipleScope())
        {
            _session.Received(sent ? 1 : 2).DeliverMessage(Arg.Any<GetPooledTransactionsMessage>());
            Assert.That(peers[2].TransactionRequests, Has.Count.EqualTo(sent ? 1 : 0));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public async Task First_pooled_send_cannot_publish_a_timer_after_disposal([Values] bool closing)
    {
        HandleIncomingStatusMessage();
        ManualTimeProvider clock = new();
        RecordingBackgroundTaskScheduler scheduler = new() { QueueCapacity = 8192 };
        RecreateHandler(backgroundTaskScheduler: scheduler, pooledRequestClock: clock);
        HandleIncomingStatusMessage();
        await PooledTransactionRetryCache.VerifyFirstPooledSendCannotOutliveDisposal(_handler, _session, clock,
            () => _handler.HandleMessage(PooledTransactionRequestMessage.New(TestItem.KeccakA)), closing,
            freshClock =>
            {
                RecreateHandler(backgroundTaskScheduler: scheduler, pooledRequestClock: freshClock);
                HandleIncomingStatusMessage();
                return _handler;
            }, message => _handler.HandleMessage(message));
    }

    [Test]
    public void sparse_blob_caches_should_fit_per_peer_quotas()
    {
        // Persistent storage holds more transactions than one peer may announce, so the per-peer quota caps the cache.
        TxPoolConfig txPoolConfig = new() { BlobsSupport = BlobsSupportMode.Storage };
        using Eth72ProtocolHandler warmup = CreateProbeHandler(_session, _sparseBlobPoolPeerRegistry, txPoolConfig: txPoolConfig);
        MaterializeSparseBlobCaches(warmup);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            using Eth72ProtocolHandler handler = CreateProbeHandler(_session, _sparseBlobPoolPeerRegistry, txPoolConfig: txPoolConfig);
            MaterializeSparseBlobCaches(handler);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Progress.WriteLine($"Sparse cache allocation for 50 peers: {allocated} bytes.");
        Assert.That(allocated, Is.LessThan(50L * 1024 * 1024));
        (ClockCache<ValueHash256, BlobCellMask> announced, ClockCache<ValueHash256, DateTimeOffset> backoff) = GetSparseBlobCaches(warmup);
        for (int i = 0; i < 4096; i++)
        {
            ValueHash256 hash = HashFromInt(i).ValueHash256;
            announced.Set(hash, BlobCellMask.Full);
            backoff.Set(hash, DateTimeOffset.UtcNow);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announced.Count, Is.EqualTo(SparseBlobPoolPeerRegistry.MaxAnnouncementsPerPeer));
            Assert.That(backoff.Count, Is.EqualTo(1024));
        }
    }

    /// <summary>Allocates every per-peer sparse blob cache, including the fixed-size announced-shape cache.</summary>
    private static void MaterializeSparseBlobCaches(Eth72ProtocolHandler handler)
    {
        GetSparseBlobCaches(handler);
        typeof(Eth68ProtocolHandler)
            .GetMethod("SetTxShape", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(handler, [Keccak.Zero.ValueHash256, (1024, TxType.Blob)]);
    }

    private static (ClockCache<ValueHash256, BlobCellMask>, ClockCache<ValueHash256, DateTimeOffset>) GetSparseBlobCaches(Eth72ProtocolHandler handler)
    {
        ClockCache<ValueHash256, BlobCellMask> announced = ((Lazy<ClockCache<ValueHash256, BlobCellMask>>)typeof(Eth72ProtocolHandler)
            .GetField("_announcedBlobTransactionMasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!).Value;
        ClockCache<ValueHash256, DateTimeOffset> backoff = ((Lazy<ClockCache<ValueHash256, DateTimeOffset>>)typeof(Eth72ProtocolHandler)
            .GetField("_partialCellResponseBackoff", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!).Value;
        return (announced, backoff);
    }

    [Test]
    public void registry_should_admit_a_new_peer_when_global_tracking_is_full([Values(0, 1, 2)] int progress, [Values] bool sharedAnnouncement)
    {
        using SparseBlobPoolPeerRegistry registry = new(
            NullTxPool.Instance, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 9);
        for (int i = 0; i < 16384; i++)
        {
            Assert.That(registry.RecordAnnouncement(peers[i / 2048], HashFromInt(i), BlobCellMask.Full), Is.True);
        }

        Hash256 oldestHash = HashFromInt(0);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        if (sharedAnnouncement) Assert.That(registry.RecordAnnouncement(peers[8], oldestHash, BlobCellMask.Full), Is.True);
        if (progress == 1) Assert.That(registry.RecordCells(peers[0], oldestHash, cellMask, [new byte[CkzgLib.Ckzg.BytesPerCell]]), Is.True);
        if (progress == 2) Assert.That(registry.TryRequestCells(oldestHash, cellMask, peers[8].Id), Is.True);

        Hash256 newHash = HashFromInt(16384);
        Assert.That(registry.RecordAnnouncement(peers[8], newHash, BlobCellMask.Full), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetFullProviderAnnouncementCount(newHash), Is.EqualTo(1));
            Assert.That(registry.GetFullProviderAnnouncementCount(oldestHash), Is.EqualTo(progress == 0 ? 0 : sharedAnnouncement ? 2 : 1));
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(1)), Is.EqualTo(progress == 0 ? 1 : 0));
            Assert.That(Enumerable.Range(0, 16385).Count(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) != 0), Is.EqualTo(16384));
        }

        registry.OnCellsRequestCompleted(oldestHash, cellMask, peers[0]);
        registry.OnCellsRequestCompleted(oldestHash, cellMask, peers[0]);
        registry.Clear(oldestHash);
        registry.Clear(oldestHash);
        Assert.That(registry.RecordAnnouncement(peers[0], HashFromInt(16385), BlobCellMask.Full), Is.True);
    }

    [Test]
    public void registry_should_admit_honest_announcements_when_cell_buffers_fill_tracking([Values] bool honestOldest, [Values] bool coannounced)
    {
        using SparseBlobPoolPeerRegistry registry = new(
            NullTxPool.Instance, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, coannounced ? 10 : 9);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        byte[][] cells = [new byte[CkzgLib.Ckzg.BytesPerCell]];
        int sharedHonestIndex = honestOldest ? 0 : 16383;
        for (int i = 0; i < 16384; i++)
        {
            TestSparseBlobPeer peer = i == sharedHonestIndex || coannounced && (i is 2303 or 16382)
                ? peers[8] : i < 256 ? peers[7] : peers[(i - 256) / 2048];
            Hash256 hash = HashFromInt(i);
            Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
            if (i is < 300 or > 380) Assert.That(registry.RecordCells(peer, hash, cellMask, cells), Is.True);
        }
        if (coannounced)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(peers[0], HashFromInt(sharedHonestIndex), BlobCellMask.Full), Is.True);
                Assert.That(registry.RecordAnnouncement(peers[7], HashFromInt(2303), BlobCellMask.Full), Is.True);
                Assert.That(registry.RecordAnnouncement(peers[9], HashFromInt(16382), BlobCellMask.Full), Is.True);
            }
        }

        Hash256 honestHash = HashFromInt(16384);
        Assert.That(registry.RecordAnnouncement(peers[8], honestHash, BlobCellMask.Full), Is.True);
        if (coannounced) Assert.That(registry.RecordAnnouncement(peers[0], honestHash, BlobCellMask.Full), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(256)), Is.EqualTo(1));
            Assert.That(registry.GetFullProviderAnnouncementCount(honestHash), Is.EqualTo(coannounced ? 2 : 1));
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(sharedHonestIndex)), Is.EqualTo(coannounced ? 2 : 1));
            if (coannounced) Assert.That(new[] { sharedHonestIndex, 2303, 16382 }.All(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) == 2), Is.True);
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(300)), Is.Zero);
            Assert.That(registry.LastTrimStateCount, Is.EqualTo(513));
            Assert.That(Enumerable.Range(0, 16385).Count(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) != 0), Is.EqualTo(16384));
        }

        Assert.That(registry.RecordCells(peers[8], honestHash, cellMask, cells), Is.True);
        for (int i = 1; i <= 80; i++)
        {
            Hash256 nextHash = HashFromInt(16384 + i);
            Assert.That(registry.RecordAnnouncement(peers[8], nextHash, BlobCellMask.Full), Is.True);
            if (coannounced) Assert.That(registry.RecordAnnouncement(peers[0], nextHash, BlobCellMask.Full), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.LastTrimStateCount, Is.EqualTo(i == 1 ? 45 : 1));
                Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(300 + i)), Is.Zero);
            }
            Assert.That(registry.RecordCells(peers[8], nextHash, cellMask, cells), Is.True);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enumerable.Range(16384, 81).All(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) == (coannounced ? 2 : 1)), Is.True);
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(sharedHonestIndex)), Is.EqualTo(coannounced ? 2 : 1));
            if (coannounced) Assert.That(new[] { sharedHonestIndex, 2303, 16382 }.All(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) == 2), Is.True);
            Assert.That(Enumerable.Range(0, 16384).Where(i => i is < 300 or > 380)
                .All(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) == (coannounced && (i == sharedHonestIndex || i is 2303 or 16382) ? 2 : 1)), Is.True);
            Assert.That(Enumerable.Range(0, 16465).Count(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) != 0), Is.EqualTo(16384));
        }
    }

    [Test]
    // Eight refilling identities and 32 MiB of unverified cells can cause about 90 percent refusal per supernode attempt.
    public void registry_accepted_residual_refuses_supernode_announcements_under_eight_identity_refill()
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        _blobCustodyTracker.Update(BlobCellMask.Full);
        using SparseBlobPoolPeerRegistry registry = CreateBufferedTrackingPool(out TestSparseBlobPeer[] peers, timers, clock, ownOnly: true);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        ISparseBlobPoolPeerRegistry requests = registry;
        Hash256 incoming = HashFromInt(16384);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetRequestMask(HashFromInt(0), cellMask, 15), Is.EqualTo(cellMask));
            Assert.That(registry.RecordAnnouncement(peers[8], incoming, BlobCellMask.Full), Is.False);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetFullProviderAnnouncementCount(incoming), Is.Zero);
            Assert.That(Enumerable.Range(0, 16384).All(HasOriginalAnnouncement), Is.True);
            Assert.That(Enumerable.Range(0, 16384).Count(HasOriginalAnnouncement)
                + registry.GetFullProviderAnnouncementCount(incoming), Is.EqualTo(16384));
        }

        clock.Add(TimeSpan.FromSeconds(15));
        timers.Timer.Fire();
        Assert.That(registry.RecordAnnouncement(peers[8], incoming, BlobCellMask.Full), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetFullProviderAnnouncementCount(incoming), Is.EqualTo(1));
            Assert.That(HasOriginalAnnouncement(256), Is.False);
            Assert.That(Enumerable.Range(0, 16384).Where(i => i != 256).All(HasOriginalAnnouncement), Is.True);
            Assert.That(Enumerable.Range(0, 16384).Count(HasOriginalAnnouncement)
                + registry.GetFullProviderAnnouncementCount(incoming), Is.EqualTo(16384));
        }

        bool HasOriginalAnnouncement(int index) => requests.CoversRequest(peers[index / 2048], HashFromInt(index).ValueHash256, cellMask);
    }

    [Test]
    public void registry_should_prefer_sole_idle_announcements_when_trimming_overrepresented_peer()
    {
        using SparseBlobPoolPeerRegistry registry = CreateBufferedTrackingPool(out TestSparseBlobPeer[] peers, 256, 257);
        Assert.That(registry.RecordAnnouncement(peers[8], HashFromInt(256), BlobCellMask.Full), Is.True);

        Hash256 incoming = HashFromInt(16384);
        Assert.That(registry.RecordAnnouncement(peers[8], incoming, BlobCellMask.Full), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(256)), Is.EqualTo(2));
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(257)), Is.Zero);
            Assert.That(registry.GetFullProviderAnnouncementCount(incoming), Is.EqualTo(1));
            Assert.That(registry.LastTrimStateCount, Is.EqualTo(513));
        }
    }

    [Test, CancelAfter(10_000)]
    public void registry_should_recheck_selected_victim_progress_before_removal([Values] bool beginProgress, CancellationToken cancellationToken)
    {
        using SparseBlobPoolPeerRegistry registry = CreateBufferedTrackingPool(out TestSparseBlobPeer[] peers, 256);
        Hash256 selected = HashFromInt(256);
        Hash256 incoming = HashFromInt(16384);
        Lock laterCandidateLock = GetTrackedStateLock(registry, HashFromInt(257));
        TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new(() =>
        {
            try
            {
                completed.SetResult(registry.RecordAnnouncement(peers[8], incoming, BlobCellMask.Full));
            }
            catch (Exception exception)
            {
                completed.SetException(exception);
            }
        })
        { IsBackground = true };
        bool workerStarted = false;
        try
        {
            using (laterCandidateLock.EnterScope())
            {
                worker.Start();
                workerStarted = true;
                SpinWait spinner = new();
                while ((worker.ThreadState & ThreadState.WaitSleepJoin) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Assert.That(completed.Task.IsCompleted, Is.False);
                    spinner.SpinOnce();
                }

                if (beginProgress)
                {
                    Transaction transaction = BuildBlobTransactionWithEmptySparseSidecar(BuildBlobTransaction(fullProvider: true));
                    transaction.Hash = selected;
                    _transactionPool.ValidateTxForBlobSampling(transaction).Returns(AcceptTxResult.Accepted);
                    Assert.That(registry.RecordTransaction(peers[0], transaction), Is.Null);
                }
            }
        }
        finally
        {
            if (workerStarted) Assert.That(worker.Join(TimeSpan.FromSeconds(5)), Is.True);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed.Task.Result, Is.EqualTo(!beginProgress));
            Assert.That(registry.GetFullProviderAnnouncementCount(selected), Is.EqualTo(beginProgress ? 1 : 0));
            Assert.That(registry.GetFullProviderAnnouncementCount(incoming), Is.EqualTo(beginProgress ? 0 : 1));
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(257)), Is.EqualTo(1));
            Assert.That(Enumerable.Range(0, 16385).Count(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) != 0), Is.EqualTo(16384));
        }
    }

    private SparseBlobPoolPeerRegistry CreateBufferedTrackingPool(out TestSparseBlobPeer[] peers, params int[] idleIndices) =>
        CreateBufferedTrackingPool(out peers, new ManualTimerFactory(), null, ownOnly: false, idleIndices);

    private SparseBlobPoolPeerRegistry CreateBufferedTrackingPool(out TestSparseBlobPeer[] peers, ManualTimerFactory timers,
        ManualTimestamper? clock, bool ownOnly, params int[] idleIndices)
    {
        SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        peers = CreateSparseBlobPeers(registry, 9);
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);
        byte[][] cells = [new byte[CkzgLib.Ckzg.BytesPerCell]];
        for (int i = 0; i < 16384; i++)
        {
            TestSparseBlobPeer peer = ownOnly ? peers[i / 2048]
                : i < 256 || i is >= 2304 and < 14592 && (i - 256) % 2048 == 0
                    ? peers[8] : peers[(i - 256) / 2048];
            Hash256 hash = HashFromInt(i);
            Assert.That(registry.RecordAnnouncement(peer, hash, ownOnly ? cellMask : BlobCellMask.Full), Is.True);
            if (!idleIndices.Contains(i)) Assert.That(registry.RecordCells(peer, hash, cellMask, cells), Is.True);
        }
        return registry;
    }

    private static Lock GetTrackedStateLock(SparseBlobPoolPeerRegistry registry, Hash256 hash)
    {
        object state = GetTrackedState(registry, hash);
        return (Lock)state.GetType().GetProperty("Lock")!.GetValue(state)!;
    }

    private static object GetTrackedState(SparseBlobPoolPeerRegistry registry, Hash256 hash)
    {
        IDictionary states = (IDictionary)typeof(SparseBlobPoolPeerRegistry)
            .GetField("_transactions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        return states[hash.ValueHash256]!;
    }

    [Test]
    public void registry_should_keep_tracking_bounded_when_every_transaction_has_a_body()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 9);
        Transaction template = BuildBlobTransactionWithEmptySparseSidecar(BuildBlobTransaction(fullProvider: true));
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        for (int i = 0; i <= 16384; i++)
        {
            Transaction transaction = new();
            template.CopyTo(transaction, copyHash: true);
            transaction.Hash = HashFromInt(i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordTransaction(peers[i / 2048], transaction), Is.Null);
                Assert.That(registry.RecordAnnouncement(peers[i / 2048], transaction.Hash!, BlobCellMask.Full), Is.EqualTo(i < 16384));
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(0)), Is.EqualTo(1));
            Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(16384)), Is.Zero);
            Assert.That(Enumerable.Range(0, 16385).Count(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) != 0), Is.EqualTo(16384));
        }
    }
    [Test]
    public void registry_should_preserve_active_submission_during_announcement_churn()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 9);
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        _transactionPool.ValidateTxForBlobSampling(transaction).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(transaction, TxHandlingOptions.None).Returns(_ =>
        {
            for (int i = 0; i < 16384; i++)
            {
                Assert.That(registry.RecordAnnouncement(peers[i / 2048], HashFromInt(i), BlobCellMask.Full), Is.True);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.GetFullProviderAnnouncementCount(transaction.Hash!), Is.EqualTo(1));
                Assert.That(registry.GetFullProviderAnnouncementCount(HashFromInt(0)), Is.Zero);
                Assert.That(Enumerable.Range(0, 16384).Count(i => registry.GetFullProviderAnnouncementCount(HashFromInt(i)) != 0), Is.EqualTo(16383));
            }
            return AcceptTxResult.Accepted;
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peers[8], transaction.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(registry.RecordCells(peers[8], transaction.Hash!, cellMask, cells), Is.True);
            Assert.That(registry.RecordTransaction(peers[8], transaction), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(registry.GetFullProviderAnnouncementCount(transaction.Hash!), Is.EqualTo(1));
        }
        _transactionPool.Received(1).SubmitTx(transaction, TxHandlingOptions.None);
    }

    private Eth72ProtocolHandler CreateProbeHandler(ISession session, SparseBlobPoolPeerRegistry registry, ITxPool? txPool = null, ITxPoolConfig? txPoolConfig = null) =>
        new(session, _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance), _syncManager,
            RunImmediatelyScheduler.Instance, txPool ?? NullTxPool.Instance, _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager), LimboLogs.Instance,
            txPoolConfig ?? new TxPoolConfig { BlobsSupport = BlobsSupportMode.InMemory }, _specProvider,
            _blobCustodyTracker, registry, _txGossipPolicy);

    [Test]
    public void should_reannounce_blob_tx_when_available_cell_mask_expands([Values] bool evictPreviousAnnouncement)
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

        if (evictPreviousAnnouncement)
        {
            ClockCache<ValueHash256, BlobCellMask> announced = GetSparseBlobCaches(_handler).Item1;
            for (int i = 0; i < SparseBlobPoolPeerRegistry.MaxAnnouncementsPerPeer; i++)
            {
                announced.Set(HashFromInt(i).ValueHash256, BlobCellMask.Full);
            }
        }
        _handler.SendNewTransaction(tx);

        tx.NetworkWrapper = wrapper with
        {
            Blobs = [],
            CellMask = expandedMask,
            Cells = expandedCells,
        };
        tx.ClearLengthCache();
        _handler.SendNewTransaction(tx);

        _session.Received(evictPreviousAnnouncement ? 2 : 1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Count == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(firstMask.ToBytes())));
        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage72>(m =>
            m.Hashes.Count == 1 &&
            m.Hashes[0] == tx.Hash &&
            m.CellMask.SequenceEqual(expandedMask.ToBytes())));
    }

    [Test, NonParallelizable]
    public async Task cancelled_processing_should_return_only_unsubmitted_transactions([Values] bool rescheduleSucceeds)
    {
        V62.Eth62ProtocolHandlerTests.WitnessingTransactionsMessageSerializer serializer = new();
        _svc = Build.A.SerializationService().WithEth72(_specProvider).With(serializer).TestObject;
        RecordingBackgroundTaskScheduler scheduler = new() { Defer = true };
        RecreateHandler(backgroundTaskScheduler: scheduler, serializer: _svc);
        HandleIncomingStatusMessage();
        await V62.Eth62ProtocolHandlerTests.AssertCancellationOwnership(_handler, _session, _transactionPool,
            _svc, serializer, scheduler, rescheduleSucceeds);
    }

    [Test, NonParallelizable]
    public void should_disconnect_if_pooled_blob_tx_shape_differs_from_eth72_announcement([Values] bool wrongSize)
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
            announcedSize += 9;
        }
        else
        {
            announcedType = TxType.EIP1559;
        }

        AnnounceBlobTransaction(tx.Hash!, announcedSize, announcedType);
        long requestId = GetLastGetPooledTransactionsRequestId(tx.Hash!);

        using PooledTransactionsMessage66 response = new(requestId, new PooledTransactionsMessage65(new[] { elidedTx }.ToPooledList()));
        Transaction reusable = TxDecoder.TxObjectPool.Get();
        TxDecoder.TxObjectPool.Return(reusable);
        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reusable.Signature, Is.Null);
            Assert.That(reusable.NetworkWrapper, Is.Null);
            Assert.That(reusable.BlobVersionedHashes, Is.Null);
        }

        _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
    }

    [Test]
    public void should_serve_an_honest_peer_under_sustained_cell_requests([Values(2, 4)] int busyPeerCount)
    {
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        _handler.Dispose();
        Eth72ProtocolHandler[] peers = Enumerable.Range(0, busyPeerCount + 1)
            .Select(i => CreateCellServingPeer(registry, clock, i)).ToArray();
        Hash256 hash = HashFromInt(1000);
        Transaction transaction = new() { Type = TxType.Blob, Hash = hash, BlobVersionedHashes = [new byte[Hash256.Size]] };
        byte[][] cells = Enumerable.Repeat(new byte[CkzgLib.Ckzg.BytesPerCell], BlobCellMask.CellCount).ToArray();
        SetupGetCellsResponse(transaction, BlobCellMask.Full, BlobCellMask.Full, cells);
        long requestId = 0;
        int? servedAt = null;
        for (int second = 0; second <= 8; second++)
        {
            if (second != 0) clock.Add(TimeSpan.FromSeconds(1));
            for (int peerIndex = 0; peerIndex < busyPeerCount; peerIndex++)
            {
                for (int i = 0; i < 50; i++) RequestCells(peers[peerIndex], hash, ++requestId);
            }
            if (RequestCells(peers[^1], hash, ++requestId)) servedAt ??= second;
        }

        Assert.That(servedAt, Is.Not.Null, "128-cell requests must leave capacity for the honest peer within eight one-second refill intervals.");
        if (busyPeerCount == 2) Assert.That(servedAt, Is.Zero);
    }

    [Test]
    public void full_mask_cell_serving_refills_after_eight_or_sixteen_seconds([Values] bool materializeCells)
    {
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        _handler.Dispose();
        Eth72ProtocolHandler peer = CreateCellServingPeer(registry, clock, 0);
        Hash256 hash = HashFromInt(1000);
        Transaction transaction = new()
        {
            Type = TxType.Blob,
            Hash = hash,
            BlobVersionedHashes = Enumerable.Range(0, 6).Select(_ => new byte[Hash256.Size]).ToArray()
        };
        byte[][] cells = Enumerable.Repeat(new byte[CkzgLib.Ckzg.BytesPerCell], 768).ToArray();
        SetupGetCellsResponse(transaction, BlobCellMask.Full, BlobCellMask.Full, cells, materializeCells ? 768 : 0);
        Assert.That(RequestCells(peer, hash, 1), Is.True);
        if (!materializeCells) Assert.That(RequestCells(peer, hash, 2), Is.True);
        Assert.That(RequestCells(peer, hash, 3), Is.False);

        clock.Add(TimeSpan.FromSeconds(materializeCells ? 15 : 7));
        Assert.That(RequestCells(peer, hash, 4), Is.False);
        clock.Add(TimeSpan.FromSeconds(1));
        Assert.That(RequestCells(peer, hash, 5), Is.True);
    }

    [Test]
    public void new_cell_serving_peers_should_not_replenish_the_global_budget()
    {
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        _handler.Dispose();
        Hash256 hash = HashFromInt(1000);
        Transaction transaction = new() { Type = TxType.Blob, Hash = hash, BlobVersionedHashes = [new byte[Hash256.Size]] };
        byte[][] cells = Enumerable.Repeat(new byte[CkzgLib.Ckzg.BytesPerCell], BlobCellMask.CellCount).ToArray();
        SetupGetCellsResponse(transaction, BlobCellMask.Full, BlobCellMask.Full, cells);
        int served = 0;
        for (int i = 0; i < 64; i++)
        {
            using Eth72ProtocolHandler peer = CreateCellServingPeer(registry, clock, i);
            if (RequestCells(peer, hash, i)) served++;
        }

        Assert.That(served * BlobCellMask.CellCount, Is.EqualTo(4096));
    }

    private Eth72ProtocolHandler CreateCellServingPeer(SparseBlobPoolPeerRegistry registry, ManualTimestamper clock, int peerIndex, ITxPool? txPool = null)
    {
        ISession session = Substitute.For<ISession>();
        session.InboundReceipts.Returns(new PacketReceiptTracker());
        session.IsChannelWritable.Returns(true);
        session.AddTo(_disposables);
        byte[] key = new byte[PublicKey.LengthInBytes];
        BinaryPrimitives.WriteInt32LittleEndian(key, peerIndex + 1);
        session.Node.Returns(new Node(new PublicKey(key), new IPEndPoint(IPAddress.Loopback, 30303)));
        session.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(call =>
        {
            P2PMessage message = call.Arg<P2PMessage>();
            _deliveredMessages.Add(message);
            message.AddTo(_disposables);
        });
        session.DeliverMessage(Arg.Any<GetCellsMessage72>()).Returns(1);
        Eth72ProtocolHandler handler = CreateProbeHandler(session, registry, txPool ?? _transactionPool);
        SetHandlerTimestamper(handler, clock);
        handler.AddTo(_disposables);
        _handler = handler;
        handler.Init();
        HandleIncomingStatusMessage();
        return handler;
    }

    private bool RequestCells(Eth72ProtocolHandler peer, Hash256 hash, long requestId)
    {
        _handler = peer;
        using GetCellsMessage72 request = new(requestId, [hash], BlobCellMask.Full.ToBytes());
        HandleZeroMessage(request, Eth72MessageCode.GetCells);
        return _deliveredMessages.OfType<CellsMessage72>().Last().Hashes.Length == 1;
    }
    [Test]
    public void should_cap_cells_response_at_soft_byte_limit([Values] bool materializeCells)
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
        SetupGetCellsResponse(firstTx, requestedMask, requestedMask, cells, materializeCells ? cells.Length : 0);
        SetupGetCellsResponse(secondTx, requestedMask, requestedMask, cells, materializeCells ? cells.Length : 0);
        using GetCellsMessage72 request = new(1234, [firstHash, secondHash], requestedMask.ToBytes());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth72MessageCode.GetCells);

        _session.Received(1).DeliverMessage(Arg.Is<CellsMessage72>(m =>
            m.RequestId == request.RequestId &&
            m.Hashes.SequenceEqual(new ValueHash256[] { firstHash }) &&
            m.Cells.Length == 1));
        _transactionPool.Received(1).TryGetBlobCells(
            firstHash,
            requestedMask,
            out Arg.Any<BlobCellMask>(),
            out Arg.Any<byte[][]>());
        _transactionPool.DidNotReceive().TryGetBlobCells(
            secondHash,
            requestedMask,
            out Arg.Any<BlobCellMask>(),
            out Arg.Any<byte[][]>());
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public void zero_hash_response_removes_announcements_and_immediately_retries_another_peer()
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        RecreateHandler(cellQualityClock: quality, requestClock: clock);
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherPeer.CellRequests[0], Is.EqualTo((tx.Hash!, requestedMask)));
            Assert.That(_sparseBlobPoolPeerRegistry.CoversRequest((ISparseBlobPoolPeer)_handler, tx.Hash!.ValueHash256, requestedMask), Is.False);
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((0, 0)));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        clock.Add(TimeSpan.FromSeconds(6));
        quality.Advance(TimeSpan.FromSeconds(6));
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherPeer.CellRequests, Has.Count.EqualTo(1));
            Assert.That(_sparseBlobPoolPeerRegistry.CoversRequest((ISparseBlobPoolPeer)_handler, tx.Hash!.ValueHash256, requestedMask), Is.False);
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((0, 0)));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public void should_accept_cells_within_eth_response_timeout([Values(0, 4)] int silentPeerCount)
    {
        for (int peerIndex = 0; peerIndex < silentPeerCount; peerIndex++)
        {
            TestSparseBlobPeer silentPeer = new(TestItem.PublicKeys[20 + peerIndex]);
            _sparseBlobPoolPeerRegistry.AddPeer(silentPeer);
            for (int i = 0; i < 24; i++)
            {
                Hash256 hash = HashFromInt(peerIndex * 100 + i);
                _sparseBlobPoolPeerRegistry.RecordAnnouncement(silentPeer, hash, BlobCellMask.Full);
                _sparseBlobPoolPeerRegistry.TryRequestCells(hash, BlobCellMask.Full, silentPeer.Id);
            }
        }

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
        _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
    }

    [Test]
    public void stale_reply_does_not_claim_replacement_reservation()
    {
        ContainerBuilder builder = new();
        builder.RegisterModule(new NetworkModule(new ConfigProvider(new NetworkConfig())));
        builder.RegisterInstance(_transactionPool);
        builder.RegisterInstance(_blobCustodyTracker).As<IBlobCustodyTracker>();
        builder.RegisterInstance(RunImmediatelyScheduler.Instance).As<IBackgroundTaskScheduler>();
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterDecorator<ISparseBlobPoolPeerRegistry>((_, _, registry) => new SparseRegistryDecorator(registry, forwardCredit: true));
        using IContainer container = builder.Build();
        SparseRegistryDecorator decorated = (SparseRegistryDecorator)container.Resolve<ISparseBlobPoolPeerRegistry>();
        RecreateHandler(sparseBlobPoolPeerRegistry: decorated);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        Hash256 hash = HashFromInt(9200);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.RecordAnnouncement(peer, hash, mask), Is.True);
            Assert.That(decorated.TryRequestCells(hash, mask, peer.Id), Is.True);
        }
        long staleId = GetLastGetCellsRequestId(hash, mask);
        SparsePeerDecorator source = (SparsePeerDecorator)decorated.Resolve(peer);
        Assert.That(peer.TrySendGetCells(hash, mask, source.ClaimCellsReservation!), Is.True);
        long activeId = GetLastGetCellsRequestId(hash, mask);
        Assert.That(activeId, Is.Not.EqualTo(staleId));
        using CellsMessage72 stale = new(staleId, [], [], mask.ToBytes());
        HandleZeroMessage(stale, Eth72MessageCode.Cells);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.TryRequestCells(hash, mask, peer.Id), Is.True);
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(m => m.Hashes[0] == hash), Is.EqualTo(2));
        }
        using CellsMessage72 current = new(activeId, [], [], mask.ToBytes());
        Assert.That(() => HandleZeroMessage(current, Eth72MessageCode.Cells), Throws.Nothing);
    }

    [Test]
    public void default_receipt_session_cannot_send_cells()
    {
        _session.InboundReceipts.Returns(PacketReceiptTracker.Closed);
        RecreateHandler();
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.CanRequestCells, Is.False);
            Assert.That(peer.TrySendGetCells(HashFromInt(9300), BlobCellMask.Full), Is.False);
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>(), Is.Empty);
        }
    }

    [Test]
    public void registered_cell_session_preserves_inbound_receipt_time()
    {
        ManualTimestamper clock = new();
        RecreateHandler(requestClock: clock);
        PacketReceiptTracker receipts = _session.InboundReceipts;
        NettyBufferPacket packet = new(Unpooled.Buffer(0));
        try
        {
            receipts.Stamp(packet);
            DateTimeOffset arrival = clock.UtcNowOffset;
            clock.Add(TimeSpan.FromSeconds(11));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(packet.ReceiptSequence, Is.EqualTo(1));
                Assert.That(packet.ReceivedAt, Is.EqualTo(arrival));
                Assert.That(receipts.IsActive, Is.True);
            }
        }
        finally
        {
            packet.Release();
        }
    }

    [Test]
    public void should_require_new_announcement_after_timeout_and_ignore_stale_response_while_new_request_is_active()
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
        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(m => m.Hashes.Length == 1 && m.Hashes[0] == tx.Hash), Is.EqualTo(1));

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6));
        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(m => m.Hashes.Length == 1 && m.Hashes[0] == tx.Hash), Is.EqualTo(1));
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
    public void registry_should_reject_wrong_blob_count_against_a_pooled_transaction()
    {
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        _transactionPool.TryGetPendingBlobTransaction(transaction.Hash!, out Arg.Any<Transaction>())
            .Returns(call =>
            {
                call[1] = transaction;
                return true;
            });
        _transactionPool.MergeBlobCells(transaction.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peers[0], transaction.Hash!, cellMask, [.. cells, .. cells]), Is.False);
            Assert.That(peers[0].Disconnects, Has.Count.EqualTo(1));
        }
        _transactionPool.DidNotReceive().MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peers[1], transaction.Hash!, cellMask, cells), Is.True);
            Assert.That(registry.TryApplyRecordedCells(transaction.Hash!), Is.True);
        }
    }
    [Test]
    public void registry_should_exclude_ambiguous_cell_source_for_the_same_transaction()
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.True);
            Assert.That(registry.TryApplyRecordedCells(tx.Hash!), Is.False);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, cells), Is.False);
            Assert.That(registry.RecordAnnouncement(peer, tx.Hash!, cellMask), Is.False);
        }

        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
        _transactionPool.DidNotReceive().ForgetRejectedBlobTransaction(tx.Hash!);
    }

    [Test]
    public void registry_should_keep_honest_providers_eligible_after_three_ambiguous_cell_failures()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        TestSparseBlobPeer[] peers =
        [
            new(TestItem.PublicKeyA),
            new(TestItem.PublicKeyC),
            new(TestItem.PublicKeyD)
        ];
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>())
            .Returns(BlobCellMergeResult.InvalidCells);

        for (int i = 0; i < peers.Length; i++)
        {
            registry.AddPeer(peers[i]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordCells(peers[i], tx.Hash!, cellMask, cells), Is.True);
                Assert.That(registry.TryApplyRecordedCells(tx.Hash!), Is.False);
            }
        }

        TestSparseBlobPeer latePeer = new(TestItem.PublicKeyB);
        registry.AddPeer(latePeer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(latePeer, tx.Hash!, cellMask), Is.True);
            Assert.That(registry.RecordCells(latePeer, tx.Hash!, cellMask, cells), Is.True);
        }
        _transactionPool.DidNotReceive().RemoveTransaction(tx.Hash!);
        _transactionPool.DidNotReceive().ForgetRejectedBlobTransaction(tx.Hash!);
    }

    [Test]
    public void registry_should_keep_submitted_transaction_available_after_ambiguous_cell_failures()
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ITimestamper timestamper = Substitute.For<ITimestamper>();
        timestamper.UtcNowOffset.Returns(_ => now);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero,
            timestamper: timestamper);
        TestSparseBlobPeer submittingPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer[] invalidPeers =
        [
            new(TestItem.PublicKeyA),
            submittingPeer,
            new(TestItem.PublicKeyD)
        ];
        TestSparseBlobPeer retryPeer = new(TestItem.PublicKeyB);
        _transactionPool.ValidateTxForBlobSampling(tx).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        _transactionPool.TryGetPendingBlobCellMask(tx.Hash!, out Arg.Any<BlobCellMask>())
            .Returns(call =>
            {
                call[1] = cellMask;
                return true;
            });

        registry.AddPeer(submittingPeer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(submittingPeer, tx), Is.Null);
            Assert.That(registry.RecordCells(submittingPeer, tx.Hash!, cellMask, cells), Is.True);
        }
        _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>())
            .Returns(BlobCellMergeResult.InvalidCells);

        for (int i = 0; i < invalidPeers.Length; i++)
        {
            registry.AddPeer(invalidPeers[i]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordCells(invalidPeers[i], tx.Hash!, cellMask, cells), Is.True);
                Assert.That(registry.TryApplyRecordedCells(tx.Hash!), Is.False);
            }
        }

        registry.AddPeer(retryPeer);
        Assert.That(registry.RecordAnnouncement(retryPeer, tx.Hash!, cellMask), Is.True);

        now += TimeSpan.FromMinutes(5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(retryPeer, tx.Hash!, cellMask), Is.True);
            Assert.That(registry.RecordCells(retryPeer, tx.Hash!, cellMask, cells), Is.True);
        }
    }

    [Test]
    public async Task should_requeue_claimed_cells_after_local_refusal_and_complete_from_the_original_announcer([Values] bool quotaFull)
    {
        RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
        ManualTimestamper clock = new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SparseBlobPoolPeerRegistry registry = new(_transactionPool, _blobCustodyTracker,
            RunImmediatelyScheduler.Instance, LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        registry.AddTo(_disposables);
        RecreateHandler(backgroundTaskScheduler: scheduler, sparseBlobPoolPeerRegistry: registry);
        typeof(SyncPeerProtocolHandlerBase).GetField("_timestamper", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_handler, clock);
        try
        {
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
            if (quotaFull)
            {
                for (int index = 0; index < 32; index++)
                {
                    Hash256 hash = HashFromInt(index + 1000);
                    Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(hash, cellMask), Is.True);
                    using CellsMessage72 queued = new(GetLastGetCellsRequestId(hash, cellMask), [], [], BlobCellMask.Empty.ToBytes());
                    HandleZeroMessage(queued, Eth72MessageCode.Cells);
                }
                Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(32));
            }
            else scheduler.Reject = true;
            HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
            long firstRequestId = GetLastGetCellsRequestId(tx.Hash!, cellMask);

            using CellsMessage72 response = new(firstRequestId, [tx.Hash!], [cells], cellMask.ToBytes());
            Assert.That(() => HandleZeroMessage(response, Eth72MessageCode.Cells), Throws.Nothing);
            Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(quotaFull ? 32 : 0));

            GetCellsMessage72[] requests = _deliveredMessages
                .OfType<GetCellsMessage72>()
                .Where(m => m.Hashes.Length == 1 && m.Hashes[0] == tx.Hash)
                .ToArray();
            Assert.That(requests, Has.Length.EqualTo(1));
            scheduler.Reject = false;
            await scheduler.Drain(CancellationToken.None);
            clock.Add(TimeSpan.FromSeconds(6));
            ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(clock.UtcNowOffset);
            requests = _deliveredMessages
                .OfType<GetCellsMessage72>()
                .Where(m => m.Hashes.Length == 1 && m.Hashes[0] == tx.Hash)
                .ToArray();
            Assert.That(requests, Has.Length.EqualTo(2));
            Assert.That(requests[1].RequestId, Is.Not.EqualTo(firstRequestId));
            _transactionPool.DidNotReceive().MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
            _transactionPool.TryGetPendingBlobTransaction(tx.Hash!, out Arg.Any<Transaction>()).Returns(call =>
            {
                call[1] = tx;
                return true;
            });
            _transactionPool.MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>()).Returns(BlobCellMergeResult.Accepted);
            using CellsMessage72 retryResponse = new(requests[1].RequestId, [tx.Hash!], [cells], cellMask.ToBytes());
            HandleZeroMessage(retryResponse, Eth72MessageCode.Cells);
            await scheduler.Drain(CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                _transactionPool.Received(1).MergeBlobCells(tx.Hash!, cellMask, Arg.Any<byte[][]>());
                _transactionPool.Received(1).NotifyAboutTx(tx.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
                _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
        }
        finally
        {
            await scheduler.Drain(new CancellationToken(true));
        }
    }

    [TestCase(false, TestName = "should_restore_announced_mask_after_empty_cells_response")]
    [TestCase(true, TestName = "should_not_restore_announced_mask_after_cell_request_times_out")]
    public void should_only_restore_announced_mask_after_an_explicit_response(bool timeOutRequest)
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        registry.TryRequestCells(Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>(), Arg.Any<PublicKey>()).Returns(true);
        ConfigureAnnouncementRestoration(registry);
        registry.TryRestoreAnnouncement(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<Hash256>(), Arg.Any<AnnouncementRestoration>()).Returns(true);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;
        BlobCellMask cellMask = BlobCellMask.FromIndices([4]);

        HandleIncomingStatusMessage();
        Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(tx.Hash!, cellMask), Is.True);
        if (timeOutRequest)
        {
            // Past the 10s request TTL, so the unanswered request is requeued.
            ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));
        }
        else
        {
            using CellsMessage72 empty = new(GetLastGetCellsRequestId(tx.Hash!, cellMask), [], [], BlobCellMask.Empty.ToBytes());
            HandleZeroMessage(empty, Eth72MessageCode.Cells);
        }

        if (timeOutRequest) registry.Received(1).RemoveAnnouncementPermanently(_handler, tx.Hash!);
        else registry.Received(1).TryBeginAnnouncementRestore(_handler, tx.Hash!, out Arg.Any<AnnouncementRestoration?>());

        registry.ClearReceivedCalls();
        // Past the 5s backoff but inside the 15s pending-request TTL.
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));

        using (Assert.EnterMultipleScope())
        {
            registry.Received(timeOutRequest ? 0 : 1).TryRestoreAnnouncement(_handler, tx.Hash!, Arg.Is<AnnouncementRestoration>(ticket => ticket.Mask.IsFull));
            registry.DidNotReceive().TryRestoreAnnouncement(_handler, tx.Hash!, Arg.Is<AnnouncementRestoration>(ticket => ticket.Mask == cellMask));
            registry.Received(timeOutRequest ? 0 : 1).TryRequestCells(tx.Hash!, cellMask, Arg.Any<PublicKey>());
        }
    }

    [Test]
    public void should_stop_counting_as_full_provider_after_cell_request_times_out()
    {
        RecreateHandler();
        _blobCustodyTracker.Update(BlobCellMask.FromIndices([2, 7]));
        Assert.That(SparseBlobPoolPeerRegistry.HasSupernodeCustody(_blobCustodyTracker.CurrentMask), Is.False);
        Transaction tx = BuildBlobTransaction(fullProvider: true);
        BlobCellMask requestMask = BlobCellMask.FromIndices([4]);

        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob],
            [1024],
            [tx.Hash!],
            BlobCellMask.Full.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(tx.Hash!), Is.EqualTo(1));

            Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(tx.Hash!, requestMask), Is.True);
        }
        // Past the 10s request TTL, so the unanswered request is requeued.
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));
        Assert.That(_sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(tx.Hash!), Is.Zero);

        // Past the 5s backoff but inside the 15s pending-request TTL.
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));

        Assert.That(_sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(tx.Hash!), Is.Zero);
    }

    [Test]
    public void should_retry_capacity_parked_cells_without_a_new_announcement()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        registry.RecordAnnouncement(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>()).Returns(true);
        registry.GetRequestMask(Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>(), Arg.Any<int>()).Returns(BlobCellMask.Full);
        registry.TryRequestCells(Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>(), Arg.Any<PublicKey>()).Returns(false, true);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        Hash256 hash = HashFromInt(1000);
        AnnounceBlobTransaction(hash, 1024, TxType.Blob);
        registry.Received(1).TryRequestCells(hash, BlobCellMask.Full, _handler.Id);

        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));
        registry.Received(2).TryRequestCells(hash, BlobCellMask.Full, _handler.Id);
    }

    [Test]
    public void registry_should_leave_cell_capacity_for_an_honest_provider_after_four_silent_peers()
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] silentPeers = TestItem.PublicKeys.Take(4).Select(key => new TestSparseBlobPeer(key)).ToArray();
        TestSparseBlobPeer honestPeer = new(TestItem.PublicKeys[4]);
        foreach (TestSparseBlobPeer peer in silentPeers) registry.AddPeer(peer);
        registry.AddPeer(honestPeer);
        for (int peerIndex = 0; peerIndex < silentPeers.Length; peerIndex++)
        {
            for (int i = 0; i < 24; i++)
            {
                Hash256 hash = HashFromInt(peerIndex * 100 + i);
                registry.RecordAnnouncement(silentPeers[peerIndex], hash, BlobCellMask.Full);
                registry.TryRequestCells(hash, BlobCellMask.Full, silentPeers[peerIndex].Id);
            }
        }

        Hash256 honestHash = HashFromInt(1000);
        registry.RecordAnnouncement(honestPeer, honestHash, BlobCellMask.Full);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryRequestCells(honestHash, BlobCellMask.Full, honestPeer.Id), Is.True);
            Assert.That(honestPeer.CellRequests, Is.EqualTo(new[] { (honestHash, BlobCellMask.Full) }));
        }
        registry.OnCellsRequestCompleted(honestHash, BlobCellMask.Full, honestPeer);
        Assert.That(silentPeers.Sum(peer => peer.CellRequests.Count) * BlobCellMask.CellCount, Is.LessThan(8192));
    }

    [Test]
    public void registry_should_release_single_provider_capacity_once_per_reservation()
    {
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers);
        TestSparseBlobPeer[] peers = TestItem.PublicKeys.Take(16).Select(key => new TestSparseBlobPeer(key)).ToArray();
        foreach (TestSparseBlobPeer peer in peers) registry.AddPeer(peer);
        for (int peerIndex = 0; peerIndex < peers.Length; peerIndex++)
        {
            for (int i = 0; i < 2; i++)
            {
                Hash256 hash = HashFromInt(peerIndex * 2 + i);
                registry.RecordAnnouncement(peers[peerIndex], hash, BlobCellMask.Full);
                Assert.That(registry.TryRequestCells(hash, BlobCellMask.Full, peers[peerIndex].Id), Is.True);
            }
        }

        TestSparseBlobPeer nextPeer = new(TestItem.PublicKeys[16]);
        TestSparseBlobPeer secondPeer = new(TestItem.PublicKeys[17]);
        registry.AddPeer(nextPeer);
        registry.AddPeer(secondPeer);
        Hash256 nextHash = HashFromInt(1000);
        Hash256 secondHash = HashFromInt(1001);
        registry.RecordAnnouncement(nextPeer, nextHash, BlobCellMask.Full);
        registry.RecordAnnouncement(secondPeer, secondHash, BlobCellMask.Full);
        Assert.That(registry.TryRequestCells(nextHash, BlobCellMask.Full, nextPeer.Id), Is.False);
        registry.OnCellsRequestCompleted(HashFromInt(0), BlobCellMask.Full, peers[0]);
        registry.OnCellsRequestCompleted(HashFromInt(0), BlobCellMask.Full, peers[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryRequestCells(nextHash, BlobCellMask.Full, nextPeer.Id), Is.True);
            Assert.That(registry.TryRequestCells(secondHash, BlobCellMask.Full, secondPeer.Id), Is.False);
        }
        registry.RemovePeer(peers[0]);
        timers.Timer.Fire();
        Assert.That(secondPeer.CellRequests, Is.EqualTo(new[] { (secondHash, BlobCellMask.Full) }));
    }

    [Test]
    public void registry_should_expire_only_the_peer_whose_cell_reservation_is_due([Values] bool expireOnRequest)
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer first = new(TestItem.PublicKeyC);
        TestSparseBlobPeer later = new(TestItem.PublicKeyD);
        Hash256 hash = HashFromInt(1000);
        BlobCellMask firstMask = BlobCellMask.FromIndices([4]);
        BlobCellMask laterMask = BlobCellMask.FromIndices([5]);
        registry.AddPeer(first);
        registry.AddPeer(later);
        registry.RecordAnnouncement(first, hash, BlobCellMask.Full);
        Assert.That(registry.TryRequestCells(hash, firstMask, first.Id), Is.True);
        clock.Add(TimeSpan.FromSeconds(9));
        registry.RecordAnnouncement(later, hash, BlobCellMask.Full);
        Assert.That(registry.TryRequestCells(hash, laterMask, first.Id), Is.True);
        clock.Add(TimeSpan.FromSeconds(2));
        if (expireOnRequest) Assert.That(registry.TryRequestCells(hash, laterMask, first.Id), Is.True);
        else timers.Timer.Fire();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetFullProviderAnnouncementCount(hash), Is.EqualTo(1));
            Assert.That(later.CellRequests, Is.EqualTo(new[] { (hash, laterMask) }));
        }
        Assert.That(registry.TryRequestCells(hash, laterMask, first.Id), Is.True);
        registry.OnCellsRequestCompleted(hash, laterMask, later);
        timers.Timer.Fire();
        Assert.That(later.CellRequests, Is.EqualTo(new[] { (hash, laterMask), (hash, firstMask) }));
    }

    [Test]
    public void registry_should_disconnect_when_transaction_and_invalid_cells_have_same_source([Values] bool corruptProof)
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        if (corruptProof) CorruptBodyProof(tx);
        byte[][] invalidCells = corruptProof ? cells : CorruptCellValue(cells);

        using SparseBlobPoolPeerRegistry registry = new(_transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        TestSparseBlobPeer peer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer otherPeer = new(TestItem.PublicKeyA);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);

        registry.AddPeer(peer);
        registry.AddPeer(otherPeer);
        registry.RecordAnnouncement(otherPeer, tx.Hash!, cellMask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peer, tx), Is.Null);
            Assert.That(registry.RecordCells(peer, tx.Hash!, cellMask, invalidCells), Is.True);
        }

        _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        Assert.That(peer.Disconnects, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Disconnects[0].Reason, Is.EqualTo(DisconnectReason.BreachOfProtocol));
            Assert.That(otherPeer.Disconnects, Is.Empty);
            Assert.That(otherPeer.CellRequests, Is.EqualTo(new[] { (tx.Hash!, cellMask) }));
            Assert.That(otherPeer.TransactionRequests, Is.EqualTo(new[] { tx.Hash! }));
        }
        _transactionPool.Received(1).ForgetRejectedBlobTransaction(tx.Hash!);
    }

    [Test, CancelAfter(10_000)]
    public void registry_should_not_invoke_disconnect_callback_under_transaction_state_lock([Values] bool cellsFirst, CancellationToken cancellationToken)
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
        Task<bool>? mutation = null;
        txPeer.Disconnecting = () =>
        {
            mutation = Task.Run(() => registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask));
            reentrantMutationCompleted = mutation.Wait(TimeSpan.FromSeconds(2), cancellationToken)
                && mutation.GetAwaiter().GetResult();
        };
        registry.AddPeer(txPeer);
        registry.AddPeer(cellPeer);
        registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask);
        try
        {
            if (cellsFirst) Assert.That(registry.RecordCells(cellPeer, tx.Hash!, cellMask, cells), Is.True);
            Assert.That(registry.RecordTransaction(txPeer, tx), Is.Null);
            if (!cellsFirst) Assert.That(registry.RecordCells(cellPeer, tx.Hash!, cellMask, cells), Is.True);
        }
        finally
        {
            mutation?.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).GetAwaiter().GetResult();
        }

        Assert.That(reentrantMutationCompleted, Is.True);
    }

    [Test]
    public void registry_should_preserve_buffered_cells_when_a_new_source_changes_blob_count([Values] bool bodyAvailable, [Values] bool attachedToTransaction)
    {
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask firstMask, out byte[][] firstCells);
        BlobCellMask secondMask = BlobCellMask.FromIndices([9]);
        byte[][] wrongCells = [.. firstCells, .. firstCells];
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        Assert.That(registry.RecordCells(peers[0], transaction.Hash!, firstMask, firstCells), Is.True);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        bool pooled = bodyAvailable;
        _transactionPool.TryGetPendingBlobTransaction(transaction.Hash!, out Arg.Any<Transaction>()).Returns(call =>
        {
            call[1] = pooled ? transaction : null;
            return pooled;
        });
        _transactionPool.MergeBlobCells(transaction.Hash!, firstMask, firstCells).Returns(BlobCellMergeResult.Accepted);
        if (attachedToTransaction)
        {
            Transaction incoming = new();
            transaction.CopyTo(incoming, copyHash: true);
            incoming.NetworkWrapper = ((ShardBlobNetworkWrapper)transaction.NetworkWrapper!) with { CellMask = secondMask, Cells = wrongCells };
            Assert.That(registry.RecordTransaction(peers[1], incoming), Is.Null);
        }
        else
        {
            Assert.That(registry.RecordCells(peers[1], transaction.Hash!, secondMask, wrongCells), Is.EqualTo(!bodyAvailable));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peers[1], transaction.Hash!, secondMask), Is.EqualTo(!bodyAvailable && !attachedToTransaction));
            Assert.That(peers[1].Disconnects, Has.Count.EqualTo(bodyAvailable || attachedToTransaction ? 1 : 0));
        }
        pooled = true;
        Assert.That(registry.TryApplyRecordedCells(transaction.Hash!), Is.True);
        _transactionPool.Received(1).MergeBlobCells(transaction.Hash!, firstMask, firstCells);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers[0].Disconnects, Is.Empty);
            Assert.That(peers[1].Disconnects, Has.Count.EqualTo(1));
        }
    }
    [Test]
    public void registry_should_not_exclude_a_valid_body_source_when_early_cells_have_the_wrong_blob_count([Values] bool cellsFirst)
    {
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        if (!cellsFirst) Assert.That(registry.RecordTransaction(peers[1], transaction), Is.Null);
        Assert.That(registry.RecordCells(peers[0], transaction.Hash!, mask, [.. cells, .. cells]), Is.EqualTo(cellsFirst));
        transaction.NetworkWrapper = ((ShardBlobNetworkWrapper)transaction.NetworkWrapper!) with { CellMask = mask, Cells = cells };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peers[1], transaction), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(registry.RecordAnnouncement(peers[0], transaction.Hash!, mask), Is.False);
            Assert.That(registry.RecordAnnouncement(peers[1], transaction.Hash!, mask), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers[0].Disconnects, Has.Count.EqualTo(1));
            Assert.That(peers[1].Disconnects, Is.Empty);
        }
    }

    [Test]
    public void registry_should_disconnect_each_source_with_a_wrong_blob_count()
    {
        Transaction tx = BuildSparseBlobTransaction(out _, out _, out byte[][] fullCells);
        BlobCellMask firstMask = BlobCellMask.FromIndices([4]);
        BlobCellMask secondMask = BlobCellMask.FromIndices([9]);
        byte[] firstCell = BlobCellsHelper.SelectFlattenedCells(fullCells, BlobCellMask.Full, firstMask, 1)[0];
        byte[] secondCell = BlobCellsHelper.SelectFlattenedCells(fullCells, BlobCellMask.Full, secondMask, 1)[0];
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool,
            new BlobCustodyTracker(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            maxAdmissionDelay: TimeSpan.Zero);
        TestSparseBlobPeer transactionPeer = new(TestItem.PublicKeyB);
        TestSparseBlobPeer firstCellPeer = new(TestItem.PublicKeyC);
        TestSparseBlobPeer lastCellPeer = new(TestItem.PublicKeyD);
        registry.AddPeer(transactionPeer);
        registry.AddPeer(firstCellPeer);
        registry.AddPeer(lastCellPeer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(firstCellPeer, tx.Hash!, firstMask, [firstCell, firstCell]), Is.True);
            Assert.That(registry.RecordCells(lastCellPeer, tx.Hash!, secondMask, [secondCell, secondCell]), Is.True);
            Assert.That(registry.RecordTransaction(transactionPeer, tx), Is.Null);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactionPeer.Disconnects, Is.Empty);
            Assert.That(firstCellPeer.Disconnects, Has.Count.EqualTo(1));
            Assert.That(lastCellPeer.Disconnects, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void registry_should_allow_valid_tuple_after_ambiguous_proof_failure([Values(1, 3, 65)] int failures)
    {
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2 * (failures + 1));
        ShardBlobNetworkWrapper honestBody = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        byte[][] corruptCells = CorruptCellValue(cells);
        int accepted = 0;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            if (valid) accepted++;
            return valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        for (int i = 0; i <= failures; i++)
            Assert.That(registry.RecordAnnouncement(peers[2 * i], tx.Hash!, cellMask), Is.True);
        for (int i = 0; i < failures; i++)
        {
            TestSparseBlobPeer bodyPeer = peers[2 * i];
            TestSparseBlobPeer cellPeer = peers[2 * i + 1];
            tx.NetworkWrapper = honestBody;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask), Is.True);
                Assert.That(registry.RecordTransaction(bodyPeer, tx), Is.Null);
                Assert.That(registry.RecordCells(cellPeer, tx.Hash!, cellMask, corruptCells), Is.True);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accepted, Is.Zero);
                Assert.That(registry.RecordAnnouncement(bodyPeer, tx.Hash!, cellMask), Is.False);
                Assert.That(registry.RecordAnnouncement(cellPeer, tx.Hash!, cellMask), Is.False);
                Assert.That(peers[2 * (i + 1)].TransactionRequests, Has.Count.EqualTo(1));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(bodyPeer, HashFromInt(100000 + i), cellMask), Is.True);
                Assert.That(registry.RecordAnnouncement(cellPeer, HashFromInt(100000 + i), cellMask), Is.True);
            }
        }

        tx.NetworkWrapper = honestBody;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peers[^2], tx), Is.Null);
            Assert.That(registry.RecordCells(peers[^1], tx.Hash!, cellMask, cells), Is.True);
            Assert.That(accepted, Is.EqualTo(1));
        }
        _transactionPool.Received(failures).ForgetRejectedBlobTransaction(tx.Hash!);
        _transactionPool.Received(failures + 1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peers[0], tx.Hash!, cellMask), Is.EqualTo(failures > 32));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
    }

    private void RecreateHandler(
        int providerProbabilityPercent = 15,
        IBackgroundTaskScheduler? backgroundTaskScheduler = null,
        ISparseBlobPoolPeerRegistry? sparseBlobPoolPeerRegistry = null,
        IMessageSerializationService? serializer = null,
        ILogManager? logManager = null,
        TimeProvider? cellQualityClock = null,
        ITimestamper? requestClock = null,
        TimeProvider? pooledRequestClock = null)
    {
        _handler.Dispose();
        if (requestClock is not null && _session is not Session and not ForwardingSession)
            _session.InboundReceipts.Returns(new PacketReceiptTracker());
        _txPoolConfig.SparseBlobProviderProbabilityPercent.Returns(providerProbabilityPercent);
        _handler = new Eth72ProtocolHandler(
            _session,
            serializer ?? _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            backgroundTaskScheduler ?? RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            logManager ?? LimboLogs.Instance,
            _txPoolConfig,
            _specProvider,
            _blobCustodyTracker,
            sparseBlobPoolPeerRegistry ?? _sparseBlobPoolPeerRegistry,
            _txGossipPolicy, pooledRequestClock)
        { CellQualityClock = cellQualityClock ?? TimeProvider.System };
        if (requestClock is not null) SetHandlerTimestamper(_handler, requestClock);
        _session.AddProtocolHandler(_handler);
        _handler.Init();
    }

    private static void SetHandlerTimestamper(Eth72ProtocolHandler handler, ITimestamper timestamper) =>
        typeof(SyncPeerProtocolHandlerBase).GetField("_timestamper", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, timestamper);

    private void SetupGetCellsResponse(Transaction tx, BlobCellMask requestedMask, BlobCellMask availableMask, byte[][] cells, int materializationWork = 0)
        => SetupGetCellsResponse(_transactionPool, tx, requestedMask, availableMask, cells, materializationWork);

    private static void SetupGetCellsResponse(ITxPool txPool, Transaction tx, BlobCellMask requestedMask, BlobCellMask availableMask, byte[][] cells, int materializationWork = 0)
    {
        txPool.TryGetPendingBlobCellMetadata(
                tx.Hash!,
                out Arg.Any<BlobCellMask>(),
                out Arg.Any<int>(),
                out Arg.Any<int>())
            .Returns(x =>
            {
                x[1] = availableMask;
                x[2] = tx.BlobVersionedHashes?.Length ?? 0;
                x[3] = materializationWork;
                return true;
            });
        txPool.TryGetPendingBlobCellMask(tx.Hash!, out Arg.Any<BlobCellMask>())
            .Returns(x =>
            {
                x[1] = availableMask;
                return true;
            });
        txPool.TryGetBlobCells(tx.Hash!, requestedMask, out Arg.Any<BlobCellMask>(), out Arg.Any<byte[][]>())
            .Returns(x =>
            {
                x[2] = availableMask;
                x[3] = cells;
                return true;
            });
    }

    [Test, CancelAfter(10000)]
    public async Task body_retry_completion_preserves_the_latest_successful_send([Values] bool newerSendSucceeded, CancellationToken cancellationToken)
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 8);
        Transaction valid = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        Transaction bad = new();
        valid.CopyTo(bad, copyHash: true);
        CorruptBodyProof(bad);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.InvalidBlobProofs);
        int[] announcementOrder = newerSendSucceeded ? [0, 1, 2, 3, 4, 5, 6, 7] : [0, 1, 3, 2, 4, 5, 6, 7];
        foreach (int index in announcementOrder)
            Assert.That(registry.RecordAnnouncement(peers[index], valid.Hash!, mask), Is.True);
        System.Threading.Tasks.TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        peers[2].TransactionRequestHandler = _ =>
        {
            if (!newerSendSucceeded && peers[2].TransactionRequests.Count > 1) return false;
            started.TrySetResult();
            release.Wait(cancellationToken);
            return true;
        };
        if (!newerSendSucceeded)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordTransaction(peers[0], bad), Is.Null);
                Assert.That(registry.RecordCells(peers[1], valid.Hash!, mask, cells), Is.True);
                Assert.That(peers[3].TransactionRequests, Has.Count.EqualTo(1));
            }
        }
        Assert.That(registry.RecordTransaction(peers[newerSendSucceeded ? 0 : 4], bad), Is.Null);
        Task olderSend = Task.Run(() => registry.RecordCells(peers[newerSendSucceeded ? 1 : 5], valid.Hash!, mask, cells), cancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordTransaction(peers[newerSendSucceeded ? 2 : 6], bad), Is.Null);
                Assert.That(registry.RecordCells(peers[newerSendSucceeded ? 4 : 7], valid.Hash!, mask, cells), Is.True);
                Assert.That(peers[newerSendSucceeded ? 3 : 2].TransactionRequests, Has.Count.EqualTo(newerSendSucceeded ? 1 : 2));
            }
        }
        finally
        {
            release.Set();
            await olderSend.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        clock.Add(Timeouts.Eth);
        timers.Timer.Fire();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers[2].TransactionRequests, Has.Count.EqualTo(newerSendSucceeded ? 1 : 2));
            Assert.That(peers[3].TransactionRequests, Has.Count.EqualTo(newerSendSucceeded ? 1 : 2));
            Assert.That(peers[5].TransactionRequests, Has.Count.EqualTo(newerSendSucceeded ? 1 : 0));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
    }

    private static void CorruptBodyProof(Transaction transaction)
    {
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)transaction.NetworkWrapper!;
        byte[] proofs = wrapper.Proofs.ToArray();
        proofs.AsSpan(4 * CkzgLib.Ckzg.BytesPerProof, CkzgLib.Ckzg.BytesPerProof).Clear();
        transaction.NetworkWrapper = wrapper with { Proofs = proofs };
    }

    private static byte[][] CorruptCellValue(byte[][] cells)
    {
        byte[][] corrupted = cells.Select(cell => cell.ToArray()).ToArray();
        corrupted[0][31] ^= 1;
        return corrupted;
    }

    [Test]
    public void handler_should_request_new_hashes_after_ambiguous_failures([Values(1, 2, 3)] int failures)
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer bodyPeer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer cellPeer = CreateSparseBlobPeers(registry, 1)[0];
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        for (int i = 0; i < failures; i++)
        {
            Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells, out _, (ulong)i);
            CorruptBodyProof(transaction);
            registry.RecordAnnouncement(bodyPeer, transaction.Hash!, mask);
            registry.RecordAnnouncement(cellPeer, transaction.Hash!, mask);
            registry.RecordTransaction(bodyPeer, transaction);
            registry.RecordCells(cellPeer, transaction.Hash!, mask, cells);
        }
        _transactionPool.Received(failures).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);

        Transaction announced = BuildSparseBlobTransaction(out BlobCellMask announcedMask, out _, out _, 100);
        _transactionPool.NotifyAboutTx(announced.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        using NewPooledTransactionHashesMessage72 message = new([(byte)TxType.Blob], [1024], [announced.Hash!], announcedMask.ToBytes());
        HandleZeroMessage(message, Eth72MessageCode.NewPooledTransactionHashes);

        _transactionPool.Received(1).NotifyAboutTx(announced.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
        Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage>().Count(request => request.EthMessage.Hashes.Contains(announced.Hash!)),
            Is.EqualTo(1));
    }

    [Test]
    public void registry_should_keep_ambiguous_body_sources_eligible_across_hashes([Values(1, 2)] int failures)
    {
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 5);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        for (int i = 0; i < failures; i++)
        {
            if (i != 0) clock.Add(TimeSpan.FromSeconds(59));
            FailProof((ulong)i, 0, 1, false);
        }
        FailProof(10, 2, 3, true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers[0].TransactionRequests, Has.Count.EqualTo(1));
            Assert.That(peers[4].TransactionRequests, Is.Empty);
        }

        clock.Add(TimeSpan.FromSeconds(59));
        FailProof(11, 2, 3, true);
        Assert.That(peers[0].TransactionRequests, Has.Count.EqualTo(2));
        clock.Add(TimeSpan.FromSeconds(1));
        FailProof(12, 2, 3, true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers[0].TransactionRequests, Has.Count.EqualTo(3));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }

        void FailProof(ulong nonce, int body, int cell, bool alternative)
        {
            Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells, out _, nonce);
            CorruptBodyProof(transaction);
            if (alternative)
            {
                registry.RecordAnnouncement(peers[0], transaction.Hash!, mask);
                registry.RecordAnnouncement(peers[4], transaction.Hash!, mask);
            }
            registry.RecordAnnouncement(peers[body], transaction.Hash!, mask);
            registry.RecordAnnouncement(peers[cell], transaction.Hash!, mask);
            registry.RecordTransaction(peers[body], transaction);
            registry.RecordCells(peers[cell], transaction.Hash!, mask, cells);
        }
    }

    [Test]
    public void registry_should_not_frame_a_common_honest_provider_with_ambiguous_tuples([Values] bool corruptProof)
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 6);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        int accepted = 0;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            if (valid) accepted++;
            return valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        for (int index = 0; index < 3; index++)
        {
            Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells, out _, (ulong)index);
            if (corruptProof) CorruptBodyProof(transaction);
            TestSparseBlobPeer bodyPeer = corruptProof ? peers[index + 1] : peers[0];
            TestSparseBlobPeer cellPeer = corruptProof ? peers[0] : peers[index + 1];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(bodyPeer, transaction.Hash!, mask), Is.True);
                Assert.That(registry.RecordAnnouncement(cellPeer, transaction.Hash!, mask), Is.True);
                Assert.That(registry.RecordAnnouncement(peers[5], transaction.Hash!, mask), Is.True);
                Assert.That(registry.RecordTransaction(bodyPeer, transaction), Is.Null);
                Assert.That(registry.RecordCells(cellPeer, transaction.Hash!, mask, corruptProof ? cells : CorruptCellValue(cells)), Is.True);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(bodyPeer, transaction.Hash!, mask), Is.False);
                Assert.That(registry.RecordAnnouncement(cellPeer, transaction.Hash!, mask), Is.False);
                Assert.That(peers[5].TransactionRequests, Has.Count.EqualTo(index + 1));
                Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
                Assert.That(accepted, Is.Zero);
            }
        }

        Transaction retry = BuildSparseBlobTransaction(out BlobCellMask retryMask, out byte[][] retryCells, out _, 10);
        CorruptBodyProof(retry);
        foreach (TestSparseBlobPeer peer in new[] { peers[0], peers[5], peers[4], peers[3] })
            Assert.That(registry.RecordAnnouncement(peer, retry.Hash!, retryMask), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peers[4], retry), Is.Null);
            Assert.That(registry.RecordCells(peers[3], retry.Hash!, retryMask, retryCells), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers[0].TransactionRequests, Is.EqualTo(new[] { retry.Hash! }));
            Assert.That(peers[5].TransactionRequests, Has.Count.EqualTo(3));
        }

        Transaction valid = BuildSparseBlobTransaction(out BlobCellMask validMask, out byte[][] validCells, out _, 11);
        Assert.That(registry.RecordAnnouncement(peers[0], valid.Hash!, validMask), Is.True);
        if (corruptProof)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.TryRequestCells(valid.Hash!, validMask, peers[4].Id), Is.True);
                Assert.That(peers[0].CellRequests, Does.Contain((valid.Hash!, validMask)));
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peers[0], valid), Is.Null);
            Assert.That(registry.RecordCells(peers[0], valid.Hash!, validMask, validCells), Is.True);
        }
        if (corruptProof) registry.OnCellsRequestCompleted(valid.Hash!, validMask, peers[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(1));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
        _transactionPool.Received(5).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void maintenance_body_retry_rotates_past_silent_announcers([Values(0, 1, 2)] int alternativeCount)
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 3 + alternativeCount);
        foreach (TestSparseBlobPeer peer in peers) peer.CellRequestHandler = (_, _) => false;
        Transaction valid = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        Transaction bad = new();
        valid.CopyTo(bad, copyHash: true);
        CorruptBodyProof(bad);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        int accepted = 0;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool verified = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            if (verified) accepted++;
            return verified ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        foreach (TestSparseBlobPeer peer in peers)
            Assert.That(registry.RecordAnnouncement(peer, valid.Hash!, mask), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peers[0], bad), Is.Null);
            Assert.That(registry.RecordCells(peers[1], valid.Hash!, mask, cells), Is.True);
            Assert.That(peers[2].TransactionRequests, Has.Count.EqualTo(1));
        }
        for (int retry = 0; retry < Math.Max(1, alternativeCount); retry++)
        {
            clock.Add(Timeouts.Eth);
            timers.Timer.Fire();
            Assert.That(peers[alternativeCount == 0 ? 2 : 3 + retry].TransactionRequests,
                Has.Count.EqualTo(alternativeCount == 0 ? 2 : 1));
        }
        TestSparseBlobPeer responder = peers[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers[2].TransactionRequests, Has.Count.EqualTo(alternativeCount == 0 ? 2 : 1));
            Assert.That(registry.RecordTransaction(responder, valid), Is.Null);
            Assert.That(registry.RecordCells(responder, valid.Hash!, mask, cells), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(1));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
        _transactionPool.Received(2).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void registry_should_recover_honest_body_after_one_bad_cell_source()
    {
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 3);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        int accepted = 0;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            if (valid) accepted++;
            return valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        registry.RecordAnnouncement(peers[0], transaction.Hash!, cellMask);
        registry.RecordAnnouncement(peers[1], transaction.Hash!, cellMask);
        registry.RecordTransaction(peers[0], transaction);
        byte[][] corruptCells = CorruptCellValue(cells);
        registry.RecordCells(peers[1], transaction.Hash!, cellMask, corruptCells);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.Zero);
            Assert.That(peers[0].TransactionRequests, Is.Empty);
        }
        clock.Add(TimeSpan.FromSeconds(4));
        timers.Timer.Fire();
        Assert.That(peers[0].TransactionRequests, Is.Empty);
        clock.Add(TimeSpan.FromSeconds(1));
        timers.Timer.Fire();
        Assert.That(peers[0].TransactionRequests, Has.Count.EqualTo(1));

        registry.RecordTransaction(peers[0], transaction);
        registry.RecordAnnouncement(peers[2], transaction.Hash!, cellMask);
        registry.RecordCells(peers[2], transaction.Hash!, cellMask, cells);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(1));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
        _transactionPool.TryGetPendingBlobCellMask(transaction.Hash!, out Arg.Any<BlobCellMask>()).Returns(call =>
        {
            call[1] = cellMask;
            return true;
        });
        clock.Add(TimeSpan.FromSeconds(10));
        timers.Timer.Fire();
        Assert.That(peers.SelectMany(peer => peer.TransactionRequests).Count(), Is.EqualTo(1));
    }

    [Test]
    public void registry_should_rotate_body_source_after_real_proof_failure([Values] bool corruptProof)
    {
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask cellMask, out byte[][] cells);
        if (corruptProof) CorruptBodyProof(transaction);

        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 3);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        AcceptTxResult? proofResult = null;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            proofResult = valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
            return proofResult.Value;
        });
        foreach (TestSparseBlobPeer peer in peers)
            Assert.That(registry.RecordAnnouncement(peer, transaction.Hash!, cellMask), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peers[0], transaction), Is.Null);
            Assert.That(registry.RecordCells(peers[1], transaction.Hash!, cellMask, cells), Is.True);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofResult, Is.EqualTo(corruptProof ? AcceptTxResult.InvalidBlobProofs : AcceptTxResult.Accepted));
            Assert.That(peers[0].TransactionRequests, Is.Empty);
            Assert.That(peers[1].TransactionRequests, Is.Empty);
            Assert.That(peers[2].TransactionRequests, Has.Count.EqualTo(corruptProof ? 1 : 0));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
    }

    [Test]
    public void Expired_reply_retires_its_network_reservation_before_requesting_the_honest_announcer()
    {
        _handler.Dispose();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        Eth72ProtocolHandler expired = CreateCellServingPeer(registry, clock, 101);
        Eth72ProtocolHandler honest = CreateCellServingPeer(registry, clock, 102);
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        ISparseBlobPoolPeer expiredPeer = expired;
        ISparseBlobPoolPeer honestPeer = honest;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(expiredPeer, transaction.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRequestCells(transaction.Hash!, mask, expiredPeer.Id), Is.True);
        }
        long expiredId = GetLastGetCellsRequestId(transaction.Hash!, mask);
        Assert.That(registry.RecordAnnouncement(honestPeer, transaction.Hash!, BlobCellMask.Full), Is.True);
        int before = _deliveredMessages.OfType<GetCellsMessage72>().Count();
        clock.Add(Timeouts.Eth + TimeSpan.FromMilliseconds(1));
        _handler = expired;
        using CellsMessage72 late = new(expiredId, [transaction.Hash!.ValueHash256], [cells], mask.ToBytes());
        HandleZeroMessage(late, Eth72MessageCode.Cells);
        GetCellsMessage72 retry = _deliveredMessages.OfType<GetCellsMessage72>().Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(before + 1));
            Assert.That(retry.Hashes, Is.EqualTo(new[] { transaction.Hash!.ValueHash256 }));
            Assert.That(BlobCellMask.FromBytes(retry.CellMask), Is.EqualTo(mask));
            Assert.That(registry.GetFullProviderAnnouncementCount(transaction.Hash!), Is.EqualTo(1));
            expired.Session.Received(1).DeliverMessage(Arg.Any<GetCellsMessage72>());
            honest.Session.Received(1).DeliverMessage(retry);
        }
        _transactionPool.DidNotReceive().MergeBlobCells(transaction.Hash!, mask, Arg.Any<byte[][]>());
        _handler = honest;
    }

    [Test]
    public void Old_request_completed_before_replacement_send_keeps_the_new_reservation([Values] bool maintenanceCompletion)
    {
        bool writable = true;
        Session? realSession = null;
        using Reactive.AnonymousDisposable _ = new(() => realSession?.MarkDisconnected(
            DisconnectReason.ClientQuitting, DisconnectType.Remote, "test completed"));
        if (maintenanceCompletion)
        {
            IChannel channel = Substitute.For<IChannel>();
            channel.IsWritable.Returns(_ => writable);
            realSession = new Session(30303, new Node(TestItem.PublicKeyA, "127.0.0.1", 30303),
                channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            IProtocolHandler p2p = Substitute.For<IProtocolHandler>();
            p2p.ProtocolCode.Returns("p2p");
            p2p.MessageIdSpaceSize.Returns(16);
            realSession.AddProtocolHandler(p2p);
            realSession.Handshake(TestItem.PublicKeyA);
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            context.Channel.Returns(channel);
            IPacketSender sender = Substitute.For<IPacketSender>();
            sender.Enqueue(Arg.Any<P2PMessage>()).Returns(call => Capture(call.Arg<P2PMessage>()));
            sender.Enqueue(Arg.Any<GetCellsMessage72>()).Returns(call => Capture(call.Arg<GetCellsMessage72>()));
            realSession.Init(5, context, sender);
            _session = realSession;
        }
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance, TimeSpan.Zero);
        bool claimOldReply = false;
        long originalRequestId = 0;
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        SparseRegistryDecorator decorated = new(registry, true, () =>
        {
            if (!claimOldReply) return;
            claimOldReply = false;
            int sentBeforeClaim = _deliveredMessages.OfType<GetCellsMessage72>().Count();
            if (maintenanceCompletion)
            {
                writable = false;
                ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(DateTimeOffset.UtcNow + Timeouts.Eth + TimeSpan.FromSeconds(1));
                writable = true;
            }
            else
            {
                using CellsMessage72 oldReply = new(originalRequestId, [], [], mask.ToBytes());
                HandleZeroMessage(oldReply, Eth72MessageCode.Cells);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.TryRequestCells(transaction.Hash!, mask, TestItem.PublicKeyA), Is.True);
                Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(sentBeforeClaim));
            }
        });
        RecreateHandler(sparseBlobPoolPeerRegistry: decorated);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        _transactionPool.TryGetPendingBlobTransaction(transaction.Hash!, out Arg.Any<Transaction>()).Returns(call =>
        {
            call[1] = transaction;
            return true;
        });
        _transactionPool.MergeBlobCells(transaction.Hash!, mask, Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>()).Returns(call =>
        {
            call[3] = mask;
            return BlobCellMergeResult.Accepted;
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.RecordAnnouncement(peer, transaction.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRequestCells(transaction.Hash!, mask, peer.Id), Is.True);
        }
        originalRequestId = GetLastGetCellsRequestId(transaction.Hash!, mask);
        registry.Clear(transaction.Hash!);
        Assert.That(decorated.RecordAnnouncement(peer, transaction.Hash!, BlobCellMask.Full), Is.True);
        claimOldReply = true;
        Assert.That(registry.TryRequestCells(transaction.Hash!, mask, peer.Id), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimOldReply, Is.False);
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(2));
        }
        Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Last().CellMask, Is.EqualTo(mask.ToBytes()));
        using CellsMessage72 currentReply = new(GetLastGetCellsRequestId(transaction.Hash!, mask), [transaction.Hash!.ValueHash256], [cells], mask.ToBytes());
        HandleZeroMessage(currentReply, Eth72MessageCode.Cells);
        BlobCellMask remaining = BlobCellMask.FromIndices([5]);
        Assert.That(registry.TryRequestCells(transaction.Hash!, remaining, peer.Id), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Any(request =>
                request.Hashes.Contains(transaction.Hash!.ValueHash256)
                && !(BlobCellMask.FromBytes(request.CellMask) & remaining).IsEmpty), Is.True);
            if (realSession is not null) Assert.That(realSession.IsClosing, Is.False);
            else _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }

        int Capture(P2PMessage message)
        {
            _deliveredMessages.Add(message);
            message.AddTo(_disposables);
            return 1;
        }
    }

    [Test]
    public void older_cell_claim_stays_stale_after_newer_same_state_reservation_completes()
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        Hash256 hash = HashFromInt(7091);
        BlobCellMask oldMask = BlobCellMask.FromIndices([4]);
        BlobCellMask honestMask = BlobCellMask.FromIndices([5]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peers[0], hash, oldMask), Is.True);
            Assert.That(registry.RecordAnnouncement(peers[1], hash, honestMask), Is.True);
        }
        object state = GetTrackedState(registry, hash);
        Assert.That(registry.TryRequestCells(hash, oldMask, peers[0].Id), Is.True);
        (IDisposable Lease, Action<BlobCellMask> Complete) old = peers[0].ClaimCellsReservation!();
        using IDisposable oldLease = old.Lease;
        old.Complete(oldMask);
        Assert.That(registry.TryRequestCells(hash, honestMask, peers[1].Id), Is.True);
        (IDisposable Lease, Action<BlobCellMask> Complete) honest = peers[1].ClaimCellsReservation!();
        using IDisposable honestLease = honest.Lease;
        Assert.That(registry.TryRequestCells(hash, oldMask, peers[0].Id), Is.True);
        (IDisposable Lease, Action<BlobCellMask> Complete) newer = peers[0].ClaimCellsReservation!();
        using (newer.Lease) newer.Complete(oldMask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetTrackedState(registry, hash), Is.SameAs(state));
            Assert.That(((ICellRequestOwnership)old.Lease).IsCurrent, Is.False);
            Assert.That(((ICellRequestOwnership)honest.Lease).IsCurrent, Is.True);
        }
        registry.RemoveAnnouncementPermanently(peers[0], hash, old.Lease);
        Assert.That(registry.TryBeginAnnouncementRestore(peers[0], hash, out AnnouncementRestoration? restoration, old.Lease), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restoration, Is.Null);
            Assert.That(registry.CoversRequest(peers[0], hash.ValueHash256, oldMask), Is.True);
            Assert.That(registry.RecordCells(peers[0], hash, oldMask, [new byte[CkzgLib.Ckzg.BytesPerCell]], old.Lease), Is.False);
        }
        old.Complete(oldMask);
        old.Lease.Dispose();
        honest.Complete(honestMask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(((ICellRequestOwnership)honest.Lease).IsCurrent, Is.True);
            Assert.That(registry.RecordCells(peers[1], hash, honestMask, [new byte[CkzgLib.Ckzg.BytesPerCell]], honest.Lease), Is.True);
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
        registry.Clear(hash);
    }

    [Test]
    public void cell_count_mismatch_penalizes_only_current_claim_ownership([Values] bool staleOwnership)
    {
        ManualTimeProvider quality = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory());
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        bool bodyKnown = false;
        bool mutate = false;
        ISparseBlobPoolPeer? peer = null;
        SparseRegistryDecorator? decorated = null;
        decorated = new(registry, true, beforeCellMutation: () =>
        {
            if (!mutate) return;
            mutate = false;
            bodyKnown = true;
            if (!staleOwnership) return;
            registry.Clear(transaction.Hash!);
            Assert.That(decorated!.RecordAnnouncement(peer!, transaction.Hash!, BlobCellMask.Full), Is.True);
        });
        _transactionPool.TryGetPendingBlobTransaction(transaction.Hash!, out Arg.Any<Transaction>()).Returns(call =>
        {
            call[1] = bodyKnown ? transaction : null;
            return bodyKnown;
        });
        RecreateHandler(sparseBlobPoolPeerRegistry: decorated, cellQualityClock: quality);
        HandleIncomingStatusMessage();
        peer = (ISparseBlobPoolPeer)_handler;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.RecordAnnouncement(peer, transaction.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRequestCells(transaction.Hash!, mask, peer.Id), Is.True);
        }
        mutate = true;
        using CellsMessage72 response = new(GetLastGetCellsRequestId(transaction.Hash!, mask),
            [transaction.Hash!.ValueHash256], [cells.Concat(cells).ToArray()], mask.ToBytes());
        HandleZeroMessage(response, Eth72MessageCode.Cells);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mutate, Is.False);
            Assert.That(registry.CoversRequest(decorated.Resolve(peer), transaction.Hash!.ValueHash256, BlobCellMask.Full), Is.EqualTo(staleOwnership));
            _session.Received(staleOwnership ? 0 : 1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((0, 0)));
        }
        if (staleOwnership)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetTrackedState(registry, transaction.Hash!).GetType().GetProperty("Cells")!
                    .GetValue(GetTrackedState(registry, transaction.Hash!)), Is.Null);
                Assert.That(registry.TryRequestCells(transaction.Hash!, BlobCellMask.FromIndices([5]), peer.Id), Is.True);
            }
        }
    }

    [Test]
    public void unfamiliar_cell_ownership_is_neutral_and_direct_requests_remain_valid([Values] bool unfamiliarOwnership)
    {
        ManualTimeProvider quality = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory());
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Hash256 hash = HashFromInt(7092);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
        int completions = 0;
        int disposals = 0;
        bool sent = unfamiliarOwnership
            ? peer.TrySendGetCells(hash, mask, () => (new Reactive.AnonymousDisposable(() => disposals++), _ => completions++))
            : peer.TrySendGetCells(hash, mask);
        Assert.That(sent, Is.True);
        using CellsMessage72 response = new(GetLastGetCellsRequestId(hash, mask), [], [], mask.ToBytes());
        HandleZeroMessage(response, Eth72MessageCode.Cells);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.CoversRequest(peer, hash.ValueHash256, BlobCellMask.Full), Is.EqualTo(unfamiliarOwnership));
            Assert.That(completions, Is.EqualTo(unfamiliarOwnership ? 1 : 0));
            Assert.That(disposals, Is.EqualTo(unfamiliarOwnership ? 1 : 0));
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(1));
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((0, 0)));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public void stale_registry_cell_claim_is_ignored_before_deserialization()
    {
        ManualTimeProvider quality = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory());
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Hash256 hash = HashFromInt(7090);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRequestCells(hash, mask, peer.Id), Is.True);
        }
        long originalRequestId = GetLastGetCellsRequestId(hash, mask);
        registry.Clear(hash);
        Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
        using CellsMessage72 malformedOldReply = new(originalRequestId, [hash.ValueHash256], [[]], BlobCellMask.Empty.ToBytes());
        Assert.That(() => HandleZeroMessage(malformedOldReply, Eth72MessageCode.Cells), Throws.Nothing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.CoversRequest(peer, hash.ValueHash256, BlobCellMask.Full), Is.True);
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((0, 0)));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        Assert.That(registry.TryRequestCells(hash, BlobCellMask.FromIndices([5]), peer.Id), Is.True);
    }

    [Test]
    public void claimed_old_cell_response_cannot_mutate_replacement_state([Values(0, 1, 2, 3)] int responseKind)
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory(), timestamper: clock);
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        BlobCellMask requested = responseKind == 2 ? mask | BlobCellMask.FromIndices([5]) : mask;
        bool replaceState = false;
        ISparseBlobPoolPeer? peer = null;
        SparseRegistryDecorator? decorated = null;
        decorated = new(registry, true, beforeCellMutation: () =>
        {
            if (!replaceState) return;
            replaceState = false;
            registry.Clear(transaction.Hash!);
            Assert.That(decorated!.RecordAnnouncement(peer!, transaction.Hash!, BlobCellMask.Full), Is.True);
        });
        RecreateHandler(sparseBlobPoolPeerRegistry: decorated, cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        peer = (ISparseBlobPoolPeer)_handler;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.RecordAnnouncement(peer, transaction.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRequestCells(transaction.Hash!, requested, peer.Id), Is.True);
        }
        long requestId = GetLastGetCellsRequestId(transaction.Hash!, requested);
        object originalState = GetTrackedState(registry, transaction.Hash!);
        replaceState = true;
        using CellsMessage72 response = responseKind switch
        {
            0 => new(requestId, [], [], requested.ToBytes()),
            1 => new(requestId, [], [], BlobCellMask.Empty.ToBytes()),
            _ => new(requestId, [transaction.Hash!.ValueHash256], [cells], mask.ToBytes())
        };
        HandleZeroMessage(response, Eth72MessageCode.Cells);
        object replacementState = GetTrackedState(registry, transaction.Hash!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replaceState, Is.False);
            Assert.That(replacementState, Is.Not.SameAs(originalState));
            Assert.That(registry.CoversRequest(decorated.Resolve(peer), transaction.Hash!.ValueHash256, BlobCellMask.Full), Is.True);
            Assert.That(replacementState.GetType().GetProperty("Cells")!.GetValue(replacementState), Is.Null);
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((0, 0)));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        Assert.That(registry.TryRequestCells(transaction.Hash!, BlobCellMask.FromIndices([5]), peer.Id), Is.True);
        registry.Clear(transaction.Hash!);
    }

    [Test]
    public void queued_invalid_cell_reply_faults_only_its_current_owner([Values] bool replaceState)
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        QueuedBackgroundTaskScheduler background = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory(), timestamper: clock);
        RecreateHandler(backgroundTaskScheduler: background, sparseBlobPoolPeerRegistry: registry,
            cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, transaction.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRequestCells(transaction.Hash!, mask, peer.Id), Is.True);
        }
        long requestId = GetLastGetCellsRequestId(transaction.Hash!, mask);
        using CellsMessage72 wrongHashReply = new(requestId, [HashFromInt(7099).ValueHash256], [cells], mask.ToBytes());
        HandleZeroMessage(wrongHashReply, Eth72MessageCode.Cells);
        Assert.That(background.HasPendingTask, Is.True);
        if (replaceState)
        {
            registry.Clear(transaction.Hash!);
            Assert.That(registry.RecordAnnouncement(peer, transaction.Hash!, BlobCellMask.Full), Is.True);
            Assert.DoesNotThrow(() => background.RunNext());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.CoversRequest(peer, transaction.Hash!.ValueHash256, BlobCellMask.Full), Is.True);
                Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((0, 0)));
                _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            Assert.That(registry.TryRequestCells(transaction.Hash!, mask, peer.Id), Is.True);
        }
        else
        {
            Assert.DoesNotThrow(() => background.RunNext());
            _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol,
                Arg.Is<string>(message => message.Contains("Response does not match requested blob cells")));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(background.HasPendingTask, Is.False);
            _transactionPool.DidNotReceive().MergeBlobCells(Arg.Any<Hash256>(), Arg.Any<BlobCellMask>(),
                Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>());
        }
    }

    [Test]
    public void registry_claim_holds_release_work_after_the_last_owner_and_ignore_replacement_state()
    {
        ManualTimerFactory timers = new();
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer peer = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(70000);
        BlobCellMask first = BlobCellMask.FromIndices([4]);
        BlobCellMask second = BlobCellMask.FromIndices([5]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRequestCells(hash, first | second, peer.Id), Is.True);
        }
        (IDisposable Lease, Action<BlobCellMask> Complete) firstOwnership = peer.ClaimCellsReservation!();
        (IDisposable Lease, Action<BlobCellMask> Complete) secondOwnership = peer.ClaimCellsReservation!();
        using IDisposable firstHold = firstOwnership.Lease;
        using IDisposable secondHold = secondOwnership.Lease;
        firstOwnership.Complete(first);
        firstHold.Dispose();
        clock.Add(Timeouts.Eth);
        timers.Timer.Fire();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryRequestCells(hash, first | second, peer.Id), Is.False);
            Assert.That(peer.CellRequests, Has.Count.EqualTo(1));
        }
        secondOwnership.Complete(second);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryRequestCells(hash, first, peer.Id), Is.True);
            Assert.That(peer.CellRequests, Has.Count.EqualTo(2));
        }
        secondOwnership.Complete(first);
        secondHold.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryRequestCells(hash, first, peer.Id), Is.True);
            Assert.That(peer.CellRequests, Has.Count.EqualTo(2));
        }
        (IDisposable Lease, Action<BlobCellMask> Complete) staleOwnership = peer.ClaimCellsReservation!();
        using IDisposable staleHold = staleOwnership.Lease;
        registry.Clear(hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRequestCells(hash, first, peer.Id), Is.True);
        }
        staleOwnership.Complete(first);
        staleHold.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryRequestCells(hash, first, peer.Id), Is.True);
            Assert.That(peer.CellRequests, Has.Count.EqualTo(3));
        }
        using IDisposable abandonedHold = peer.ClaimCellsReservation!().Lease;
        abandonedHold.Dispose();
        Assert.That(registry.TryRequestCells(hash, first, peer.Id), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.CellRequests, Has.Count.EqualTo(4));
            Assert.That(peer.Disconnects, Is.Empty);
        }
        (IDisposable Lease, Action<BlobCellMask> Complete) retiredOwnership = peer.ClaimCellsReservation!();
        using IDisposable retiredHold = retiredOwnership.Lease;
        registry.OnCellsRequestCompleted(hash, first, peer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryRequestCells(hash, first, peer.Id), Is.True);
            Assert.That(peer.CellRequests, Has.Count.EqualTo(5));
        }
        retiredHold.Dispose();
        Assert.That(registry.TryRequestCells(hash, first, peer.Id), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.CellRequests, Has.Count.EqualTo(5));
            Assert.That(peer.CellRequests[^1], Is.EqualTo((hash, first)));
            Assert.That(peer.Disconnects, Is.Empty);
        }
    }

    [TestCase(false, false, false, false)]
    [TestCase(true, false, false, false)]
    [TestCase(false, true, false, false)]
    [TestCase(false, true, true, false)]
    [TestCase(false, true, false, true)]
    public void Cell_deadline_uses_final_receipt_before_the_handler_queue(bool finalChunkLate, bool delayBackground, bool replaceHeldState, bool removeBeforeClaim)
    {
        ManualTimeProvider qualityClock = new();
        ManualTimestamper clock = new(qualityClock.GetUtcNow().UtcDateTime);
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        EmbeddedChannel channel = new();
        Session realSession = new(30303, new Node(TestItem.PublicKeyA, "127.0.0.1", 30303),
            channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        IProtocolHandler p2p = Substitute.For<IProtocolHandler>();
        p2p.ProtocolCode.Returns("p2p");
        p2p.MessageIdSpaceSize.Returns(16);
        realSession.AddProtocolHandler(p2p);
        realSession.Handshake(TestItem.PublicKeyA);
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Channel.Returns(channel);
        IPacketSender sender = Substitute.For<IPacketSender>();
        long lastRequestId = 0;
        List<(ValueHash256[] Hashes, byte[] Mask)> sentRequests = [];
        sender.Enqueue(Arg.Any<P2PMessage>()).Returns(call =>
        {
            if (call.Arg<P2PMessage>() is GetCellsMessage72 request) Capture(request);
            return 1;
        });
        sender.Enqueue(Arg.Any<GetCellsMessage72>()).Returns(call =>
        {
            Capture(call.Arg<GetCellsMessage72>());
            return 1;
        });
        realSession.Init(5, context, sender);
        _session = realSession;
        QueuedBackgroundTaskScheduler background = new();
        RecreateHandler(backgroundTaskScheduler: delayBackground ? background : RunImmediatelyScheduler.Instance,
            sparseBlobPoolPeerRegistry: registry, cellQualityClock: qualityClock, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        bool defer = false;
        Queue<Action> queued = new();
        bool backgroundQueued = false;
        IEventExecutor executor = Substitute.For<IEventExecutor>();
        executor.GetNext().Returns(executor);
        executor.InEventLoop.Returns(_ => !defer);
        executor.When(x => x.Execute(Arg.Any<Action<object, object>>(), Arg.Any<object>(), Arg.Any<object>())).Do(call =>
        {
            Action action = () => call.ArgAt<Action<object, object>>(0)(call.ArgAt<object>(1), call.ArgAt<object>(2));
            if (defer) queued.Enqueue(action);
            else action();
        });
        channel.Pipeline.AddLast(new ZeroFrameMerger(LimboLogs.Instance, realSession.InboundReceipts));
        channel.Pipeline.AddLast(executor, new ZeroNettyP2PHandler(realSession, LimboLogs.Instance));
        try
        {
            BlobCellMask mask = BlobCellMask.FromIndices([4]);
            for (int i = 0; i < 7; i++)
            {
                if (!peer.CanRequestCells) Advance(TimeSpan.FromMinutes(1));
                Hash256 silentHash = HashFromInt(5000 + i);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(registry.RecordAnnouncement(peer, silentHash, mask), Is.True);
                    Assert.That(registry.TryRequestCells(silentHash, mask, peer.Id), Is.True);
                }
                Advance(Timeouts.Eth);
                timers.Timer.Fire();
                Assert.That(realSession.IsClosing, Is.False);
            }
            Advance(TimeSpan.FromMinutes(1));
            Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask responseMask, out byte[][] cells);
            _transactionPool.TryGetPendingBlobTransaction(transaction.Hash!, out Arg.Any<Transaction>()).Returns(call =>
            {
                call[1] = transaction;
                return true;
            });
            _transactionPool.MergeBlobCells(transaction.Hash!, responseMask, Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>())
                .Returns(call =>
                {
                    call[3] = responseMask;
                    return BlobCellMergeResult.Accepted;
                });
            BlobCellMask remainingMask = BlobCellMask.FromIndices([5]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(peer, transaction.Hash!, BlobCellMask.Full), Is.True);
                Assert.That(registry.TryRequestCells(transaction.Hash!, responseMask, peer.Id), Is.True);
            }
            using CellsMessage72 reply = new(lastRequestId, [transaction.Hash!.ValueHash256], [cells], responseMask.ToBytes())
            { AdaptivePacketType = 16 + Eth72MessageCode.Cells };
            using DisposableByteBuffer serialized = _svc.ZeroSerialize(reply).AsDisposable();
            using DisposableByteBuffer frames = new ZeroPacketSplitterTestWrapper().Encode(serialized).AsDisposable();
            Advance(Timeouts.Eth - TimeSpan.FromSeconds(1));
            defer = true;
            while (frames.IsReadable())
            {
                int offset = frames.ReaderIndex;
                int payload = (frames.GetByte(offset) << 16) | (frames.GetByte(offset + 1) << 8) | frames.GetByte(offset + 2);
                int frameLength = Frame.HeaderSize + payload + Frame.CalculatePadding(payload);
                if (finalChunkLate && frames.ReadableBytes == frameLength)
                {
                    Advance(TimeSpan.FromSeconds(2));
                }
                channel.WriteInbound(frames.ReadRetainedSlice(frameLength));
            }
            Assert.That(queued, Has.Count.EqualTo(1));
            if (!finalChunkLate)
            {
                Advance(Timeouts.Cleanup + Timeouts.Cleanup);
                timers.Timer.Fire();
                long queuedRequestId = lastRequestId;
                registry.TryRequestCells(transaction.Hash!, responseMask, peer.Id);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(lastRequestId, Is.EqualTo(queuedRequestId));
                    Assert.That(realSession.IsClosing, Is.False);
                }
            }
            if (removeBeforeClaim) registry.Clear(transaction.Hash!);
            defer = false;
            queued.Dequeue()();
            backgroundQueued = background.HasPendingTask;
            Assert.That(backgroundQueued, Is.EqualTo(delayBackground && !finalChunkLate && !removeBeforeClaim));
            if (removeBeforeClaim)
            {
                using (Assert.EnterMultipleScope())
                {
                    _transactionPool.DidNotReceive().MergeBlobCells(transaction.Hash!, responseMask, Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>());
                    Assert.That(realSession.IsClosing, Is.False);
                    Assert.That(registry.RecordAnnouncement(peer, transaction.Hash!, BlobCellMask.Full), Is.True);
                    Assert.That(registry.TryRequestCells(transaction.Hash!, responseMask, peer.Id), Is.True);
                }
                long replacementRequestId = lastRequestId;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(registry.TryRequestCells(transaction.Hash!, responseMask, peer.Id), Is.True);
                    Assert.That(lastRequestId, Is.EqualTo(replacementRequestId));
                }
                using CellsMessage72 replacementReply = new(lastRequestId, [transaction.Hash!.ValueHash256], [cells], responseMask.ToBytes());
                HandleZeroMessage(replacementReply, Eth72MessageCode.Cells);
                Assert.That(background.HasPendingTask, Is.True);
                background.RunNext();
            }
            channel.CheckException();
            if (backgroundQueued)
            {
                _transactionPool.DidNotReceive().MergeBlobCells(transaction.Hash!, responseMask, Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>());
                Advance(Timeouts.Eth + TimeSpan.FromSeconds(1));
                timers.Timer.Fire();
                long heldRequestId = lastRequestId;
                registry.TryRequestCells(transaction.Hash!, responseMask, peer.Id);
                Assert.That(lastRequestId, Is.EqualTo(heldRequestId));
                if (replaceHeldState)
                {
                    registry.Clear(transaction.Hash!);
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(registry.RecordAnnouncement(peer, transaction.Hash!, BlobCellMask.Full), Is.True);
                        Assert.That(registry.TryRequestCells(transaction.Hash!, responseMask, peer.Id), Is.True);
                    }
                }
                else
                {
                    TestSparseBlobPeer[] fillers = CreateSparseBlobPeers(registry, 8);
                    for (int i = 0; i < 16383; i++)
                    {
                        ISparseBlobPoolPeer source = i < 2047 ? peer : fillers[(i - 2047) / 2048];
                        Hash256 hash = HashFromInt(50000 + i);
                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(registry.RecordAnnouncement(source, hash, BlobCellMask.Full), Is.True);
                            Assert.That(registry.RecordCells(source, hash, responseMask, [new byte[CkzgLib.Ckzg.BytesPerCell]]), Is.True);
                        }
                    }
                    Assert.That(registry.RecordAnnouncement(fillers[7], HashFromInt(100000), BlobCellMask.Full), Is.False);
                }
                long replacementRequestId = lastRequestId;
                background.RunNext();
                backgroundQueued = false;
                if (replaceHeldState)
                    _transactionPool.DidNotReceive().MergeBlobCells(transaction.Hash!, responseMask, Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>());
                if (replaceHeldState)
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(registry.TryRequestCells(transaction.Hash!, responseMask, peer.Id), Is.True);
                        Assert.That(lastRequestId, Is.EqualTo(replacementRequestId));
                    }
                    using CellsMessage72 replacementReply = new(lastRequestId, [transaction.Hash!.ValueHash256], [cells], responseMask.ToBytes());
                    HandleZeroMessage(replacementReply, Eth72MessageCode.Cells);
                    backgroundQueued = true;
                    background.RunNext();
                    backgroundQueued = false;
                }
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(realSession.IsClosing, Is.EqualTo(finalChunkLate));
                if (finalChunkLate)
                    _transactionPool.DidNotReceive().MergeBlobCells(transaction.Hash!, responseMask, Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>());
                else
                {
                    _transactionPool.Received(1).MergeBlobCells(transaction.Hash!, responseMask, Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>());
                }
            }
            if (!finalChunkLate)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(registry.TryRequestCells(transaction.Hash!, remainingMask, peer.Id), Is.True);
                    Assert.That(sentRequests.Any(request => request.Hashes.Contains(transaction.Hash!.ValueHash256)
                        && !(BlobCellMask.FromBytes(request.Mask) & remainingMask).IsEmpty), Is.True);
                }
            }
        }
        finally
        {
            defer = false;
            while (queued.TryDequeue(out Action? action)) action();
            if (background.HasPendingTask) background.RunNext(new CancellationToken(canceled: true));
            channel.FinishAndReleaseAll();
            realSession.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Local, "test completed");
        }

        void Advance(TimeSpan elapsed)
        {
            qualityClock.Advance(elapsed);
            clock.Add(elapsed);
        }

        void Capture(GetCellsMessage72 request)
        {
            lastRequestId = request.RequestId;
            sentRequests.Add((request.Hashes.ToArray(), request.CellMask.ToArray()));
        }
    }

    [Test]
    public void spaced_cell_timeouts_resume_selection_and_accumulate_failures([Values(7, 8)] int failures)
    {
        ManualTimeProvider qualityClock = new();
        ManualTimestamper requestClock = new(qualityClock.GetUtcNow().UtcDateTime);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: requestClock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: qualityClock);
        SetHandlerTimestamper(_handler, requestClock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        for (int i = 0; i < failures; i++)
        {
            Hash256 hash = HashFromInt(1000 + i);
            Assert.That(registry.RecordAnnouncement(peer, hash, mask), Is.True);
            if (!peer.CanRequestCells)
            {
                Assert.That(registry.TryRequestCells(hash, mask, peer.Id), Is.False);
                Advance(TimeSpan.FromMinutes(1));
                Assert.That(peer.CanRequestCells, Is.True);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.TryRequestCells(hash, mask, peer.Id), Is.True);
                Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(i + 1));
            }
            Advance(Timeouts.Eth);
            peer.MaintainSparseBlobState(requestClock.UtcNowOffset);
            _session.Received(i == 7 ? 1 : 0).InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
            if (i == 3) Assert.That(peer.CanRequestCells, Is.False);
        }

        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());

        void Advance(TimeSpan elapsed)
        {
            qualityClock.Advance(elapsed);
            requestClock.Add(elapsed);
        }
    }

    [Test]
    public void covered_partials_between_timeouts_do_not_hide_failures([Values(7, 8)] int failures, [Values] bool empty)
    {
        ManualTimeProvider qualityClock = new();
        ManualTimestamper requestClock = new(qualityClock.GetUtcNow().UtcDateTime);
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: requestClock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: qualityClock);
        SetHandlerTimestamper(_handler, requestClock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer alternative = CreateSparseBlobPeers(registry, 1)[0];
        BlobCellMask requested = BlobCellMask.FromIndices([4, 5, 6]);
        BlobCellMask partial = BlobCellMask.FromIndices([4]);
        for (int i = 0; i < failures; i++)
        {
            if (!peer.CanRequestCells) Advance(TimeSpan.FromMinutes(1));
            Hash256 hash = HashFromInt(2000 + i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(peer, hash, requested), Is.True);
                Assert.That(registry.RecordAnnouncement(alternative, hash, requested), Is.True);
                Assert.That(peer.TrySendGetCells(hash, requested), Is.True);
            }
            if (i % 2 == 0)
            {
                if (empty)
                {
                    ReplyEmpty(hash, requested);
                }
                else
                {
                    using CellsMessage72 reply = new(GetLastGetCellsRequestId(hash, requested), [hash.ValueHash256],
                        [[new byte[CkzgLib.Ckzg.BytesPerCell]]], partial.ToBytes());
                    HandleZeroMessage(reply, Eth72MessageCode.Cells);
                }
            }
            else
            {
                Advance(Timeouts.Eth);
                peer.MaintainSparseBlobState(requestClock.UtcNowOffset);
            }
            _session.Received(i == 7 ? 1 : 0).InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
            if (i == 3) Assert.That(peer.CanRequestCells, Is.False);
        }
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());

        void Advance(TimeSpan elapsed)
        {
            qualityClock.Advance(elapsed);
            requestClock.Add(elapsed);
        }
    }

    [Test]
    public void neutral_empty_replies_cannot_replace_silence_evidence([Values(0, 32)] int neutralReplies)
    {
        ManualTimeProvider clock = new();
        RecreateHandler(cellQualityClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        for (int failure = 0; failure < 8; failure++)
        {
            if (!peer.CanRequestCells) clock.Advance(TimeSpan.FromMinutes(1));
            Hash256 hash = HashFromInt(failure * 100);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_sparseBlobPoolPeerRegistry.RecordAnnouncement(peer, hash, mask), Is.True);
                Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
            }
            Hash256[] neutralHashes = Enumerable.Range(1, neutralReplies).Select(i => HashFromInt(failure * 100 + i)).ToArray();
            foreach (Hash256 neutralHash in neutralHashes)
                Assert.That(peer.TrySendGetCells(neutralHash, mask), Is.True);
            ReplyEmpty(hash, mask);
            foreach (Hash256 neutralHash in neutralHashes)
            {
                using CellsMessage72 reply = new(GetLastGetCellsRequestId(neutralHash, mask), [], [], mask.ToBytes());
                HandleZeroMessage(reply, Eth72MessageCode.Cells);
            }
            _session.Received(failure == 7 ? 1 : 0).InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
        }
    }

    [Test]
    public void actual_serving_budget_replies_are_neutral_to_the_requesting_handler([Values] bool usefulBodyArrivesLast)
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        using SparseBlobPoolPeerRegistry receivingRegistry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory(), timestamper: clock);
        RecreateHandler(sparseBlobPoolPeerRegistry: receivingRegistry, cellQualityClock: quality, requestClock: clock);
        ITxPool servingPool = Substitute.For<ITxPool>();
        using SparseBlobPoolPeerRegistry servingRegistry = CreateRegisteredRegistry(
            servingPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        HandleIncomingStatusMessage();
        Eth72ProtocolHandler receiving = _handler;
        Eth72ProtocolHandler serving = CreateCellServingPeer(servingRegistry, clock, 100, servingPool);
        _handler = receiving;
        Hash256 hash = HashFromInt(4000);
        Transaction transaction = new() { Type = TxType.Blob, Hash = hash, BlobVersionedHashes = [new byte[Hash256.Size]] };
        byte[][] cells = Enumerable.Range(0, BlobCellMask.CellCount).Select(_ => new byte[CkzgLib.Ckzg.BytesPerCell]).ToArray();
        SetupGetCellsResponse(servingPool, transaction, BlobCellMask.Full, BlobCellMask.Full, cells);
        ISparseBlobPoolPeer requester = (ISparseBlobPoolPeer)receiving;
        Transaction usefulBody = BuildSparseBlobTransaction(out BlobCellMask usefulMask, out byte[][] usefulCells);
        TestSparseBlobPeer bodyPeer = new(TestItem.PublicKeyC);
        receivingRegistry.AddPeer(bodyPeer);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        if (!usefulBodyArrivesLast) receivingRegistry.RecordTransaction(bodyPeer, usefulBody);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receivingRegistry.RecordAnnouncement(requester, usefulBody.Hash!, usefulMask), Is.True);
            Assert.That(requester.TrySendGetCells(usefulBody.Hash!, usefulMask), Is.True);
        }
        using CellsMessage72 usefulReply = new(GetLastGetCellsRequestId(usefulBody.Hash!, usefulMask),
            [usefulBody.Hash!.ValueHash256], [usefulCells], usefulMask.ToBytes());
        HandleZeroMessage(usefulReply, Eth72MessageCode.Cells);
        Assert.That(GetCellEvidence(receiving, quality), Is.EqualTo((0, usefulBodyArrivesLast ? 0 : 1)));
        int served = 0;
        int refused = 0;
        for (int i = 0; i < 24; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(receivingRegistry.RecordAnnouncement(requester, hash, BlobCellMask.Full), Is.True);
                Assert.That(requester.TrySendGetCells(hash, BlobCellMask.Full), Is.True);
            }
            GetCellsMessage72 request = _deliveredMessages.OfType<GetCellsMessage72>().Last();
            _handler = serving;
            try
            {
                HandleZeroMessage(request, Eth72MessageCode.GetCells);
            }
            finally
            {
                _handler = receiving;
            }
            CellsMessage72 reply = _deliveredMessages.OfType<CellsMessage72>().Last();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reply.RequestId, Is.EqualTo(request.RequestId));
                Assert.That(BlobCellMask.FromBytes(reply.CellMask), Is.EqualTo(BlobCellMask.Full));
            }
            if (reply.Hashes.Length == 0) refused++;
            else served++;
            HandleZeroMessage(reply, Eth72MessageCode.Cells);
            Assert.That(requester.CanRequestCells, Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(served, Is.EqualTo(12));
            Assert.That(refused, Is.EqualTo(12));
            Assert.That(GetCellEvidence(receiving, quality), Is.EqualTo((0, usefulBodyArrivesLast ? 0 : 1)));
        }
        if (usefulBodyArrivesLast) receivingRegistry.RecordTransaction(bodyPeer, usefulBody);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetCellEvidence(receiving, quality), Is.EqualTo((0, 1)));
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
            _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
        }
    }

    [Test]
    public void partial_announcers_are_not_penalized_for_serving_their_advertised_columns([Values] bool useSelection)
    {
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        BlobCellMask requested = BlobCellMask.FromIndices([4, 5, 6, 7]);
        BlobCellMask announced = BlobCellMask.FromIndices([4, 5]);
        for (int i = 0; i < 8; i++)
        {
            Hash256 hash = HashFromInt(3000 + i);
            Assert.That(_sparseBlobPoolPeerRegistry.RecordAnnouncement(peer, hash, announced), Is.True);
            BlobCellMask sentMask = useSelection ? announced : requested;
            if (useSelection)
                Assert.That(_sparseBlobPoolPeerRegistry.TryRequestCells(hash, requested, peer.Id), Is.False);
            else
                Assert.That(peer.TrySendGetCells(hash, requested), Is.True);
            Assert.That(BlobCellMask.FromBytes(_deliveredMessages.OfType<GetCellsMessage72>().Last().CellMask), Is.EqualTo(sentMask));
            using CellsMessage72 reply = new(GetLastGetCellsRequestId(hash, sentMask), [hash.ValueHash256],
                [[new byte[CkzgLib.Ckzg.BytesPerCell], new byte[CkzgLib.Ckzg.BytesPerCell]]], announced.ToBytes());
            HandleZeroMessage(reply, Eth72MessageCode.Cells);
            Assert.That(peer.CanRequestCells, Is.True);
        }
        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
        _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }

    [Test]
    public void cell_backoff_expires_at_one_minute([Values(59, 60)] int elapsedSeconds)
    {
        ManualTimeProvider clock = new();
        CellResponseHistory history = new();
        for (int i = 0; i < 4; i++) history.Record(HashFromInt(i).ValueHash256, useful: false, clock);
        Assert.That(history.IsBackedOff(clock), Is.True);
        clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        Assert.That(history.IsBackedOff(clock), Is.EqualTo(elapsedSeconds < 60));
    }

    [Test]
    public void cell_failure_and_useful_evidence_expire_at_five_minutes([Values(299, 300)] int elapsedSeconds)
    {
        ManualTimeProvider clock = new();
        CellResponseHistory history = new();
        history.Record(HashFromInt(0).ValueHash256, useful: true, clock);
        for (int i = 1; i <= 7; i++) history.Record(HashFromInt(i).ValueHash256, useful: false, clock);
        clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        (int failures, int useful) = history.Record(HashFromInt(8).ValueHash256, useful: false, clock);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Is.EqualTo(elapsedSeconds < 300 ? 8 : 1));
            Assert.That(useful, Is.EqualTo(elapsedSeconds < 300 ? 1 : 0));
        }
    }

    [Test]
    public void useful_cell_hash_deduplication_expires_at_one_minute([Values(59, 60)] int elapsedSeconds)
    {
        ManualTimeProvider clock = new();
        CellResponseHistory history = new();
        ValueHash256 hash = HashFromInt(0).ValueHash256;
        history.Record(hash, useful: true, clock);
        clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        (int failures, int useful) = history.Record(hash, useful: true, clock);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Is.Zero);
            Assert.That(useful, Is.EqualTo(elapsedSeconds < 60 ? 1 : 2));
        }
    }

    [Test]
    public void deferred_cell_credit_belongs_to_the_original_session([Values] bool replaceSession)
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        HandleIncomingStatusMessage();
        TestSparseBlobPeer bodyPeer = CreateSparseBlobPeers(registry, 1)[0];
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells, out _);
        ISparseBlobPoolPeer original = (ISparseBlobPoolPeer)_handler;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(original, transaction.Hash!, mask), Is.True);
            Assert.That(original.TrySendGetCells(transaction.Hash!, mask), Is.True);
        }
        using CellsMessage72 reply = new(GetLastGetCellsRequestId(transaction.Hash!, mask), [transaction.Hash!.ValueHash256], [cells], mask.ToBytes());
        HandleZeroMessage(reply, Eth72MessageCode.Cells);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            Assert.That(IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!), Is.True);
            if (replaceSession)
            {
                ISession previous = _session;
                Node remoteNode = previous.Node;
                _session = Substitute.For<ISession>();
                _session.Node.Returns(remoteNode);
                _session.InboundReceipts.Returns(new PacketReceiptTracker());
                _session.IsChannelWritable.Returns(true);
                _session.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(call =>
                {
                    P2PMessage message = call.Arg<P2PMessage>();
                    _deliveredMessages.Add(message);
                    message.AddTo(_disposables);
                });
                _session.DeliverMessage(Arg.Any<GetCellsMessage72>()).Returns(1);
                RecreateHandler(sparseBlobPoolPeerRegistry: registry);
                HandleIncomingStatusMessage();
                Assert.That(_session.InboundReceipts, Is.Not.SameAs(previous.InboundReceipts));
            }
            return AcceptTxResult.Accepted;
        });
        registry.RecordTransaction(bodyPeer, transaction);
        _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);

        ISparseBlobPoolPeer current = (ISparseBlobPoolPeer)_handler;
        for (int i = 0; i < 8; i++)
        {
            Hash256 hash = HashFromInt(100 + i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(current, hash, mask), Is.True);
                Assert.That(current.TrySendGetCells(hash, mask), Is.True);
            }
        }
        for (int i = 0; i < 8; i++) ReplyEmpty(HashFromInt(100 + i), mask);
        _session.Received(replaceSession ? 1 : 0).InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
    }

    [Test]
    public void parked_capacity_retries_wait_one_then_two_then_four_seconds()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        registry.RecordAnnouncement(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>()).Returns(true);
        registry.GetRequestMask(Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>(), Arg.Any<int>()).Returns(BlobCellMask.Full);
        registry.TryRequestCells(Arg.Any<ValueHash256>(), Arg.Any<BlobCellMask>(), Arg.Any<PublicKey>()).Returns(false);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        Hash256 hash = HashFromInt(1000);
        AnnounceBlobTransaction(hash, 1024, TxType.Blob);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        peer.MaintainSparseBlobState(now + TimeSpan.FromSeconds(1));
        registry.Received(2).TryRequestCells(hash, BlobCellMask.Full, _handler.Id);
        peer.MaintainSparseBlobState(now + TimeSpan.FromSeconds(2));
        registry.Received(2).TryRequestCells(hash, BlobCellMask.Full, _handler.Id);
        peer.MaintainSparseBlobState(now + TimeSpan.FromSeconds(3));
        registry.Received(3).TryRequestCells(hash, BlobCellMask.Full, _handler.Id);
        peer.MaintainSparseBlobState(now + TimeSpan.FromSeconds(6));
        registry.Received(3).TryRequestCells(hash, BlobCellMask.Full, _handler.Id);
        registry.TryRequestCells(hash, BlobCellMask.Full, _handler.Id).Returns(true);
        peer.MaintainSparseBlobState(now + TimeSpan.FromSeconds(7));
        registry.Received(4).TryRequestCells(hash, BlobCellMask.Full, _handler.Id);
        peer.MaintainSparseBlobState(now + TimeSpan.FromSeconds(11));
        registry.Received(4).TryRequestCells(hash, BlobCellMask.Full, _handler.Id);
    }

    [Test]
    public void cell_retry_does_not_request_cells_already_buffered_before_the_body()
    {
        TestSparseBlobPeer peer = CreateSparseBlobPeers(_sparseBlobPoolPeerRegistry, 1)[0];
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask received, out byte[][] cells, out _);
        BlobCellMask missing = BlobCellMask.FromIndices([5]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sparseBlobPoolPeerRegistry.RecordAnnouncement(peer, transaction.Hash!, received | missing), Is.True);
            Assert.That(_sparseBlobPoolPeerRegistry.RecordCells(peer, transaction.Hash!, received, cells), Is.True);
            Assert.That(_sparseBlobPoolPeerRegistry.TryRequestCells(transaction.Hash!, received | missing, peer.Id), Is.True);
            Assert.That(peer.CellRequests, Is.EqualTo(new[] { (transaction.Hash!, missing) }));
        }
    }

    [Test]
    public void cell_timeouts_during_local_channel_backpressure_are_neutral([Values] bool writable, [Values] bool decorated)
    {
        IChannel channel = Substitute.For<IChannel>();
        channel.IsWritable.Returns(true);
        Session session = new(30303, new Node(TestItem.PublicKeyB, "127.0.0.1", 30304), channel,
            NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        _session = decorated ? new ForwardingSession(session) : session;
        try
        {
            IProtocolHandler p2p = Substitute.For<IProtocolHandler>();
            p2p.ProtocolCode.Returns("p2p");
            p2p.MessageIdSpaceSize.Returns(16);
            session.AddProtocolHandler(p2p);
            session.Handshake(TestItem.PublicKeyB);
            IPacketSender sender = Substitute.For<IPacketSender>();
            sender.Enqueue(Arg.Any<P2PMessage>()).Returns(32);
            session.Init(5, Substitute.For<IChannelHandlerContext>(), sender);
            ManualTimestamper clock = new();
            RecreateHandler(requestClock: clock);
            HandleIncomingStatusMessage();
            ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
            BlobCellMask mask = BlobCellMask.FromIndices([4]);
            for (int i = 0; i < 8; i++)
            {
                Assert.That(peer.TrySendGetCells(HashFromInt(i), mask), Is.True);
                AnnounceBodyRequest(peer, HashFromInt(1000 + i));
                Assert.That(peer.TrySendPooledTransactionRequest(HashFromInt(1000 + i)), Is.True);
            }
            channel.IsWritable.Returns(writable);
            clock.Add(TimeSpan.FromSeconds(11));
            peer.MaintainSparseBlobState(clock.UtcNowOffset);

            Assert.That(session.IsClosing, Is.EqualTo(writable));
            if (!writable)
            {
                Assert.That(GetBodyEvidence(_handler, TimeProvider.System), Is.EqualTo((0, 0)));
                channel.IsWritable.Returns(true);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(peer.TrySendGetCells(HashFromInt(100), mask), Is.True);
                    Assert.That(peer.TrySendPooledTransactionRequest(HashFromInt(1100)), Is.True);
                }
            }
        }
        finally
        {
            session.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Local, "test cleanup");
        }
    }

    [TestCase(true, false)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    public void only_validated_useful_cells_credit_the_provider_window(bool supplyBody, bool bodyArrivesLast)
        => VerifyUsefulCellCredit(supplyBody, bodyArrivesLast);

    [Test]
    public void decorators_forward_verified_cell_credit_and_request_coverage([Values] bool forwardCredit)
        => VerifyUsefulCellCredit(supplyBody: true, bodyArrivesLast: false, decorate: true, forwardCredit);

    private void VerifyUsefulCellCredit(bool supplyBody, bool bodyArrivesLast, bool decorate = false, bool forwardCredit = true)
    {
        using SparseBlobPoolPeerRegistry registry = new(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        SparseRegistryDecorator? decorator = decorate ? new(registry, forwardCredit) : null;
        RecreateHandler(sparseBlobPoolPeerRegistry: decorator is null ? registry : decorator);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        if (decorate)
        {
            peer = decorator!.Resolve(peer);
        }
        TestSparseBlobPeer bodyPeer = CreateSparseBlobPeers(registry, 1)[0];
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        Transaction[] transactions = new Transaction[4];
        byte[][][] cells = new byte[4][][];
        for (int i = 0; i < 4; i++)
        {
            transactions[i] = BuildSparseBlobTransaction(out _, out cells[i], out _, (ulong)i);
            foreach (Hash256 hash in new[] { transactions[i].Hash!, HashFromInt(100 + i) })
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(registry.RecordAnnouncement(peer, hash, mask), Is.True);
                    Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
                }
            }
        }
        for (int i = 0; i < 4; i++)
        {
            Transaction transaction = transactions[i];
            if (supplyBody && !bodyArrivesLast) registry.RecordTransaction(bodyPeer, transaction);
            using CellsMessage72 reply = new(GetLastGetCellsRequestId(transaction.Hash!, mask), [transaction.Hash!.ValueHash256], [cells[i]], mask.ToBytes());
            HandleZeroMessage(reply, Eth72MessageCode.Cells);
            if (supplyBody && bodyArrivesLast) registry.RecordTransaction(bodyPeer, transaction);
            ReplyEmpty(HashFromInt(100 + i), mask);
        }

        Assert.That(peer.TrySendGetCells(HashFromInt(1000), mask), Is.EqualTo(supplyBody && (!decorate || forwardCredit)));
        _transactionPool.Received(supplyBody ? 4 : 0).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
    }

    [Test]
    public void zero_hash_replies_remove_announcements_without_self_restoration([Values] bool covered)
    {
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer alternative = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(600);
        BlobCellMask requested = BlobCellMask.FromIndices([4, 5]);
        BlobCellMask announced = covered ? requested : BlobCellMask.FromIndices([4]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, hash, announced), Is.True);
            Assert.That(registry.RecordAnnouncement(alternative, hash, requested), Is.True);
            Assert.That(peer.TrySendGetCells(hash, requested), Is.True);
        }
        using CellsMessage72 reply = new(GetLastGetCellsRequestId(hash, requested), [], [], requested.ToBytes());
        HandleZeroMessage(reply, Eth72MessageCode.Cells);
        Assert.That(alternative.CellRequests, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.CoversRequest(peer, hash.ValueHash256, announced), Is.False);
            Assert.That(alternative.CellRequests[0].CellMask, Is.EqualTo(requested));
            Assert.That(peer.CanRequestCells, Is.True);
        }
        clock.Add(TimeSpan.FromSeconds(5));
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        clock.Add(TimeSpan.FromSeconds(10));
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.CoversRequest(peer, hash.ValueHash256, announced), Is.False);
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(1));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public void admitted_sampler_retry_uses_remaining_honest_full_provider()
    {
        _blobCustodyTracker.Update(BlobCellMask.Empty);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        RecreateHandler(providerProbabilityPercent: 0, sparseBlobPoolPeerRegistry: registry);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer honest = CreateSparseBlobPeers(registry, 1)[0];
        Transaction body = BuildSparseBlobTransaction(out _, out _);
        _transactionPool.ValidateTxForBlobSampling(body).Returns(AcceptTxResult.Accepted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(honest, body), Is.Null);
            Assert.That(registry.RecordAnnouncement(peer, body.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(registry.RecordAnnouncement(honest, body.Hash!, BlobCellMask.Full), Is.True);
        }
        BlobCellMask requested = BlobCellMask.FromIndices([4]);
        Assert.That(peer.TrySendGetCells(body.Hash!, requested), Is.True);
        using CellsMessage72 reply = new(GetLastGetCellsRequestId(body.Hash!, requested), [], [], requested.ToBytes());
        HandleZeroMessage(reply, Eth72MessageCode.Cells);
        Assert.That(honest.CellRequests, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetFullProviderAnnouncementCount(body.Hash!), Is.EqualTo(1));
            Assert.That(honest.CellRequests[0].CellMask, Is.EqualTo(requested));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public void admitted_retry_does_not_admit_newly_merged_columns()
    {
        ManualTimestamper clock = new();
        BlobCellMask admitted = BlobCellMask.FromIndices([4]);
        BlobCellMask fresh = BlobCellMask.FromIndices([5]);
        _blobCustodyTracker.Update(fresh);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        RecreateHandler(providerProbabilityPercent: 0, sparseBlobPoolPeerRegistry: registry, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer honest = CreateSparseBlobPeers(registry, 1)[0];
        honest.CellRequestHandler = (_, _) => false;
        Transaction body = BuildSparseBlobTransaction(out _, out _);
        _transactionPool.ValidateTxForBlobSampling(body).Returns(AcceptTxResult.Accepted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(honest, body), Is.Null);
            Assert.That(registry.RecordAnnouncement(peer, body.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(registry.RecordAnnouncement(honest, body.Hash!, BlobCellMask.Full), Is.True);
            Assert.That(peer.TrySendGetCells(body.Hash!, admitted), Is.True);
        }
        using CellsMessage72 unavailable = new(GetLastGetCellsRequestId(body.Hash!, admitted), [], [], admitted.ToBytes());
        HandleZeroMessage(unavailable, Eth72MessageCode.Cells);
        using NewPooledTransactionHashesMessage72 announcement = new(
            [(byte)TxType.Blob], [body.GetElidedNetworkEncodingSize()], [body.Hash!.ValueHash256], fresh.ToBytes());
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.GetFullProviderAnnouncementCount(body.Hash!), Is.EqualTo(1));
            Assert.That(honest.CellRequests.Select(request => request.CellMask), Is.All.EqualTo(admitted));
        }
        honest.CellRequestHandler = (_, _) => true;
        clock.Add(TimeSpan.FromSeconds(6));
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        Assert.That(honest.CellRequests.Last().CellMask, Is.EqualTo(admitted));
        object pending = ((IDictionary)typeof(Eth72ProtocolHandler)
            .GetField("_pendingCellRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_handler)!)[body.Hash!.ValueHash256]!;
        Assert.That(pending.GetType().GetProperty("Mask")!.GetValue(pending), Is.EqualTo(fresh));
    }

    [Test]
    public void silent_body_backoff_excludes_buffered_cell_candidate()
        => VerifySilentBodySourceRecovery(bufferedCells: true);

    [Test]
    public void silent_body_backoff_excludes_announced_candidate()
        => VerifySilentBodySourceRecovery(bufferedCells: false);

    private void VerifySilentBodySourceRecovery(bool bufferedCells)
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.FromSeconds(1), timerFactory: timers, timestamper: clock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer silent = (ISparseBlobPoolPeer)_handler;
        BackOffBodyRequests(silent, clock, quality);
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        TestSparseBlobPeer bodySource = peers[0];
        TestSparseBlobPeer honest = peers[1];
        Transaction body = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] silentCells, out byte[][] allCells);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        Assert.That(registry.RecordTransaction(bodySource, body), Is.Null);
        if (bufferedCells)
        {
            BlobCellMask honestMask = BlobCellMask.FromIndices([5]);
            byte[][] honestCells = BlobCellsHelper.SelectFlattenedCells(allCells, BlobCellMask.Full, honestMask, body.BlobVersionedHashes!.Length);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordCells(silent, body.Hash!, mask, silentCells), Is.True);
                Assert.That(registry.RecordCells(honest, body.Hash!, honestMask, honestCells), Is.True);
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(silent, body.Hash!, mask), Is.True);
                Assert.That(registry.RecordAnnouncement(honest, body.Hash!, mask), Is.True);
            }
        }
        object retained = GetTrackedState(registry, body.Hash!);
        using (Assert.EnterMultipleScope())
        {
            _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
            Assert.That(retained.GetType().GetProperty("NotBefore")!.GetValue(retained), Is.GreaterThan(clock.UtcNowOffset));
            Assert.That(retained.GetType().GetProperty("Submitting")!.GetValue(retained), Is.False);
        }
        int requestsBefore = _deliveredMessages.OfType<GetPooledTransactionsMessage>().Count();
        registry.RemovePeer(bodySource);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { body.Hash! }));
            Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage>().Count(), Is.EqualTo(requestsBefore));
            Assert.That(silent.CanRequestTransactions, Is.False);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(honest, body), Is.Null);
            Assert.That(registry.RecordCells(honest, body.Hash!, mask, silentCells), Is.True);
        }
        clock.Add(TimeSpan.FromSeconds(1));
        quality.Advance(TimeSpan.FromSeconds(1));
        timers.Timer.Fire();
        using (Assert.EnterMultipleScope())
        {
            _transactionPool.Received(1).SubmitTx(Arg.Is<Transaction>(transaction => transaction.Hash == body.Hash), TxHandlingOptions.None);
            Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { body.Hash! }));
        }
    }

    [Test]
    public void silent_body_backoff_rejects_last_requested_terminal_fallback()
        => VerifyAmbiguousBodyFallback(bodySourceIsSilent: false);

    [Test]
    public void silent_body_backoff_rejects_direct_ambiguous_body_fallback()
        => VerifyAmbiguousBodyFallback(bodySourceIsSilent: true);

    private void VerifyAmbiguousBodyFallback(bool bodySourceIsSilent)
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        int selectedSilent = 0;
        SparseRegistryDecorator decorated = new(registry, true, beforeBodySend: () => selectedSilent++);
        RecreateHandler(sparseBlobPoolPeerRegistry: decorated, cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer silent = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        ISparseBlobPoolPeer originalBody = bodySourceIsSilent ? decorated.Resolve(silent) : peers[0];
        TestSparseBlobPeer badCells = peers[1];
        if (bodySourceIsSilent) BackOffBodyRequests(silent, clock, quality);
        Transaction body = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] validCells);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        int accepted = 0;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            if (valid) accepted++;
            return valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(originalBody, body.Hash!, mask), Is.True);
            Assert.That(registry.RecordAnnouncement(badCells, body.Hash!, mask), Is.True);
        }
        if (!bodySourceIsSilent) Assert.That(decorated.RecordAnnouncement(silent, body.Hash!, mask), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(originalBody, body), Is.Null);
            Assert.That(registry.RecordCells(badCells, body.Hash!, mask, CorruptCellValue(validCells)), Is.True);
            Assert.That(accepted, Is.Zero);
        }
        if (!bodySourceIsSilent)
        {
            Assert.That(selectedSilent, Is.EqualTo(1));
            registry.RemovePeer(originalBody);
            for (int i = 0; i < 4; i++)
            {
                clock.Add(Timeouts.Eth);
                quality.Advance(Timeouts.Eth);
                silent.MaintainSparseBlobState(clock.UtcNowOffset);
            }
        }
        clock.Add(TimeSpan.FromSeconds(5));
        quality.Advance(TimeSpan.FromSeconds(5));
        Assert.That(silent.CanRequestTransactions, Is.False);
        if (!bodySourceIsSilent) Assert.That(selectedSilent, Is.EqualTo(4));
        int selectionsBefore = selectedSilent;
        timers.Timer.Fire();
        Assert.That(selectedSilent, Is.EqualTo(selectionsBefore));
        if (bodySourceIsSilent) Assert.That(registry.RecordAnnouncement(originalBody, body.Hash!, mask), Is.False);
        TestSparseBlobPeer honest = new(TestItem.PublicKeyD);
        registry.AddPeer(honest);
        Assert.That(registry.RecordAnnouncement(honest, body.Hash!, mask), Is.True);
        clock.Add(TimeSpan.FromSeconds(bodySourceIsSilent ? 5 : 10));
        quality.Advance(TimeSpan.FromSeconds(bodySourceIsSilent ? 5 : 10));
        timers.Timer.Fire();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { body.Hash! }));
            Assert.That(registry.RecordTransaction(honest, body), Is.Null);
            Assert.That(registry.RecordCells(honest, body.Hash!, mask, validCells), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(1));
            Assert.That(selectedSilent, Is.EqualTo(selectionsBefore));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
    }

    private void BackOffBodyRequests(ISparseBlobPoolPeer peer, ManualTimestamper clock, ManualTimeProvider quality)
    {
        Hash256 hash = HashFromInt(930);
        AnnounceBodyRequest(peer, hash);
        Assert.That(peer.TrySendPooledTransactionRequest(hash), Is.True);
        for (int i = 0; i < 4; i++)
        {
            clock.Add(Timeouts.Eth);
            quality.Advance(Timeouts.Eth);
            peer.MaintainSparseBlobState(clock.UtcNowOffset);
            peer.MaintainSparseBlobState(clock.UtcNowOffset);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((i + 1, 0)));
                Assert.That(peer.CanRequestTransactions, Is.EqualTo(i < 3));
            }
        }
    }

    [Test]
    public void ordinary_pooled_requests_do_not_change_sparse_body_quality()
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        RecreateHandler(cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        for (int i = 0; i < 4; i++) _handler.HandleMessages([HashFromInt(970 + i).ValueHash256]);
        clock.Add(Timeouts.Eth);
        quality.Advance(Timeouts.Eth);
        ((ISparseBlobPoolPeer)_handler).MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((0, 0)));
            Assert.That(((ISparseBlobPoolPeer)_handler).CanRequestTransactions, Is.True);
            Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage>().Count(), Is.EqualTo(4));
        }
    }

    [Test]
    public void genuine_body_silence_records_once_and_backs_off_on_fourth_failure()
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        RecreateHandler(cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        BackOffBodyRequests(peer, clock, quality);
        Assert.That(peer.TrySendPooledTransactionRequest(HashFromInt(904)), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage>().Count(), Is.EqualTo(4));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public void locally_refused_body_requests_do_not_create_silence_samples()
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        RecreateHandler(cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(0);
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        for (int i = 0; i < 4; i++)
        {
            AnnounceBodyRequest(peer, HashFromInt(910 + i));
            Assert.That(peer.TrySendPooledTransactionRequest(HashFromInt(910 + i)), Is.False);
        }
        clock.Add(Timeouts.Eth);
        quality.Advance(Timeouts.Eth);
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((0, 0)));
            Assert.That(peer.CanRequestTransactions, Is.True);
        }
    }

    [Test]
    public void body_received_before_local_processing_is_not_silence([Values] bool dropLocally)
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        IBackgroundTaskScheduler scheduler = dropLocally ? new RejectingBackgroundTaskScheduler() : RunImmediatelyScheduler.Instance;
        RecreateHandler(backgroundTaskScheduler: scheduler, cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Transaction body = BuildSparseBlobTransaction(out _, out _);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        for (int i = 0; i < 40; i++)
        {
            Hash256 hash = body.Hash!;
            AnnounceBodyRequest(peer, hash);
            Assert.That(peer.TrySendPooledTransactionRequest(hash), Is.True);
            using PooledTransactionsMessage66 reply = new(GetLastGetPooledTransactionsRequestId(hash), new PooledTransactionsMessage65(new[] { body }.ToPooledList()));
            HandleZeroMessage(reply, Eth66MessageCode.PooledTransactions);
        }
        Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage>().Count(), Is.EqualTo(40));
        clock.Add(Timeouts.Eth);
        quality.Advance(Timeouts.Eth);
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((0, dropLocally ? 0 : 1)));
            Assert.That(peer.CanRequestTransactions, Is.True);
        }
    }

    [Test]
    public void empty_body_coannouncer_cannot_absorb_honest_fallback_retries()
        => VerifyOmittedBodyRotation(partial: false);

    [Test]
    public void partial_body_reply_rotates_only_missing_hashes()
        => VerifyOmittedBodyRotation(partial: true);

    private void VerifyOmittedBodyRotation(bool partial)
    {
        ManualTimestamper clock = new();
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer source = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer honest = CreateSparseBlobPeers(registry, 1)[0];
        Transaction first = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] firstCells, out _, 100);
        Transaction second = BuildSparseBlobTransaction(out _, out byte[][] secondCells, out _, 101);
        Transaction[] bodies = partial ? [second, first] : [first];
        foreach (Transaction body in bodies)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(source, body.Hash!, mask), Is.True);
                Assert.That(registry.RecordAnnouncement(honest, body.Hash!, mask), Is.True);
            }
        }
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        _handler.HandleMessages(bodies.Select(body => body.Hash!.ValueHash256).ToArray());
        using PooledTransactionsMessage66 reply = new(GetLastGetPooledTransactionsRequestId(first.Hash!),
            new PooledTransactionsMessage65(partial ? new[] { first }.ToPooledList() : IOwnedReadOnlyList<Transaction>.Empty));
        HandleZeroMessage(reply, Eth66MessageCode.PooledTransactions);
        Transaction missing = partial ? second : first;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { missing.Hash! }));
            Assert.That(registry.CoversRequest(source, missing.Hash!.ValueHash256, mask), Is.False);
            if (partial) Assert.That(registry.CoversRequest(source, first.Hash!.ValueHash256, mask), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(source, missing.Hash!, mask), Is.True);
            Assert.That(registry.RecordTransaction(honest, missing), Is.Null);
            Assert.That(registry.RecordCells(honest, missing.Hash!, mask, partial ? secondCells : firstCells), Is.True);
        }
        clock.Add(TimeSpan.FromSeconds(10));
        timers.Timer.Fire();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { missing.Hash! }));
            _transactionPool.Received(1).SubmitTx(Arg.Is<Transaction>(body => body.Hash == missing.Hash), TxHandlingOptions.None);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public void body_omissions_preserve_replacement_state_and_newer_generation([Values] bool partial, [Values(0, 1, 2)] int transition)
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(_transactionPool, _blobCustodyTracker,
            RunImmediatelyScheduler.Instance, LimboLogs.Instance, timerFactory: new ManualTimerFactory(), timestamper: clock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer source = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer honest = CreateSparseBlobPeers(registry, 1)[0];
        Transaction first = BuildSparseBlobTransaction(out BlobCellMask mask, out _, out _, 7093);
        Transaction second = BuildSparseBlobTransaction(out _, out _, out _, 7094);
        Transaction[] bodies = partial ? [second, first] : [first];
        foreach (Transaction body in bodies)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(source, body.Hash!, BlobCellMask.Full), Is.True);
                Assert.That(registry.RecordAnnouncement(honest, body.Hash!, mask), Is.True);
            }
        }
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _handler.HandleMessages(bodies.Select(body => body.Hash!.ValueHash256).ToArray());
        long originalId = GetLastGetPooledTransactionsRequestId(first.Hash!);
        Transaction missing = partial ? second : first;
        object originalState = GetTrackedState(registry, missing.Hash!);
        if (transition == 1)
        {
            registry.Clear(missing.Hash!);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(source, missing.Hash!, BlobCellMask.Full), Is.True);
                Assert.That(registry.RecordAnnouncement(honest, missing.Hash!, mask), Is.True);
            }
        }
        else if (transition == 2)
        {
            Assert.That(source.TrySendPooledTransactionRequest(missing.Hash!), Is.True);
            long newerId = GetLastGetPooledTransactionsRequestId(missing.Hash!);
            Assert.That(newerId, Is.Not.EqualTo(originalId));
            _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(false);
            using PooledTransactionsMessage66 newerReply = new(newerId, new PooledTransactionsMessage65(IOwnedReadOnlyList<Transaction>.Empty));
            HandleZeroMessage(newerReply, Eth66MessageCode.PooledTransactions);
            _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
            Assert.That(GetTrackedState(registry, missing.Hash!).GetType().GetProperty("CurrentBodyAttempt")!
                .GetValue(GetTrackedState(registry, missing.Hash!)), Is.Null);
        }
        object currentState = GetTrackedState(registry, missing.Hash!);
        AnnouncementRestoration? ticket;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryBeginAnnouncementRestore(source, missing.Hash!, out ticket), Is.True);
            Assert.That(ticket, Is.Not.Null);
            Assert.That(registry.RecordAnnouncement(source, missing.Hash!, BlobCellMask.Full), Is.True);
        }
        IOwnedReadOnlyList<Transaction> included = partial ? new[] { first }.ToPooledList() : IOwnedReadOnlyList<Transaction>.Empty;
        using PooledTransactionsMessage66 oldReply = new(originalId, new PooledTransactionsMessage65(included));
        HandleZeroMessage(oldReply, Eth66MessageCode.PooledTransactions);
        bool stale = transition != 0;
        ISet<PublicKey> unavailable = (ISet<PublicKey>)currentState.GetType().GetProperty("BodyUnavailableSources")!.GetValue(currentState)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReferenceEquals(currentState, originalState), Is.EqualTo(transition != 1));
            Assert.That(ticket!.IsValid, Is.EqualTo(stale));
            Assert.That(registry.CoversRequest(source, missing.Hash!.ValueHash256, BlobCellMask.Full), Is.EqualTo(stale));
            Assert.That(unavailable.Contains(source.Id), Is.EqualTo(!stale));
            Assert.That(source.CanRequestTransactions, Is.True);
            Assert.That(GetBodyEvidence(_handler, quality).Failures, Is.Zero);
            if (partial) Assert.That(registry.HasRecordedTransaction(first.Hash!), Is.True);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        int sourceRequests = _deliveredMessages.OfType<GetPooledTransactionsMessage>().Count();
        registry.RecordBodyUnavailable(honest, missing.Hash!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage>().Count(), Is.EqualTo(sourceRequests + (stale ? 1 : 0)));
            Assert.That(ticket!.IsValid, Is.EqualTo(stale));
            Assert.That(registry.TryRestoreAnnouncement(source, missing.Hash!, ticket), Is.EqualTo(stale));
        }
    }

    [Test]
    public void completed_body_omission_ownership_cannot_consume_fresh_ticket_twice()
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(_transactionPool, _blobCustodyTracker,
            RunImmediatelyScheduler.Instance, LimboLogs.Instance, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer source = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(7095);
        Assert.That(registry.RecordAnnouncement(source, hash, BlobCellMask.Full), Is.True);
        BodyRequestAttempt attempt = registry.CreateBodyRequestAttempt(source, hash)!;
        attempt.ConfirmSent();
        attempt.Complete(unanswered: false);
        registry.RecordBodyUnavailable(source, hash, attempt);
        AnnouncementRestoration? fresh;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.CoversRequest(source, hash.ValueHash256, BlobCellMask.Full), Is.False);
            Assert.That(registry.RecordAnnouncement(source, hash, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryBeginAnnouncementRestore(source, hash, out fresh), Is.True);
            Assert.That(registry.RecordAnnouncement(source, hash, BlobCellMask.Full), Is.True);
        }
        registry.RecordBodyUnavailable(source, hash, attempt);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fresh!.IsValid, Is.True);
            Assert.That(registry.CoversRequest(source, hash.ValueHash256, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryRestoreAnnouncement(source, hash, fresh), Is.True);
        }
    }

    [Test]
    public void body_omission_received_before_send_confirmation_keeps_original_ownership()
    {
        ManualTimeProvider quality = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(_transactionPool, _blobCustodyTracker,
            RunImmediatelyScheduler.Instance, LimboLogs.Instance, timerFactory: new ManualTimerFactory());
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer source = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer honest = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(7096);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        AnnouncementRestoration? ticket;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(source, hash, BlobCellMask.Full), Is.True);
            Assert.That(registry.RecordAnnouncement(honest, hash, mask), Is.True);
            Assert.That(registry.TryBeginAnnouncementRestore(source, hash, out ticket), Is.True);
            Assert.That(registry.RecordAnnouncement(source, hash, BlobCellMask.Full), Is.True);
        }
        _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(call =>
        {
            GetPooledTransactionsMessage66 request = (GetPooledTransactionsMessage66)call.Arg<GetPooledTransactionsMessage>();
            using PooledTransactionsMessage66 reply = new(request.RequestId, new PooledTransactionsMessage65(IOwnedReadOnlyList<Transaction>.Empty));
            HandleZeroMessage(reply, Eth66MessageCode.PooledTransactions);
            return 32;
        });
        Assert.That(source.TrySendPooledTransactionRequest(hash), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ticket!.IsValid, Is.False);
            Assert.That(registry.CoversRequest(source, hash.ValueHash256, BlobCellMask.Full), Is.False);
            Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { hash }));
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((0, 0)));
            Assert.That(source.CanRequestTransactions, Is.True);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    // Reusing an excluded body source requires 65 identities to turn over a 64-entry per-hash history.
    public void body_omission_accepted_residual_requires_identity_turnover([Values(64, 65)] int identities)
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        Transaction body = BuildSparseBlobTransaction(out BlobCellMask mask, out _);
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, identities);
        foreach (TestSparseBlobPeer peer in peers)
        {
            Assert.That(registry.RecordAnnouncement(peer, body.Hash!, mask), Is.True);
            registry.RecordBodyUnavailable(peer, body.Hash!);
        }
        object tracked = GetTrackedState(registry, body.Hash!);
        Assert.That(tracked.GetType().GetProperty("BodyUnavailableSources")!.GetValue(tracked), Has.Count.EqualTo(64));
        foreach (TestSparseBlobPeer peer in peers) Assert.That(registry.RecordAnnouncement(peer, body.Hash!, mask), Is.True);
        TestSparseBlobPeer originalBody = new(TestItem.PublicKeyD);
        registry.AddPeer(originalBody);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        Assert.That(registry.RecordTransaction(originalBody, body), Is.Null);
        registry.RemovePeer(originalBody);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers[0].TransactionRequests, Has.Count.EqualTo(identities == 65 ? 1 : 0));
            Assert.That(peers.Skip(1).SelectMany(peer => peer.TransactionRequests), Is.Empty);
        }
    }

    [Test]
    public void ontime_body_reply_waiting_before_network_handler_is_not_silence()
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        RecreateHandler(cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        PacketReceiptTracker receipts = _session.InboundReceipts;
        for (int i = 0; i < 4; i++)
        {
            Transaction body = BuildSparseBlobTransaction(out _, out _, out _, (ulong)(200 + i));
            AnnounceBodyRequest(peer, body.Hash!);
            Assert.That(peer.TrySendPooledTransactionRequest(body.Hash!), Is.True);
            using PooledTransactionsMessage66 reply = new(GetLastGetPooledTransactionsRequestId(body.Hash!), new PooledTransactionsMessage65(new[] { body }.ToPooledList()));
            using DisposableByteBuffer encoded = _svc.ZeroSerialize(reply).AsDisposable();
            encoded.ReadByte();
            NettyBufferPacket packet = new(encoded) { PacketType = Eth66MessageCode.PooledTransactions };
            receipts.Stamp(packet);
            clock.Add(Timeouts.Eth + TimeSpan.FromSeconds(1));
            quality.Advance(Timeouts.Eth + TimeSpan.FromSeconds(1));
            peer.MaintainSparseBlobState(clock.UtcNowOffset);
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((0, i)));
            try
            {
                _handler.HandleMessage(packet);
            }
            finally
            {
                receipts.Complete(packet.ReceiptSequence);
            }
            peer.MaintainSparseBlobState(clock.UtcNowOffset);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((0, i + 1)));
                Assert.That(peer.CanRequestTransactions, Is.True);
            }
        }
        _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public void earlier_body_receipt_preserves_newer_registry_attempt([Values] bool removePeer)
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer honest = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(960);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, hash, mask), Is.True);
            Assert.That(registry.RecordAnnouncement(honest, hash, mask), Is.True);
            Assert.That(peer.TrySendPooledTransactionRequest(hash), Is.True);
        }
        long earlier = GetLastGetPooledTransactionsRequestId(hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.TrySendPooledTransactionRequest(hash), Is.True);
            Assert.That(GetLastGetPooledTransactionsRequestId(hash), Is.Not.EqualTo(earlier));
        }
        object state = GetTrackedState(registry, hash);
        object? current = state.GetType().GetProperty("CurrentBodyAttempt")!.GetValue(state);
        Assert.That(current, Is.Not.Null);
        using PooledTransactionsMessage66 reply = new(earlier, new PooledTransactionsMessage65(IOwnedReadOnlyList<Transaction>.Empty));
        HandleZeroMessage(reply, Eth66MessageCode.PooledTransactions);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetType().GetProperty("CurrentBodyAttempt")!.GetValue(state), Is.SameAs(current));
            Assert.That(honest.TransactionRequests, Is.Empty);
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((0, 0)));
        }
        AssertRestorationUsage(registry, peer.Id, hash, 1, 2, 0);
        if (removePeer)
        {
            registry.RemovePeer(peer);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(state.GetType().GetProperty("CurrentBodyAttempt")!.GetValue(state), Is.Null);
                Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { hash }));
                IDictionary usage = (IDictionary)typeof(SparseBlobPoolPeerRegistry)
                    .GetField("_peerUsage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
                Assert.That(usage.Contains(peer.Id), Is.False);
            }
            return;
        }
        clock.Add(Timeouts.Eth);
        quality.Advance(Timeouts.Eth);
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { hash }));
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((1, 0)));
        }
    }

    [Test]
    public void late_old_body_reply_cannot_retire_newer_attempt()
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        RecreateHandler(cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Hash256 hash = HashFromInt(920);
        ISparseBlobPoolPeerRegistry registry = AnnounceBodyRequest(peer, hash);
        Assert.That(peer.TrySendPooledTransactionRequest(hash), Is.True);
        long expiredId = GetLastGetPooledTransactionsRequestId(hash);
        registry.RecordBodyUnavailable(peer, hash);
        clock.Add(Timeouts.Eth);
        quality.Advance(Timeouts.Eth);
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((1, 0)));
            Assert.That(peer.TrySendPooledTransactionRequest(hash), Is.True);
            Assert.That(GetLastGetPooledTransactionsRequestId(hash), Is.Not.EqualTo(expiredId));
        }
        using PooledTransactionsMessage66 oldReply = new(expiredId, new PooledTransactionsMessage65(IOwnedReadOnlyList<Transaction>.Empty));
        HandleZeroMessage(oldReply, Eth66MessageCode.PooledTransactions);
        Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((1, 0)));
        clock.Add(Timeouts.Eth);
        quality.Advance(Timeouts.Eth);
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((2, 0)));
    }

    [Test]
    public void body_receipt_before_send_returns_retires_the_original_attempt()
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        RecreateHandler(cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        Transaction body = BuildSparseBlobTransaction(out _, out _);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        int receipts = 0;
        _session.DeliverMessage(Arg.Any<GetPooledTransactionsMessage>()).Returns(call =>
        {
            GetPooledTransactionsMessage66 outgoing = (GetPooledTransactionsMessage66)call.Arg<GetPooledTransactionsMessage>();
            using PooledTransactionsMessage66 reply = new(outgoing.RequestId, new PooledTransactionsMessage65(new[] { body }.ToPooledList()));
            HandleZeroMessage(reply, Eth66MessageCode.PooledTransactions);
            receipts++;
            return 32;
        });
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        AnnounceBodyRequest(peer, body.Hash!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.TrySendPooledTransactionRequest(body.Hash!), Is.True);
            Assert.That(receipts, Is.EqualTo(1));
        }
        clock.Add(Timeouts.Eth);
        quality.Advance(Timeouts.Eth);
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((0, 1)));
            Assert.That(peer.CanRequestTransactions, Is.True);
        }
    }

    private ISparseBlobPoolPeerRegistry AnnounceBodyRequest(ISparseBlobPoolPeer peer, Hash256 hash)
    {
        ISparseBlobPoolPeerRegistry registry = (ISparseBlobPoolPeerRegistry)typeof(Eth72ProtocolHandler)
            .GetField("_sparseBlobPoolPeerRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_handler)!;
        Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.FromIndices([4])), Is.True);
        return registry;
    }

    private static (int Failures, int Useful) GetBodyEvidence(Eth72ProtocolHandler handler, TimeProvider clock)
    {
        CellResponseHistory history = (CellResponseHistory)typeof(Eth72ProtocolHandler)
            .GetField("_bodyResponses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
        return ((int, int))typeof(CellResponseHistory).GetMethod("Count", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(history, [clock, TimeSpan.FromMinutes(1)])!;
    }

    [Test]
    public async Task delayed_restore_producer_cannot_replace_newer_ownership([Values(0, 1, 2, 3)] int newerDisposition)
    {
        bool newerTemporaryMiss = newerDisposition == 1;
        ManualTimestamper clock = new();
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        using ManualResetEventSlim producerEntered = new();
        using ManualResetEventSlim releaseProducer = new();
        AnnouncementRestoration? first = null;
        AnnouncementRestoration? second = null;
        int producers = 0;
        SparseRegistryDecorator decorated = new(registry, true, afterRestoreCreated: ticket =>
        {
            if (Interlocked.Increment(ref producers) == 1)
            {
                first = ticket;
                producerEntered.Set();
                if (!releaseProducer.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            else second = ticket;
        });
        RecreateHandler(sparseBlobPoolPeerRegistry: decorated, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Hash256 hash = HashFromInt(710);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
            Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
        }
        long originalId = GetLastGetCellsRequestId(hash, mask);
        Task originalReply = Task.Run(() =>
        {
            using CellsMessage72 reply = new(originalId, [], [], BlobCellMask.Empty.ToBytes());
            HandleZeroMessage(reply, Eth72MessageCode.Cells);
        });
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(producerEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(first, Is.Not.Null);
                Assert.That(decorated.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
                Assert.That(newerDisposition == 3
                    ? decorated.TryRequestCells(hash, mask, peer.Id)
                    : peer.TrySendGetCells(hash, mask), Is.True);
            }
            if (newerDisposition == 3)
            {
                object state = GetTrackedState(registry, hash);
                IDictionary inFlight = (IDictionary)state.GetType().GetProperty("InFlightByPeer")!.GetValue(state)!;
                Assert.That(inFlight[peer.Id], Is.EqualTo(mask));
            }
            long newerId = GetLastGetCellsRequestId(hash, mask);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(newerId, Is.Not.EqualTo(originalId));
                Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(2));
            }
            if (newerDisposition >= 2)
            {
                clock.Add(Timeouts.Eth);
                if (newerDisposition == 2) peer.MaintainSparseBlobState(clock.UtcNowOffset);
                else Assert.That(registry.TryRequestCells(hash, mask, peer.Id), Is.False);
            }
            else
            {
                using CellsMessage72 newerReply = new(newerId, [], [], (newerTemporaryMiss ? BlobCellMask.Empty : mask).ToBytes());
                HandleZeroMessage(newerReply, Eth72MessageCode.Cells);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(first!.IsValid, Is.False);
                if (newerTemporaryMiss) Assert.That(second, Is.Not.Null);
                else Assert.That(second, Is.Null);
            }
        }
        finally
        {
            releaseProducer.Set();
            await originalReply.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.That(GetPendingRestoration(_handler, hash), Is.SameAs(newerTemporaryMiss ? second : null));
        clock.Add(TimeSpan.FromSeconds(6));
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.CoversRequest(peer, hash.ValueHash256, BlobCellMask.Full), Is.EqualTo(newerTemporaryMiss));
            Assert.That(GetPendingRestoration(_handler, hash), Is.Null);
        }
    }

    [Test]
    public async Task captured_restore_cannot_revive_a_permanently_removed_announcement()
    {
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        using ManualResetEventSlim restoreEntered = new();
        using ManualResetEventSlim releaseRestore = new();
        SparseRegistryDecorator decorated = new(registry, true, beforeRestore: _ =>
        {
            restoreEntered.Set();
            if (!releaseRestore.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        });
        RecreateHandler(sparseBlobPoolPeerRegistry: decorated, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Hash256 hash = HashFromInt(711);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
            Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
        }
        ReplyEmpty(hash, mask);
        clock.Add(TimeSpan.FromSeconds(6));
        Task restore = Task.Run(() => peer.MaintainSparseBlobState(clock.UtcNowOffset));
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(restoreEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(decorated.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
                Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
                Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(2));
            }
            using CellsMessage72 unavailable = new(GetLastGetCellsRequestId(hash, mask), [], [], mask.ToBytes());
            HandleZeroMessage(unavailable, Eth72MessageCode.Cells);
        }
        finally
        {
            releaseRestore.Set();
            await restore.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.CoversRequest(peer, hash.ValueHash256, BlobCellMask.Full), Is.False);
            Assert.That(GetPendingRestoration(_handler, hash), Is.Null);
            Assert.That(_deliveredMessages.OfType<GetCellsMessage72>().Count(), Is.EqualTo(2));
        }
    }

    [Test]
    public async Task older_restore_clear_preserves_newer_registry_ticket([Values] bool permanentClear)
    {
        ManualTimestamper clock = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: clock);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int stops = 0;
        void Pause(AnnouncementRestoration? _)
        {
            if (Interlocked.Increment(ref stops) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }
        SparseRegistryDecorator decorated = new(registry, true,
            beforeRestore: permanentClear ? null : Pause,
            afterPermanentRemove: permanentClear ? Pause : null);
        RecreateHandler(sparseBlobPoolPeerRegistry: decorated, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Hash256 hash = HashFromInt(712);
        BlobCellMask oldMask = BlobCellMask.FromIndices([4]);
        BlobCellMask newMask = BlobCellMask.FromIndices([5]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.RecordAnnouncement(peer, hash, oldMask), Is.True);
            Assert.That(peer.TrySendGetCells(hash, oldMask), Is.True);
        }
        ReplyEmpty(hash, oldMask);
        AnnouncementRestoration? first = GetPendingRestoration(_handler, hash);
        Assert.That(first, Is.Not.Null);
        Task oldContinuation;
        if (permanentClear)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decorated.RecordAnnouncement(peer, hash, oldMask), Is.True);
                Assert.That(peer.TrySendGetCells(hash, oldMask), Is.True);
            }
            long requestId = GetLastGetCellsRequestId(hash, oldMask);
            oldContinuation = Task.Run(() =>
            {
                using CellsMessage72 unavailable = new(requestId, [], [], oldMask.ToBytes());
                HandleZeroMessage(unavailable, Eth72MessageCode.Cells);
            });
        }
        else
        {
            clock.Add(TimeSpan.FromSeconds(6));
            oldContinuation = Task.Run(() => peer.MaintainSparseBlobState(clock.UtcNowOffset));
        }
        AnnouncementRestoration? second = null;
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(decorated.RecordAnnouncement(peer, hash, newMask), Is.True);
                Assert.That(peer.TrySendGetCells(hash, newMask), Is.True);
            }
            ReplyEmpty(hash, newMask);
            second = GetPendingRestoration(_handler, hash);
            Assert.That(second, Is.Not.Null.And.Not.SameAs(first));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(first!.IsValid, Is.False);
                AssertRestorationUsage(registry, peer.Id, hash, 0, 1, 1);
            }
        }
        finally
        {
            release.Set();
            await oldContinuation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.That(GetPendingRestoration(_handler, hash), Is.SameAs(second));
        _session.DeliverMessage(Arg.Any<GetCellsMessage72>()).Returns(0);
        clock.Add(TimeSpan.FromSeconds(6));
        peer.MaintainSparseBlobState(clock.UtcNowOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.CoversRequest(decorated.Resolve(peer), hash.ValueHash256, newMask), Is.True);
            Assert.That(registry.CoversRequest(decorated.Resolve(peer), hash.ValueHash256, oldMask), Is.False);
            Assert.That(GetPendingRestoration(_handler, hash), Is.Null);
            AssertRestorationUsage(registry, peer.Id, hash, 1, 1, 0);
        }
        decorated.RemovePeer(peer);
        AssertRestorationUsage(registry, peer.Id, hash, 0, 0, 0);
    }

    [Test]
    public void removing_one_ticket_owner_preserves_the_other_peer()
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        Hash256 hash = HashFromInt(713);
        BlobCellMask firstMask = BlobCellMask.FromIndices([4]);
        BlobCellMask secondMask = BlobCellMask.FromIndices([5]);
        AnnouncementRestoration? second;
        AnnouncementRestoration? first;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peers[0], hash, firstMask), Is.True);
            Assert.That(registry.RecordAnnouncement(peers[1], hash, secondMask), Is.True);
            Assert.That(registry.TryBeginAnnouncementRestore(peers[0], hash, out first), Is.True);
            Assert.That(registry.TryBeginAnnouncementRestore(peers[1], hash, out second), Is.True);
        }
        registry.RemovePeer(peers[1]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second!.IsValid, Is.False);
            Assert.That(first!.IsValid, Is.True);
            AssertRestorationUsage(registry, peers[0].Id, hash, 0, 1, 1);
        }
        Assert.That(registry.TryRestoreAnnouncement(peers[0], hash, first), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.CoversRequest(peers[0], hash.ValueHash256, firstMask), Is.True);
            AssertRestorationUsage(registry, peers[0].Id, hash, 1, 1, 0);
        }
        registry.RemovePeer(peers[0]);
        AssertRestorationUsage(registry, peers[0].Id, hash, 0, 0, 0);
    }

    [Test]
    public void throwing_body_retry_retires_every_hash_in_the_request([Values(0, 1, 2)] int faultPath)
    {
        bool qualityClockFailure = faultPath == 1;
        bool registryClockFailure = faultPath == 2;
        ManualTimeProvider time = new();
        OneShotQualityClock quality = new(time);
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        ManualTimerFactory timers = new();
        ITimestamper registryClock = Substitute.For<ITimestamper>();
        bool throwRegistryTime = false;
        int registryFaults = 0;
        registryClock.UtcNowOffset.Returns(_ =>
        {
            if (throwRegistryTime)
            {
                throwRegistryTime = false;
                registryFaults++;
                throw new InvalidOperationException("Local body retry clock failed.");
            }
            return clock.UtcNowOffset;
        });
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: registryClock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer honest = CreateSparseBlobPeers(registry, 1)[0];
        Transaction[] bodies = [BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] firstCells, out _, 414),
            BuildSparseBlobTransaction(out _, out byte[][] secondCells, out _, 415)];
        byte[][][] cells = [firstCells, secondCells];
        foreach (Transaction body in bodies)
        {
            AnnounceBodyRequest(peer, body.Hash!);
            Assert.That(registry.RecordAnnouncement(honest, body.Hash!, mask), Is.True);
        }
        _handler.HandleMessages(bodies.Select(body => body.Hash!.ValueHash256).ToArray());
        if (faultPath == 0)
            honest.TransactionRequestHandler = _ => throw new InvalidOperationException("Local send failed.");
        clock.Add(Timeouts.Eth);
        time.Advance(Timeouts.Eth);
        quality.ThrowNext = qualityClockFailure;
        throwRegistryTime = registryClockFailure;
        Assert.DoesNotThrow(() => peer.MaintainSparseBlobState(clock.UtcNowOffset));
        foreach (Transaction body in bodies)
        {
            object state = GetTrackedState(registry, body.Hash!);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(state.GetType().GetProperty("CurrentBodyAttempt")!.GetValue(state), Is.Null);
                AssertRestorationUsage(registry, peer.Id, body.Hash!, 2, 1, 0);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.TransactionRequests, Is.EqualTo(registryClockFailure
                ? new[] { bodies[1].Hash! } : bodies.Select(body => body.Hash!).ToArray()));
            Assert.That(registryFaults, Is.EqualTo(registryClockFailure ? 1 : 0));
            Assert.That(quality.Faults, Is.EqualTo(qualityClockFailure ? 1 : 0));
            Assert.That(GetBodyEvidence(_handler, quality), Is.EqualTo((qualityClockFailure ? 0 : 1, 0)));
        }
        object requests = typeof(Eth72ProtocolHandler)
            .GetField("_sentPooledTransactionRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_handler)!;
        IDictionary snapshots = (IDictionary)requests.GetType()
            .GetField("_indices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(requests)!;
        Assert.That(snapshots.Count, Is.Zero);
        BackgroundTaskSchedulerWrapper scheduler = (BackgroundTaskSchedulerWrapper)typeof(ProtocolHandlerBase)
            .GetProperty("BackgroundTaskScheduler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_handler)!;
        List<PeerWorkQuota.Reservation> recovered = [];
        try
        {
            for (int i = 0; i < 32; i++)
            {
                Assert.That(scheduler.TryReservePooledReply(out PeerWorkQuota.Reservation? reservation), Is.True);
                recovered.Add(reservation!);
            }
            Assert.That(scheduler.TryReservePooledReply(out _), Is.False);
        }
        finally { foreach (PeerWorkQuota.Reservation reservation in recovered) reservation.Dispose(); }
        honest.TransactionRequestHandler = null;
        if (faultPath == 0)
        {
            clock.Add(Timeouts.Eth);
            time.Advance(Timeouts.Eth);
        }
        if (faultPath != 1) timers.Timer.Fire();
        Assert.That(honest.TransactionRequests, Is.EquivalentTo(faultPath switch
        {
            0 => new[] { bodies[0].Hash!, bodies[1].Hash!, bodies[0].Hash!, bodies[1].Hash! },
            2 => new[] { bodies[1].Hash!, bodies[0].Hash! },
            _ => bodies.Select(body => body.Hash!).ToArray()
        }));
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        for (int i = 0; i < bodies.Length; i++)
        {
            registry.RecordTransaction(honest, BuildElidedBlobTransaction(bodies[i]));
            Assert.That(registry.RecordCells(honest, bodies[i].Hash!, mask, cells[i]), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            _transactionPool.Received(2).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
            Assert.That(GetBufferedCellBytes(registry), Is.Zero);
            Assert.That(GetTrackedTransactionBytes(registry), Is.Zero);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public void quality_clock_failure_does_not_abort_cell_recovery_or_valid_submission([Values(0, 1, 2)] int path)
    {
        ManualTimeProvider time = new();
        OneShotQualityClock quality = new(time);
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory(), timestamper: clock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Transaction body = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        Assert.That(registry.RecordAnnouncement(peer, body.Hash!, mask), Is.True);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
        if (path == 2)
        {
            Assert.That(peer.TrySendGetCells(body.Hash!, mask), Is.True);
            quality.ThrowNext = true;
            using CellsMessage72 reply = new(GetLastGetCellsRequestId(body.Hash!, mask), [], [], BlobCellMask.Empty.ToBytes());
            Assert.DoesNotThrow(() => HandleZeroMessage(reply, Eth72MessageCode.Cells));
            IDictionary pending = (IDictionary)typeof(Eth72ProtocolHandler)
                .GetField("_pendingCellRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_handler)!;
            Assert.That(pending.Count, Is.EqualTo(1));
            AssertRestorationUsage(registry, peer.Id, body.Hash!, 0, 1, 1);
            clock.Add(TimeSpan.FromSeconds(5));
            peer.MaintainSparseBlobState(clock.UtcNowOffset);
        }
        quality.ThrowNext = path == 0;
        Assert.DoesNotThrow(() => registry.RecordTransaction(peer, BuildElidedBlobTransaction(body)));
        quality.ThrowNext = path == 1;
        Assert.DoesNotThrow(() => Assert.That(registry.RecordCells(peer, body.Hash!, mask, cells), Is.True));
        object state = GetTrackedState(registry, body.Hash!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(quality.Faults, Is.EqualTo(1));
            Assert.That(state.GetType().GetProperty("Submitted")!.GetValue(state), Is.True);
            Assert.That(state.GetType().GetProperty("Submitting")!.GetValue(state), Is.False);
            Assert.That(GetBufferedCellBytes(registry), Is.Zero);
            Assert.That(GetTrackedTransactionBytes(registry), Is.Zero);
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    private sealed class OneShotQualityClock(ManualTimeProvider clock) : TimeProvider
    {
        public bool ThrowNext { get; set; }
        public int Faults { get; private set; }
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override long GetTimestamp()
        {
            if (ThrowNext)
            {
                ThrowNext = false;
                Faults++;
                throw new InvalidOperationException("Local quality clock failed.");
            }
            return clock.GetTimestamp();
        }
    }

    [Test]
    public void closing_handler_finishes_cleanup_after_a_throwing_body_fallback([Values(0, 1, 2)] int faultPath)
    {
        bool retainedBody = faultPath != 0;
        ManualTimestamper clock = new();
        ITimestamper registryClock = Substitute.For<ITimestamper>();
        bool throwClock = false;
        int clockFaults = 0;
        registryClock.UtcNowOffset.Returns(_ =>
        {
            if (throwClock)
            {
                throwClock = false;
                clockFaults++;
                throw new InvalidOperationException("Local peer cleanup clock failed.");
            }
            return clock.UtcNowOffset;
        });
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: registryClock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, requestClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer honest = CreateSparseBlobPeers(registry, 1)[0];
        Transaction retained = BuildSparseBlobTransaction(out _, out byte[][] retainedCells);
        Hash256 body = retained.Hash!;
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, body, mask), Is.True);
            Assert.That(registry.RecordAnnouncement(honest, body, mask), Is.True);
            Assert.That(peer.TrySendPooledTransactionRequest(body), Is.True);
        }
        if (retainedBody)
        {
            _transactionPool.ValidateTxForBlobSampling(retained).Returns(AcceptTxResult.Accepted);
            Assert.That(registry.RecordTransaction(peer, retained), Is.Null);
        }
        if (faultPath != 2)
            honest.TransactionRequestHandler = _ => throw new InvalidOperationException("Local alternate send failed.");
        Hash256 pending = HashFromInt(717);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, pending, mask), Is.True);
            Assert.That(peer.TrySendGetCells(pending, mask), Is.True);
        }
        ReplyEmpty(pending, mask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetPendingRestoration(_handler, pending), Is.Not.Null);
            Assert.That(peer.TrySendGetCells(HashFromInt(718), mask), Is.True);
        }
        _handler.ProtocolInitialized += (_, _) => { };
        int removedEvents = _transactionPool.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "remove_NewPending");
        _session.IsClosing.Returns(true);
        throwClock = faultPath == 2;
        Assert.DoesNotThrow(() => _handler.Dispose());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.TransactionRequests, Is.EqualTo(faultPath == 2 ? Array.Empty<Hash256>() : new[] { body }));
            Assert.That(clockFaults, Is.EqualTo(faultPath == 2 ? 1 : 0));
            Assert.That(_transactionPool.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "remove_NewPending"), Is.EqualTo(removedEvents + 1));
            Assert.That(typeof(Nethermind.Network.P2P.ProtocolHandlers.ProtocolHandlerBase)
                .GetField("_protocolInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_handler), Is.Null);
            foreach (string field in new[] { "_pendingCellRequests", "_sentCellRequests", "_sentCellRequestIds" })
                Assert.That(((IDictionary)typeof(Eth72ProtocolHandler).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_handler)!).Count, Is.Zero);
            AssertRestorationUsage(registry, peer.Id, body, 0, 0, 0);
            AssertRestorationUsage(registry, peer.Id, pending, 0, 0, 0);
        }
        Assert.DoesNotThrow(() => _handler.Dispose());
        if (faultPath == 2)
        {
            timers.Timer.Fire();
            Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { body }));
            AssertRestorationUsage(registry, peer.Id, pending, 0, 0, 0);
            _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
            _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(AcceptTxResult.Accepted);
            registry.RecordTransaction(honest, BuildElidedBlobTransaction(retained));
            Assert.That(registry.RecordCells(honest, body, mask, retainedCells), Is.True);
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        }
    }

    [Test]
    public void subset_body_processing_finishes_all_omissions_after_a_retry_throws()
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        RecreateHandler(sparseBlobPoolPeerRegistry: registry);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer source = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer honest = CreateSparseBlobPeers(registry, 1)[0];
        Transaction first = BuildSparseBlobTransaction(out BlobCellMask mask, out _, out _, 400);
        Transaction included = BuildSparseBlobTransaction(out _, out byte[][] cells, out _, 401);
        Transaction last = BuildSparseBlobTransaction(out _, out _, out _, 402);
        Transaction[] bodies = [first, included, last];
        foreach (Transaction body in bodies)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(source, body.Hash!, mask), Is.True);
                Assert.That(registry.RecordAnnouncement(honest, body.Hash!, mask), Is.True);
            }
        }
        honest.TransactionRequestHandler = hash => hash == first.Hash
            ? throw new InvalidOperationException("Local first retry failed.") : true;
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        _handler.HandleMessages(bodies.Select(body => body.Hash!.ValueHash256).ToArray());
        using PooledTransactionsMessage66 reply = new(GetLastGetPooledTransactionsRequestId(first.Hash!), new PooledTransactionsMessage65(new[] { included }.ToPooledList()));
        Assert.DoesNotThrow(() => HandleZeroMessage(reply, Eth66MessageCode.PooledTransactions));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.TransactionRequests, Is.EqualTo(new[] { first.Hash! }));
            Assert.That(registry.CoversRequest(source, first.Hash!.ValueHash256, mask), Is.False);
            Assert.That(registry.CoversRequest(source, last.Hash!.ValueHash256, mask), Is.True);
            Assert.That(registry.CoversRequest(source, included.Hash!.ValueHash256, mask), Is.True);
            Assert.That(GetTrackedState(registry, included.Hash!).GetType().GetProperty("Transaction")!
                .GetValue(GetTrackedState(registry, included.Hash!)), Is.Not.Null);
        }
        Assert.That(registry.RecordCells(honest, included.Hash!, mask, cells), Is.True);
        _transactionPool.Received(1).SubmitTx(Arg.Is<Transaction>(transaction => transaction.Hash == included.Hash), TxHandlingOptions.None);
    }

    private static AnnouncementRestoration? GetPendingRestoration(Eth72ProtocolHandler handler, Hash256 hash)
    {
        System.Collections.IDictionary pending = (System.Collections.IDictionary)typeof(Eth72ProtocolHandler)
            .GetField("_pendingCellRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
        object? state = pending[hash.ValueHash256];
        return state?.GetType().GetProperty("Restoration")!.GetValue(state) as AnnouncementRestoration;
    }

    [Test]
    public void restoration_ticket_preserves_fresh_announcement_and_balances_ownership([Values] bool removePeerFirst)
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer peer = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(700);
        BlobCellMask fresh = BlobCellMask.FromIndices([3]);
        Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
        AssertRestorationUsage(registry, peer.Id, hash, 1, 1, 0);
        AnnouncementRestoration? ticket;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.TryBeginAnnouncementRestore(peer, hash, out ticket), Is.True);
            Assert.That(ticket, Is.Not.Null);
        }
        AssertRestorationUsage(registry, peer.Id, hash, 0, 1, 1);
        Assert.That(registry.RecordAnnouncement(peer, hash, fresh), Is.True);
        AssertRestorationUsage(registry, peer.Id, hash, 1, 2, 1);
        Assert.That(registry.TryRestoreAnnouncement(peer, hash, ticket!), Is.True);
        Assert.That(registry.TryRestoreAnnouncement(peer, hash, ticket!), Is.False);
        using (Assert.EnterMultipleScope())
        {
            AssertRestorationUsage(registry, peer.Id, hash, 1, 1, 0);
            Assert.That(registry.CoversRequest(peer, hash.ValueHash256, fresh), Is.True);
            Assert.That(registry.CoversRequest(peer, hash.ValueHash256, BlobCellMask.Full), Is.False);
        }
        if (!removePeerFirst) registry.RemoveAnnouncementPermanently(peer, hash);
        registry.RemovePeer(peer);
        AssertRestorationUsage(registry, peer.Id, hash, 0, 0, 0);
    }

    [Test]
    public void restoration_tickets_release_peer_tracking_ownership([Values] bool freshAnnouncement)
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer peer = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(701);
        AnnouncementRestoration? ticket;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryBeginAnnouncementRestore(peer, hash, out ticket), Is.True);
        }
        if (freshAnnouncement) Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
        AssertRestorationUsage(registry, peer.Id, hash, freshAnnouncement ? 1 : 0, freshAnnouncement ? 2 : 1, 1);
        registry.RemovePeer(peer);
        TestSparseBlobPeer replacement = new(peer.Id);
        registry.AddPeer(replacement);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ticket!.IsValid, Is.False);
            Assert.That(registry.TryRestoreAnnouncement(replacement, hash, ticket), Is.False);
            Assert.That(registry.CoversRequest(replacement, hash.ValueHash256, BlobCellMask.Full), Is.False);
        }
        AssertRestorationUsage(registry, peer.Id, hash, 0, 0, 0);
    }

    [Test]
    public void restoration_tickets_expire_after_pending_lifetime([Values(15, 16)] int seconds, [Values] bool sweep)
    {
        ManualTimestamper clock = new();
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer peer = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(702);
        AnnouncementRestoration? ticket;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryBeginAnnouncementRestore(peer, hash, out ticket), Is.True);
            Assert.That(ticket!.IsValid, Is.True);
        }
        clock.Add(TimeSpan.FromSeconds(seconds));
        if (sweep) timers.Timer.Fire();
        Assert.That(registry.TryRestoreAnnouncement(peer, hash, ticket), Is.EqualTo(seconds < 16));
        using (Assert.EnterMultipleScope())
        {
            AssertRestorationUsage(registry, peer.Id, hash, seconds < 16 ? 1 : 0, seconds < 16 ? 1 : 0, 0);
            Assert.That(ticket.IsValid, Is.False);
        }
    }

    [Test]
    public void restoration_capacity_preserves_received_failure_evidence([Values] bool covered, [Values] bool partial)
    {
        ManualTimeProvider quality = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        for (int i = 0; i < 1024; i++)
        {
            Hash256 hash = HashFromInt(20000 + i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
                Assert.That(registry.TryBeginAnnouncementRestore(peer, hash, out AnnouncementRestoration? ticket), Is.True);
                Assert.That(ticket, Is.Not.Null);
            }
        }
        BlobCellMask requested = BlobCellMask.FromIndices([4, 5]);
        BlobCellMask supplied = BlobCellMask.FromIndices([4]);
        for (int i = 0; i < 4; i++)
        {
            Hash256 hash = HashFromInt(22000 + i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(peer, hash, covered ? requested : supplied), Is.True);
                Assert.That(peer.TrySendGetCells(hash, requested), Is.True);
            }
        }
        for (int i = 0; i < 4; i++)
        {
            Hash256 hash = HashFromInt(22000 + i);
            using CellsMessage72 reply = new(GetLastGetCellsRequestId(hash, requested),
                partial ? [hash.ValueHash256] : [],
                partial ? [[new byte[CkzgLib.Ckzg.BytesPerCell]]] : [],
                (partial ? supplied : BlobCellMask.Empty).ToBytes());
            HandleZeroMessage(reply, Eth72MessageCode.Cells);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.CoversRequest(peer, hash.ValueHash256, covered ? requested : supplied), Is.True);
                Assert.That(GetPendingRestoration(_handler, hash), Is.Null);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((covered ? 4 : 0, 0)));
            Assert.That(peer.CanRequestCells, Is.EqualTo(!covered));
        }
        registry.RemovePeer(peer);
        using (Assert.EnterMultipleScope())
        {
            AssertRestorationUsage(registry, peer.Id, HashFromInt(20000), 0, 0, 0);
            _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
        }
    }

    [Test]
    public void restoration_ticket_capacity_is_isolated_per_peer()
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        for (int i = 0; i < 1024; i++)
        {
            Hash256 hash = HashFromInt(1000 + i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(peers[0], hash, BlobCellMask.Full), Is.True);
                Assert.That(registry.TryBeginAnnouncementRestore(peers[0], hash, out AnnouncementRestoration? ticket), Is.True);
                Assert.That(ticket, Is.Not.Null);
            }
        }
        Hash256 refused = HashFromInt(2024);
        AnnouncementRestoration? absent;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peers[0], refused, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryBeginAnnouncementRestore(peers[0], refused, out absent), Is.False);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(absent, Is.Null);
            Assert.That(registry.CoversRequest(peers[0], refused.ValueHash256, BlobCellMask.Full), Is.True);
        }
        AnnouncementRestoration? honest;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peers[1], refused, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryBeginAnnouncementRestore(peers[1], refused, out honest), Is.True);
            Assert.That(honest, Is.Not.Null);
        }
        using (Assert.EnterMultipleScope())
        {
            AssertRestorationUsage(registry, peers[0].Id, refused, 1, 1, 1024);
            AssertRestorationUsage(registry, peers[1].Id, refused, 0, 1, 1);
        }
        registry.RemovePeer(peers[0]);
        Assert.That(registry.TryRestoreAnnouncement(peers[1], refused, honest!), Is.True);
    }

    [Test]
    public async Task failed_creator_cleanup_retains_other_peer_ownership([Values] bool bodyOwnership)
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        Hash256 hash = HashFromInt(607);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        using ManualResetEventSlim failedCreator = new();
        using ManualResetEventSlim resume = new();
        AnnouncementRestoration? ticket = null;
        BodyRequestAttempt? body = null;
        int checks = 0;
        peers[0].IsClosingHandler = () =>
        {
            if (++checks == 1) return false;
            failedCreator.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            Assert.That(registry.RecordAnnouncement(peers[1], hash, mask), Is.True);
            if (bodyOwnership)
            {
                body = registry.CreateBodyRequestAttempt(peers[1], hash);
                body!.ConfirmSent();
                registry.RemoveAnnouncementPermanently(peers[1], hash);
            }
            else Assert.That(registry.TryBeginAnnouncementRestore(peers[1], hash, out ticket), Is.True);
            return true;
        };
        Task<bool> creator = Task.Run(() => registry.RecordAnnouncement(peers[0], hash, mask));
        try { Assert.That(failedCreator.Wait(TimeSpan.FromSeconds(10)), Is.True); }
        finally { resume.Set(); }
        Assert.That(await creator, Is.False);
        using (Assert.EnterMultipleScope())
        {
            AssertRestorationUsage(registry, peers[1].Id, hash, 0, 1, bodyOwnership ? 0 : 1);
            if (!bodyOwnership) Assert.That(registry.TryRestoreAnnouncement(peers[1], hash, ticket!), Is.True);
        }
        registry.Clear(hash);
        using (Assert.EnterMultipleScope())
        {
            AssertRestorationUsage(registry, peers[1].Id, hash, 0, 0, 0);
            if (!bodyOwnership) Assert.That(ticket!.IsValid, Is.False);
        }
    }

    [Test]
    public async Task removed_peer_cannot_publish_delayed_body_ownership([Values] bool replacePeer)
    {
        ManualTimestamper clock = new();
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        TestSparseBlobPeer oldPeer = peers[0];
        TestSparseBlobPeer honest = peers[1];
        Hash256 hash = HashFromInt(6090);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(oldPeer, hash, BlobCellMask.Full), Is.True);
            Assert.That(registry.RecordAnnouncement(honest, hash, BlobCellMask.Full), Is.True);
        }
        BodyRequestAttempt attempt = registry.CreateBodyRequestAttempt(oldPeer, hash)!;
        object state = GetTrackedState(registry, hash);
        registry.RecordBodyUnavailable(oldPeer, hash);
        int requestsBefore = honest.TransactionRequests.Count;
        Assert.That(requestsBefore, Is.EqualTo(1));
        AssertRestorationUsage(registry, oldPeer.Id, hash, 0, 0, 0);
        using ManualResetEventSlim activityChecked = new();
        using ManualResetEventSlim resumeConfirmation = new();
        oldPeer.IsClosingHandler = () =>
        {
            activityChecked.Set();
            if (!resumeConfirmation.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return false;
        };
        Task confirmation = Task.Run(attempt.ConfirmSent);
        try
        {
            Assert.That(activityChecked.Wait(TimeSpan.FromSeconds(10)), Is.True);
            if (replacePeer) registry.AddPeer(new TestSparseBlobPeer(oldPeer.Id));
            else registry.RemovePeer(oldPeer);
        }
        finally
        {
            resumeConfirmation.Set();
        }
        await confirmation;
        oldPeer.IsClosingHandler = null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetType().GetProperty("CurrentBodyAttempt")!.GetValue(state), Is.Null);
            AssertRestorationUsage(registry, oldPeer.Id, hash, 0, 0, 0);
        }
        clock.Add(Timeouts.Eth);
        timers.Timer.Fire();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.TransactionRequests.Count, Is.EqualTo(requestsBefore + 1));
            Assert.That(oldPeer.TransactionRequests, Is.Empty);
            Assert.That(honest.Disconnects, Is.Empty);
        }
        attempt.Complete(unanswered: false);
        AssertRestorationUsage(registry, oldPeer.Id, hash, 0, 0, 0);
    }

    [Test]
    public void disposed_registry_rejects_delayed_body_ownership_publication()
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer peer = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(608);
        Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
        BodyRequestAttempt attempt = registry.CreateBodyRequestAttempt(peer, hash)!;
        object state = GetTrackedState(registry, hash);
        registry.RemoveAnnouncementPermanently(peer, hash);
        registry.Dispose();
        attempt.ConfirmSent();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetType().GetProperty("CurrentBodyAttempt")!.GetValue(state), Is.Null);
            AssertRestorationUsage(registry, peer.Id, hash, 0, 0, 0);
            Assert.That(registry.CreateBodyRequestAttempt(peer, hash), Is.Null);
        }
        attempt.Complete(unanswered: false);
        AssertRestorationUsage(registry, peer.Id, hash, 0, 0, 0);
    }

    [Test]
    public void accepted_residual_ambiguous_cell_candidates_stall_until_expiry([Values(1, 3)] int failedTuples)
    {
        ManualTimestamper clock = new();
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: timers, timestamper: clock);
        Transaction tx = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        ShardBlobNetworkWrapper honestBody = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2 * failedTuples);
        byte[][] corrupt = CorruptCellValue(cells);
        int accepted = 0;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            if (valid) accepted++;
            return valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        foreach (TestSparseBlobPeer peer in peers) Assert.That(registry.RecordAnnouncement(peer, tx.Hash!, mask), Is.True);
        for (int i = 0; i < failedTuples; i++)
        {
            tx.NetworkWrapper = honestBody;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordTransaction(peers[2 * i], tx), Is.Null);
                Assert.That(registry.RecordCells(peers[2 * i + 1], tx.Hash!, mask, corrupt), Is.True);
            }
        }
        object state = GetTrackedState(registry, tx.Hash!);
        ICollection excluded = (ICollection)state.GetType().GetProperty("AmbiguousFailureSourceOrder")!.GetValue(state)!;
        int cellRequests = peers.Sum(peer => peer.CellRequests.Count);
        int bodyRequests = peers.Sum(peer => peer.TransactionRequests.Count);
        int fallbackRequests = peers[^2].TransactionRequests.Count;
        clock.Add(TimeSpan.FromSeconds(59));
        timers.Timer.Fire();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(excluded.Count, Is.EqualTo(2 * failedTuples));
            Assert.That(accepted, Is.Zero);
            Assert.That(peers.Sum(peer => peer.CellRequests.Count), Is.EqualTo(cellRequests));
            Assert.That(peers.Sum(peer => peer.TransactionRequests.Count), Is.EqualTo(bodyRequests + 1));
            Assert.That(peers[^2].TransactionRequests.Count, Is.EqualTo(fallbackRequests + 1));
            Assert.That(registry.RecordAnnouncement(peers[^1], tx.Hash!, mask), Is.False);
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
        clock.Add(TimeSpan.FromSeconds(2));
        timers.Timer.Fire();
        Assert.That(registry.RecordAnnouncement(peers[0], tx.Hash!, mask), Is.True);
        tx.NetworkWrapper = honestBody;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(peers[0], tx), Is.Null);
            Assert.That(registry.RecordCells(peers[1], tx.Hash!, mask, cells), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(1));
            _transactionPool.Received(failedTuples + 1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
        TestContext.Progress.WriteLine($"Stalling one hash costs {2 * failedTuples} admitted identities and {failedTuples} body/cell tuples with invalid proofs; exclusions retain at most 64 identities until state expiry.");
    }

    [Test]
    public void restoration_clock_failure_keeps_accounting_reclaimable([Values] bool removePeerFirst)
    {
        ManualTimestamper clock = new();
        ITimestamper faulty = Substitute.For<ITimestamper>();
        bool fault = false;
        faulty.UtcNowOffset.Returns(_ => fault ? throw new InvalidOperationException("Local restoration clock failed.") : clock.UtcNowOffset);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: new ManualTimerFactory(), timestamper: faulty);
        TestSparseBlobPeer peer = CreateSparseBlobPeers(registry, 1)[0];
        Hash256 hash = HashFromInt(609);
        Assert.That(registry.RecordAnnouncement(peer, hash, BlobCellMask.Full), Is.True);
        fault = true;
        Assert.Throws<InvalidOperationException>(() => registry.TryBeginAnnouncementRestore(peer, hash, out _));
        fault = false;
        AssertRestorationUsage(registry, peer.Id, hash, 1, 1, 0);
        if (removePeerFirst) registry.RemovePeer(peer);
        registry.Clear(hash);
        if (!removePeerFirst) registry.RemovePeer(peer);
        AssertRestorationUsage(registry, peer.Id, hash, 0, 0, 0);
        registry.AddPeer(peer);
        for (int i = 0; i < 1024; i++)
        {
            Hash256 next = HashFromInt(20000 + i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(peer, next, BlobCellMask.Full), Is.True);
                Assert.That(registry.TryBeginAnnouncementRestore(peer, next, out AnnouncementRestoration? restored), Is.True);
                Assert.That(restored, Is.Not.Null);
            }
        }
        Hash256 overflow = HashFromInt(22000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordAnnouncement(peer, overflow, BlobCellMask.Full), Is.True);
            Assert.That(registry.TryBeginAnnouncementRestore(peer, overflow, out _), Is.False);
        }
        registry.RemovePeer(peer);
        using (Assert.EnterMultipleScope())
        {
            AssertRestorationUsage(registry, peer.Id, hash, 0, 0, 0);
            AssertRestorationUsage(registry, peer.Id, overflow, 0, 0, 0);
        }
    }

    [Test]
    public void pending_clock_failure_does_not_publish_partial_work()
    {
        ManualTimeProvider quality = new();
        ManualTimestamper clock = new(quality.GetUtcNow().UtcDateTime);
        ITimestamper faulty = Substitute.For<ITimestamper>();
        bool fault = false;
        bool faultObserved = false;
        int reads = 0;
        faulty.UtcNowOffset.Returns(_ =>
        {
            if (fault && ++reads == 2)
            {
                faultObserved = true;
                throw new InvalidOperationException("Local pending clock failed.");
            }
            return clock.UtcNowOffset;
        });
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            timerFactory: timers, timestamper: clock);
        SparseRegistryDecorator decorated = new(registry, true, afterRestoreCreated: _ => fault = true);
        RecreateHandler(sparseBlobPoolPeerRegistry: decorated, cellQualityClock: quality, requestClock: faulty);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        Hash256 hash = HashFromInt(610);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.RecordAnnouncement(peer, hash, mask), Is.True);
            Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
        }
        using CellsMessage72 reply = new(GetLastGetCellsRequestId(hash, mask), [], [], BlobCellMask.Empty.ToBytes());
        InvalidOperationException? observed = null;
        try { HandleZeroMessage(reply, Eth72MessageCode.Cells); }
        catch (InvalidOperationException exception) { observed = exception; }
        if (observed is not null) Assert.That(observed.Message, Is.EqualTo("Local pending clock failed."));
        fault = false;
        IDictionary pending = (IDictionary)typeof(Eth72ProtocolHandler)
            .GetField("_pendingCellRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_handler)!;
        int Counter(string name) => (int)typeof(Eth72ProtocolHandler)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_handler)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(faultObserved, Is.True);
            Assert.That(pending.Count, Is.Zero);
            Assert.That(Counter("_pendingCellRequestCount"), Is.Zero);
            Assert.That(Counter("_pendingCellRequestWork"), Is.Zero);
        }
        clock.Add(TimeSpan.FromSeconds(17));
        timers.Timer.Fire();
        AssertRestorationUsage(registry, peer.Id, hash, 0, 0, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorated.RecordAnnouncement(peer, hash, mask), Is.True);
            Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
        }
    }

    private SparseBlobPoolPeerRegistry CreateRegisteredRegistry(
        ITxPool txPool, IBlobCustodyTracker custody, IBackgroundTaskScheduler scheduler,
        ILogManager logs, TimeSpan maxAdmissionDelay = default, TimeSpan? cellRequestTimeout = null,
        ITimerFactory? timerFactory = null, ITimestamper? timestamper = null)
    {
        ContainerBuilder builder = new();
        builder.RegisterModule(new NetworkModule(new ConfigProvider(new NetworkConfig())));
        builder.RegisterInstance(txPool).As<ITxPool>();
        builder.RegisterInstance(custody).As<IBlobCustodyTracker>();
        builder.RegisterInstance(scheduler).As<IBackgroundTaskScheduler>();
        builder.RegisterInstance(logs).As<ILogManager>();
        builder.RegisterType<SparseBlobPoolPeerRegistry>()
            .FindConstructorsWith(type => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .UsingConstructor(typeof(ITxPool), typeof(IBlobCustodyTracker), typeof(IBackgroundTaskScheduler),
                typeof(ILogManager), typeof(TimeSpan), typeof(TimeSpan?), typeof(ITimerFactory), typeof(ITimestamper))
            .WithParameter("maxAdmissionDelay", maxAdmissionDelay)
            .WithParameter("cellRequestTimeout", cellRequestTimeout)
            .WithParameter("timerFactory", timerFactory)
            .WithParameter("timestamper", timestamper)
            .As<ISparseBlobPoolPeerRegistry>().SingleInstance();
        IContainer container = builder.Build();
        container.AddTo(_disposables);
        return (SparseBlobPoolPeerRegistry)container.Resolve<ISparseBlobPoolPeerRegistry>();
    }

    private static void AssertRestorationUsage(SparseBlobPoolPeerRegistry registry, PublicKey peerId, Hash256 hash,
        int announcements, int trackedReferences, int tickets)
    {
        System.Collections.IDictionary usageByPeer = (System.Collections.IDictionary)typeof(SparseBlobPoolPeerRegistry)
            .GetField("_peerUsage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        object? usage = usageByPeer[peerId];
        int Count(string name) => usage is null ? 0 : (int)usage.GetType().GetProperty(name)!.GetValue(usage)!;
        System.Collections.IDictionary? Values(string name) => usage is null ? null
            : (System.Collections.IDictionary)usage.GetType().GetProperty(name)!.GetValue(usage)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Count("Announcements"), Is.EqualTo(announcements));
            Assert.That(Count("RestorationTickets"), Is.EqualTo(tickets));
            Assert.That(Values("AnnouncedStates")?.Count ?? 0, Is.EqualTo(announcements));
            Assert.That(Values("AnnouncedHashes")?[hash.ValueHash256] ?? 0, Is.EqualTo(announcements == 0 ? 0 : 1));
            Assert.That(Values("TrackedHashes")?[hash.ValueHash256] ?? 0, Is.EqualTo(trackedReferences));
        }
    }

    private sealed class ForwardingSession(ISession session) : ISession
    {
        public byte P2PVersion => session.P2PVersion;
        public SessionState State => session.State;
        public SessionState BestStateReached => session.BestStateReached;
        public bool IsClosing => session.IsClosing;
        public bool IsChannelClosed => session.IsChannelClosed;
        public bool IsChannelWritable => session.IsChannelWritable;
        public PacketReceiptTracker InboundReceipts => session.InboundReceipts;
        public PublicKey RemoteNodeId => session.RemoteNodeId;
        public PublicKey ObsoleteRemoteNodeId => session.ObsoleteRemoteNodeId;
        public string RemoteHost { get => session.RemoteHost; set => session.RemoteHost = value; }
        public int RemotePort { get => session.RemotePort; set => session.RemotePort = value; }
        public int LocalPort => session.LocalPort;
        public bool IsNetworkIdMatched { get => session.IsNetworkIdMatched; set => session.IsNetworkIdMatched = value; }
        public ConnectionDirection Direction => session.Direction;
        public Guid SessionId => session.SessionId;
        public Node Node => session.Node;
        public DateTime LastPingUtc { get => session.LastPingUtc; set => session.LastPingUtc = value; }
        public DateTime LastPongUtc { get => session.LastPongUtc; set => session.LastPongUtc = value; }
        public IPingSender PingSender { get => session.PingSender; set => session.PingSender = value; }
        public void ReceiveMessage(ZeroPacket packet) => session.ReceiveMessage(packet);
        public int DeliverMessage<T>(T message) where T : P2PMessage => session.DeliverMessage(message);
        public void EnableSnappy() => session.EnableSnappy();
        public void AddSupportedCapability(Capability capability) => session.AddSupportedCapability(capability);
        public bool HasAvailableCapability(Capability capability) => session.HasAvailableCapability(capability);
        public bool HasAgreedCapability(Capability capability) => session.HasAgreedCapability(capability);
        public void AddProtocolHandler(IProtocolHandler handler) => session.AddProtocolHandler(handler);
        public bool TryGetProtocolHandler(string code, out IProtocolHandler handler) => session.TryGetProtocolHandler(code, out handler);
        public void Init(byte version, IChannelHandlerContext context, IPacketSender sender) => session.Init(version, context, sender);
        public void InitiateDisconnect(DisconnectReason reason, string details) => session.InitiateDisconnect(reason, details);
        public void MarkDisconnected(DisconnectReason reason, DisconnectType type, string details) => session.MarkDisconnected(reason, type, details);
        public void Handshake(PublicKey id) => session.Handshake(id);
        public void MarkAuthenticated() => session.MarkAuthenticated();
        public void StartTrackingSession() => session.StartTrackingSession();
        public void Dispose() => session.Dispose();
        public event EventHandler<Nethermind.Network.P2P.EventArg.DisconnectEventArgs> Disconnecting { add => session.Disconnecting += value; remove => session.Disconnecting -= value; }
        public event EventHandler<Nethermind.Network.P2P.EventArg.DisconnectEventArgs> Disconnected { add => session.Disconnected += value; remove => session.Disconnected -= value; }
        public event EventHandler<EventArgs> Initialized { add => session.Initialized += value; remove => session.Initialized -= value; }
        public event EventHandler<EventArgs> HandshakeComplete { add => session.HandshakeComplete += value; remove => session.HandshakeComplete -= value; }
        public event EventHandler<Nethermind.Network.PeerEventArgs> MsgReceived { add => session.MsgReceived += value; remove => session.MsgReceived -= value; }
        public event EventHandler<Nethermind.Network.PeerEventArgs> MsgDelivered { add => session.MsgDelivered += value; remove => session.MsgDelivered -= value; }
    }

    private sealed class SparsePeerDecorator(ISparseBlobPoolPeer peer, bool forwardCredit, Action? beforeCellsSend = null,
        Action? beforeBodySend = null) : ISparseBlobPoolPeer
    {
        public Func<(IDisposable Lease, Action<BlobCellMask> Complete)>? ClaimCellsReservation { get; private set; }
        public ISparseBlobPoolPeer Wrapped => peer;
        public PublicKey Id => peer.Id;
        public bool IsClosing => peer.IsClosing;
        public bool CanRequestCells => peer.CanRequestCells;
        public bool CanRequestTransactions => peer.CanRequestTransactions;
        public void RecordUsefulBody(ValueHash256 hash) => peer.RecordUsefulBody(hash);
        public object CellSourceToken => forwardCredit ? peer.CellSourceToken : this;
        public void RecordUsefulCells(ValueHash256 hash)
        {
            if (forwardCredit) peer.RecordUsefulCells(hash);
        }
        public bool TrySendGetCells(Hash256 hash, BlobCellMask mask) => peer.TrySendGetCells(hash, mask);
        public bool TrySendGetCells(Hash256 hash, BlobCellMask mask, Func<(IDisposable Lease, Action<BlobCellMask> Complete)> claimReservation)
        {
            ClaimCellsReservation = claimReservation;
            beforeCellsSend?.Invoke();
            return peer.TrySendGetCells(hash, mask, claimReservation);
        }
        public bool TrySendPooledTransactionRequest(Hash256 hash)
        {
            beforeBodySend?.Invoke();
            return peer.TrySendPooledTransactionRequest(hash);
        }
        public void MaintainSparseBlobState(DateTimeOffset now) => peer.MaintainSparseBlobState(now);
        public void DisconnectSparseBlobPeer(DisconnectReason reason, string details) => peer.DisconnectSparseBlobPeer(reason, details);
    }

    private sealed class SparseRegistryDecorator(ISparseBlobPoolPeerRegistry registry, bool forwardCredit, Action? beforeCellsSend = null,
        Action<AnnouncementRestoration?>? afterRestoreCreated = null, Action<AnnouncementRestoration>? beforeRestore = null,
        Action? beforeBodySend = null, Action<AnnouncementRestoration?>? afterPermanentRemove = null, Action? beforeCellMutation = null) : ISparseBlobPoolPeerRegistry
    {
        private readonly Dictionary<PublicKey, SparsePeerDecorator> _peers = [];
        public ISparseBlobPoolPeer Resolve(ISparseBlobPoolPeer peer) =>
            _peers.TryGetValue(peer.Id, out SparsePeerDecorator? decorated) && ReferenceEquals(decorated.Wrapped, peer) ? decorated : peer;
        public void AddPeer(ISparseBlobPoolPeer peer)
        {
            SparsePeerDecorator decorated = new(peer, forwardCredit, beforeCellsSend, beforeBodySend);
            _peers[peer.Id] = decorated;
            registry.AddPeer(decorated);
        }
        public void RemovePeer(ISparseBlobPoolPeer peer) => registry.RemovePeer(Resolve(peer));
        public bool RecordAnnouncement(ISparseBlobPoolPeer peer, in ValueHash256 hash, BlobCellMask mask) => registry.RecordAnnouncement(Resolve(peer), hash, mask);
        public BlobCellMask GetRequestMask(in ValueHash256 hash, BlobCellMask mask, int probability) => registry.GetRequestMask(hash, mask, probability);
        public bool TryBeginAnnouncementRestore(ISparseBlobPoolPeer peer, Hash256 hash, out AnnouncementRestoration? restoration, IDisposable? requestOwnership = null)
        {
            beforeCellMutation?.Invoke();
            bool accepted = registry.TryBeginAnnouncementRestore(Resolve(peer), hash, out restoration, requestOwnership);
            afterRestoreCreated?.Invoke(restoration);
            return accepted;
        }
        public bool TryRestoreAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash, AnnouncementRestoration restoration)
        {
            beforeRestore?.Invoke(restoration);
            return registry.TryRestoreAnnouncement(Resolve(peer), hash, restoration);
        }
        public AnnouncementRestoration? RemoveAnnouncementPermanently(ISparseBlobPoolPeer peer, Hash256 hash, IDisposable? requestOwnership = null)
        {
            beforeCellMutation?.Invoke();
            AnnouncementRestoration? canceled = registry.RemoveAnnouncementPermanently(Resolve(peer), hash, requestOwnership);
            afterPermanentRemove?.Invoke(canceled);
            return canceled;
        }
        public BodyRequestAttempt? CreateBodyRequestAttempt(ISparseBlobPoolPeer peer, Hash256 hash)
            => registry.CreateBodyRequestAttempt(Resolve(peer), hash);
        public BodyRequestAttempt? CreateBodyRequestAttempt(ISparseBlobPoolPeer peer, in ValueHash256 hash)
            => registry.CreateBodyRequestAttempt(Resolve(peer), hash);
        public AnnouncementRestoration? RecordBodyUnavailable(ISparseBlobPoolPeer peer, Hash256 hash, BodyRequestAttempt? requestAttempt = null, bool retainCellAnnouncement = false)
            => registry.RecordBodyUnavailable(Resolve(peer), hash, requestAttempt, retainCellAnnouncement);
        public bool CoversRequest(ISparseBlobPoolPeer peer, ValueHash256 hash, BlobCellMask mask) => registry.CoversRequest(Resolve(peer), hash, mask);
        public bool TryRequestCells(in ValueHash256 hash, BlobCellMask mask, PublicKey lastResort) => registry.TryRequestCells(hash, mask, lastResort);
        public void OnCellsRequestCompleted(in ValueHash256 hash, BlobCellMask mask, ISparseBlobPoolPeer peer) => registry.OnCellsRequestCompleted(hash, mask, Resolve(peer));
        public bool HasRecordedTransaction(in ValueHash256 hash) => registry.HasRecordedTransaction(hash);
        public int GetFullProviderAnnouncementCount(in ValueHash256 hash) => registry.GetFullProviderAnnouncementCount(hash);
        public AcceptTxResult? RecordTransaction(ISparseBlobPoolPeer peer, Transaction transaction) => registry.RecordTransaction(Resolve(peer), transaction);
        public bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask mask, byte[][] cells, IDisposable? requestOwnership = null)
        {
            beforeCellMutation?.Invoke();
            return registry.RecordCells(Resolve(peer), hash, mask, cells, requestOwnership);
        }
        public bool TryApplyRecordedCells(Hash256 hash) => registry.TryApplyRecordedCells(hash);
        public bool TryAcquireCellServeWork(int work) => registry.TryAcquireCellServeWork(work);
        public void RefundCellServeWork(int work) => registry.RefundCellServeWork(work);
        public void ReleaseCellServeWork() => registry.ReleaseCellServeWork();
        public void Clear(in ValueHash256 hash) => registry.Clear(hash);
    }

    [Test]
    public void empty_replies_do_not_erase_silence_on_other_hashes([Values(0, 4)] int emptyReplies)
    {
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        Hash256[] hashes = Enumerable.Range(0, 8).Select(HashFromInt).ToArray();
        foreach (Hash256 hash in hashes)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_sparseBlobPoolPeerRegistry.RecordAnnouncement(peer, hash, mask), Is.True);
                Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
            }
        }
        for (int i = 0; i < emptyReplies; i++) ReplyEmpty(hashes[i], mask);
        peer.MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));

        _session.Received(1).InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
    }

    [Test]
    // Recredit costs 17 retained observations and a newly verified column after the original hash leaves the ring.
    public void retained_useful_history_accepted_residual_requires_new_verified_columns()
    {
        ManualTimeProvider quality = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer bodyPeer = CreateSparseBlobPeers(registry, 1)[0];
        Transaction first = BuildSparseBlobTransaction(out BlobCellMask originalMask, out byte[][] originalCells, out byte[][] allCells, 300);
        ShardBlobNetworkWrapper originalWrapper = (ShardBlobNetworkWrapper)first.NetworkWrapper!;
        BlobCellMask retainedMask = BlobCellMask.Empty;
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            Transaction transaction = call.Arg<Transaction>();
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)transaction.NetworkWrapper!);
            if (valid && transaction.Hash == first.Hash) retainedMask = originalMask;
            return valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        _transactionPool.TryGetPendingBlobTransaction(first.Hash!, out Arg.Any<Transaction>()).Returns(call =>
        {
            call[1] = retainedMask.IsEmpty ? null : first;
            return !retainedMask.IsEmpty;
        });
        _transactionPool.TryGetPendingBlobCellMask(first.Hash!, out Arg.Any<BlobCellMask>()).Returns(call =>
        {
            call[1] = retainedMask;
            return !retainedMask.IsEmpty;
        });
        _transactionPool.MergeBlobCells(first.Hash!, Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>()).Returns(call =>
        {
            BlobCellMask receivedMask = call.ArgAt<BlobCellMask>(1);
            ShardBlobNetworkWrapper received = originalWrapper with { CellMask = receivedMask, Cells = call.Arg<byte[][]>() };
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs(received);
            BlobCellMask added = valid ? receivedMask.Except(retainedMask) : BlobCellMask.Empty;
            call[3] = added;
            if (valid) retainedMask |= receivedMask;
            return valid ? BlobCellMergeResult.Accepted : BlobCellMergeResult.InvalidCells;
        });
        for (int i = 0; i < 17; i++)
        {
            Transaction body;
            byte[][] cells;
            if (i == 0)
            {
                body = first;
                cells = originalCells;
            }
            else body = BuildSparseBlobTransaction(out _, out cells, out _, (ulong)(300 + i));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordTransaction(bodyPeer, body), Is.Null);
                Assert.That(registry.RecordAnnouncement(peer, body.Hash!, originalMask), Is.True);
                Assert.That(peer.TrySendGetCells(body.Hash!, originalMask), Is.True);
            }
            using CellsMessage72 reply = new(GetLastGetCellsRequestId(body.Hash!, originalMask), [body.Hash!.ValueHash256], [cells], originalMask.ToBytes());
            HandleZeroMessage(reply, Eth72MessageCode.Cells);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetRetainedUsefulHashes(_handler), Does.Not.Contain(first.Hash!.ValueHash256));
            Assert.That(peer.TrySendGetCells(first.Hash!, originalMask), Is.True);
        }
        using (CellsMessage72 duplicate = new(GetLastGetCellsRequestId(first.Hash!, originalMask), [first.Hash!.ValueHash256], [originalCells], originalMask.ToBytes()))
            HandleZeroMessage(duplicate, Eth72MessageCode.Cells);
        Assert.That(GetRetainedUsefulHashes(_handler), Does.Not.Contain(first.Hash!.ValueHash256));
        BlobCellMask fresh = BlobCellMask.FromIndices([5]);
        byte[][] freshCells = BlobCellsHelper.SelectFlattenedCells(allCells, BlobCellMask.Full, fresh, first.BlobVersionedHashes!.Length);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IBlobProofsManager.For(ProofVersion.V1).ValidateProofs(originalWrapper with { CellMask = fresh, Cells = freshCells }), Is.True);
            Assert.That(registry.RecordAnnouncement(peer, first.Hash!, originalMask | fresh), Is.True);
            Assert.That(peer.TrySendGetCells(first.Hash!, fresh), Is.True);
        }
        using CellsMessage72 newColumn = new(GetLastGetCellsRequestId(first.Hash!, fresh), [first.Hash!.ValueHash256], [freshCells], fresh.ToBytes());
        HandleZeroMessage(newColumn, Eth72MessageCode.Cells);
        _transactionPool.Received(1).MergeBlobCells(first.Hash!, fresh, Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetRetainedUsefulHashes(_handler).Count(hash => hash == first.Hash!.ValueHash256), Is.EqualTo(1));
            Assert.That(retainedMask, Is.EqualTo(originalMask | fresh));
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((0, 16)));
        }
    }

    private static ValueHash256[] GetRetainedUsefulHashes(Eth72ProtocolHandler handler)
    {
        CellResponseHistory history = (CellResponseHistory)typeof(Eth72ProtocolHandler)
            .GetField("_cellResponses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
        Array samples = (Array)typeof(CellResponseHistory).GetField("_samples", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history)!;
        return samples.Cast<object>().Where(sample => (bool)sample.GetType().GetProperty("Useful")!.GetValue(sample)!)
            .Select(sample => (ValueHash256)sample.GetType().GetProperty("Hash")!.GetValue(sample)!).ToArray();
    }

    [Test]
    public void useful_evidence_survives_history_turnover_for_disconnect()
        => VerifyUsefulHistoryTurnover(expireUseful: false);

    [Test]
    public void useful_evidence_disconnect_guard_expires_at_five_minutes()
        => VerifyUsefulHistoryTurnover(expireUseful: true);

    private void VerifyUsefulHistoryTurnover(bool expireUseful)
    {
        ManualTimeProvider quality = new();
        ManualTimestamper requestClock = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory(), timestamper: requestClock);
        RecreateHandler(sparseBlobPoolPeerRegistry: registry, cellQualityClock: quality, requestClock: requestClock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        TestSparseBlobPeer bodyPeer = CreateSparseBlobPeers(registry, 1)[0];
        Transaction body = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
            IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!)
                ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordTransaction(bodyPeer, body), Is.Null);
            Assert.That(registry.RecordAnnouncement(peer, body.Hash!, mask), Is.True);
            Assert.That(peer.TrySendGetCells(body.Hash!, mask), Is.True);
        }
        using CellsMessage72 useful = new(GetLastGetCellsRequestId(body.Hash!, mask), [body.Hash!.ValueHash256], [cells], mask.ToBytes());
        HandleZeroMessage(useful, Eth72MessageCode.Cells);
        using (Assert.EnterMultipleScope())
        {
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((0, 1)));
        }
        for (int i = 0; i < 17; i++)
        {
            Hash256 hash = HashFromInt(10000 + i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordAnnouncement(peer, hash, mask), Is.True);
                Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
            }
        }
        quality.Advance(TimeSpan.FromSeconds(299));
        for (int i = 0; i < 16; i++) ReplyEmpty(HashFromInt(10000 + i), mask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetCellEvidence(_handler, quality), Is.EqualTo((16, 0)));
            Assert.That(peer.CanRequestCells, Is.False);
            _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
        }
        if (expireUseful) quality.Advance(TimeSpan.FromSeconds(1));
        ReplyEmpty(HashFromInt(10016), mask);
        _session.Received(expireUseful ? 1 : 0).InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
    }

    private static (int Failures, int Useful) GetCellEvidence(Eth72ProtocolHandler handler, TimeProvider clock)
    {
        CellResponseHistory history = (CellResponseHistory)typeof(Eth72ProtocolHandler)
            .GetField("_cellResponses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
        return ((int, int))typeof(CellResponseHistory).GetMethod("Count", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(history, [clock, TimeSpan.FromMinutes(1)])!;
    }

    [Test]
    public void cell_provider_backoff_expires_without_a_reply()
    {
        ManualTimeProvider clock = new();
        RecreateHandler(cellQualityClock: clock);
        HandleIncomingStatusMessage();
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        for (int i = 0; i < 4; i++)
        {
            Hash256 hash = HashFromInt(i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_sparseBlobPoolPeerRegistry.RecordAnnouncement(peer, hash, mask), Is.True);
                Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
            }
            ReplyEmpty(hash, mask);
        }
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.That(peer.TrySendGetCells(HashFromInt(100), mask), Is.False);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(peer.TrySendGetCells(HashFromInt(101), mask), Is.True);
        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
    }

    [Test]
    public void locally_failed_cell_sends_do_not_count_as_silence([Values] bool throws)
    {
        HandleIncomingStatusMessage();
        _session.DeliverMessage(Arg.Any<GetCellsMessage72>()).Returns(_ => throws ? throw new InvalidOperationException("local send failed") : 0);
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        for (int i = 0; i < 8; i++)
        {
            Hash256 hash = HashFromInt(i);
            if (throws) Assert.Throws<InvalidOperationException>(() => peer.TrySendGetCells(hash, mask));
            else Assert.That(peer.TrySendGetCells(hash, mask), Is.False);
        }
        peer.MaintainSparseBlobState(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(11));
        _session.DidNotReceive().InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
        _session.DeliverMessage(Arg.Any<GetCellsMessage72>()).Returns(32);
        Assert.That(peer.TrySendGetCells(HashFromInt(100), mask), Is.True);
    }

    private void AssertProviderAnnouncementPreserved(ISparseBlobPoolPeerRegistry registry, Hash256 hash)
    {
        using (Assert.EnterMultipleScope())
        {
            registry.DidNotReceive().TryBeginAnnouncementRestore(_handler, hash, out Arg.Any<AnnouncementRestoration?>(), Arg.Any<IDisposable?>());
            registry.DidNotReceive().RemoveAnnouncementPermanently(_handler, hash, Arg.Any<IDisposable?>());
        }
    }

    private static void ConfigureAnnouncementRestoration(ISparseBlobPoolPeerRegistry registry) =>
        registry.TryBeginAnnouncementRestore(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<Hash256>(), out Arg.Any<AnnouncementRestoration?>(), Arg.Any<IDisposable?>())
            .Returns(call =>
            {
                call[2] = new AnnouncementRestoration(call.Arg<ISparseBlobPoolPeer>(), new object(), BlobCellMask.Full, DateTimeOffset.UtcNow);
                return true;
            });

    private void ReplyEmpty(Hash256 hash, BlobCellMask mask)
    {
        using CellsMessage72 response = new(GetLastGetCellsRequestId(hash, mask), [], [], BlobCellMask.Empty.ToBytes());
        HandleZeroMessage(response, Eth72MessageCode.Cells);
    }

    [Test]
    public void covered_empty_cell_replies_across_hashes_stop_selection_then_disconnect([Values] bool covered)
    {
        HandleIncomingStatusMessage();
        _session.DeliverMessage(Arg.Any<GetCellsMessage72>()).Returns(call =>
        {
            using DisposableByteBuffer encoded = _svc.ZeroSerialize(call.Arg<GetCellsMessage72>()).AsDisposable();
            return encoded.ReadableBytes;
        });
        ISparseBlobPoolPeer peer = (ISparseBlobPoolPeer)_handler;
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        Hash256[] hashes = Enumerable.Range(0, 8).Select(HashFromInt).ToArray();
        foreach (Hash256 hash in hashes)
        {
            if (covered) Assert.That(_sparseBlobPoolPeerRegistry.RecordAnnouncement(peer, hash, mask), Is.True);
            Assert.That(peer.TrySendGetCells(hash, mask), Is.True);
        }

        bool canSelectAfterFour = true;
        for (int i = 0; i < hashes.Length; i++)
        {
            using CellsMessage72 empty = new(GetLastGetCellsRequestId(hashes[i], mask), [], [], BlobCellMask.Empty.ToBytes());
            HandleZeroMessage(empty, Eth72MessageCode.Cells);
            if (i == 3) canSelectAfterFour = peer.TrySendGetCells(HashFromInt(100), mask);
        }

        Assert.That(canSelectAfterFour, Is.EqualTo(!covered));
        _session.Received(covered ? 1 : 0).InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
    }

    [Test]
    public void Count_variants_preserve_honest_cells_until_body_binds_count(
        [Values] bool wrongFirst, [Values] bool sameMask, [Values] bool pooledBody)
    {
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask honestMask, out byte[][] honestCells);
        ShardBlobNetworkWrapper body = (ShardBlobNetworkWrapper)transaction.NetworkWrapper!;
        BlobCellMask wrongMask = sameMask ? honestMask : BlobCellMask.FromIndices([9]);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance,
            LimboLogs.Instance, TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 3);
        int validAdmissions = 0;
        bool bodyKnown = false;
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _transactionPool.TryGetPendingBlobTransaction(transaction.Hash!, out Arg.Any<Transaction>()).Returns(call =>
        {
            call[1] = bodyKnown ? transaction : null;
            return bodyKnown;
        });
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            if (valid) validAdmissions++;
            return valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        _transactionPool.MergeBlobCells(transaction.Hash!, Arg.Any<BlobCellMask>(), Arg.Any<byte[][]>(), out Arg.Any<BlobCellMask>()).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs(body with
            {
                CellMask = call.ArgAt<BlobCellMask>(1),
                Cells = call.ArgAt<byte[][]>(2)
            });
            if (valid) validAdmissions++;
            return valid ? BlobCellMergeResult.Accepted : BlobCellMergeResult.InvalidCells;
        });

        bool firstAccepted = wrongFirst ? RecordWrong() : RecordHonest();
        bool secondAccepted = wrongFirst ? RecordHonest() : RecordWrong();
        bool honestStillEligible = registry.RecordAnnouncement(peers[1], transaction.Hash!, honestMask);
        if (pooledBody)
        {
            bodyKnown = true;
            registry.TryApplyRecordedCells(transaction.Hash!);
        }
        else
        {
            registry.RecordTransaction(peers[2], transaction);
        }

        bool wrongStillEligible = registry.RecordAnnouncement(peers[0], transaction.Hash!, wrongMask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstAccepted, Is.True);
            Assert.That(secondAccepted, Is.True);
            Assert.That(honestStillEligible, Is.True);
            Assert.That(validAdmissions, Is.EqualTo(1));
            Assert.That(wrongStillEligible, Is.False);
            Assert.That(GetBufferedCellBytes(registry), Is.Zero);
            Assert.That(peers[1].Disconnects, Is.Empty);
            Assert.That(peers[2].Disconnects, Is.Empty);
        }

        bool RecordWrong() => registry.RecordCells(peers[0], transaction.Hash!, wrongMask, [.. honestCells, .. honestCells]);
        bool RecordHonest() => registry.RecordCells(peers[1], transaction.Hash!, honestMask, honestCells);
    }

    [Test]
    public void Count_variants_release_both_buffers([Values("expiry", "removal", "peers")] string cleanup)
    {
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        ManualTimestamper clock = new();
        ManualTimerFactory timers = new();
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            TimeSpan.Zero, timerFactory: timers, timestamper: clock);
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peers[0], transaction.Hash!, mask, [.. cells, .. cells]), Is.True);
            Assert.That(registry.RecordCells(peers[1], transaction.Hash!, mask, cells), Is.True);
            Assert.That(GetBufferedCellBytes(registry), Is.EqualTo(6144));
        }

        if (cleanup == "expiry")
        {
            clock.Add(TimeSpan.FromSeconds(16));
            timers.Timer.Fire();
        }
        else if (cleanup == "removal")
        {
            _transactionPool.RemovedPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(transaction));
        }
        else
        {
            registry.RemovePeer(peers[0]);
            Assert.That(GetBufferedCellBytes(registry), Is.EqualTo(2048));
            registry.RemovePeer(peers[1]);
        }

        Assert.That(GetBufferedCellBytes(registry), Is.Zero);
        foreach (TestSparseBlobPeer peer in peers) registry.AddPeer(peer);
        Assert.That(registry.RecordCells(peers[1], transaction.Hash!, mask, cells), Is.True);
    }

    [Test]
    public void Count_variants_enforce_combined_transaction_limit_and_two_counts()
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 3);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        Hash256 hash = Keccak.Compute("bounded variants");
        byte[] cell = new byte[2048];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peers[0], hash, mask, Enumerable.Repeat(cell, 512).ToArray()), Is.True);
            Assert.That(registry.RecordCells(peers[1], hash, mask, Enumerable.Repeat(cell, 513).ToArray()), Is.False);
            Assert.That(registry.RecordCells(peers[1], hash, mask, Enumerable.Repeat(cell, 511).ToArray()), Is.True);
            Assert.That(registry.RecordCells(peers[2], hash, mask, [cell]), Is.False);
            Assert.That(GetBufferedCellBytes(registry), Is.EqualTo(1023L * 2048));
        }
        registry.RemovePeer(peers[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peers[2], hash, mask, [cell]), Is.True);
            Assert.That(GetBufferedCellBytes(registry), Is.EqualTo(512L * 2048));
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
    }

    [Test]
    public void Count_variants_share_existing_global_and_peer_budgets([Values] bool global)
    {
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, global ? 8 : 1);
        BlobCellMask mask = BlobCellMask.FromIndices([4]);
        byte[] cell = new byte[2048];
        byte[][] first = Enumerable.Repeat(cell, 255).ToArray();
        byte[][] second = Enumerable.Repeat(cell, 257).ToArray();
        int transactions = global ? 64 : 16;
        for (int i = 0; i < transactions; i++)
        {
            Hash256 hash = Keccak.Compute($"budget {i}");
            TestSparseBlobPeer peer = peers[i % peers.Length];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.RecordCells(peer, hash, mask, first), Is.True);
                Assert.That(registry.RecordCells(peer, hash, mask, second), Is.True);
            }
        }
        long retained = GetBufferedCellBytes(registry);
        bool overflow = registry.RecordCells(peers[0], Keccak.Compute("overflow"), mask, [cell]);
        registry.RemovePeer(peers[0]);
        registry.AddPeer(peers[0]);
        bool reclaimed = registry.RecordCells(peers[0], Keccak.Compute("reclaimed"), mask, [cell]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retained, Is.EqualTo((global ? 64L : 16L) * 1024 * 1024));
            Assert.That(overflow, Is.False);
            Assert.That(reclaimed, Is.True);
            Assert.That(peers.SelectMany(peer => peer.Disconnects), Is.Empty);
        }
    }

    [Test]
    public void Count_variants_recheck_body_learned_during_cell_lookup([Values] bool matchingCount)
    {
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 2);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        int validAdmissions = 0;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            if (valid) validAdmissions++;
            return valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        bool recordBodyOnPeerCheck = false;
        _transactionPool.TryGetPendingBlobTransaction(transaction.Hash!, out Arg.Any<Transaction>()).Returns(call =>
        {
            recordBodyOnPeerCheck = true;
            call[1] = null;
            return false;
        });
        peers[0].IsClosingHandler = () =>
        {
            if (recordBodyOnPeerCheck)
            {
                recordBodyOnPeerCheck = false;
                registry.RecordTransaction(peers[1], transaction);
            }
            return false;
        };

        bool accepted = registry.RecordCells(peers[0], transaction.Hash!, mask, matchingCount ? cells : [.. cells, .. cells]);
        bool eligible = registry.RecordAnnouncement(peers[0], transaction.Hash!, mask);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(matchingCount));
            Assert.That(eligible, Is.EqualTo(matchingCount));
            Assert.That(validAdmissions, Is.EqualTo(matchingCount ? 1 : 0));
            Assert.That(GetBufferedCellBytes(registry), Is.Zero);
            Assert.That(peers[1].Disconnects, Is.Empty);
        }
    }

    [Test]
    public void Count_variants_release_accounting_when_admission_is_cancelled([Values] bool cancel)
    {
        Transaction transaction = BuildSparseBlobTransaction(out BlobCellMask mask, out byte[][] cells);
        using SparseBlobPoolPeerRegistry registry = CreateRegisteredRegistry(
            _transactionPool, _blobCustodyTracker, RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            TimeSpan.Zero, timerFactory: new ManualTimerFactory());
        TestSparseBlobPeer[] peers = CreateSparseBlobPeers(registry, 3);
        _transactionPool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        int verified = 0;
        _transactionPool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            bool valid = IBlobProofsManager.For(ProofVersion.V1).ValidateProofs((ShardBlobNetworkWrapper)call.Arg<Transaction>().NetworkWrapper!);
            if (valid) verified++;
            if (cancel) _transactionPool.RemovedPending += Raise.Event<EventHandler<TxEventArgs>>(_transactionPool, new TxEventArgs(transaction));
            return valid ? AcceptTxResult.Accepted : AcceptTxResult.InvalidBlobProofs;
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.RecordCells(peers[0], transaction.Hash!, mask, [.. cells, .. cells]), Is.True);
            Assert.That(registry.RecordCells(peers[1], transaction.Hash!, mask, cells), Is.True);
        }

        registry.RecordTransaction(peers[2], transaction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verified, Is.EqualTo(1));
            Assert.That(GetBufferedCellBytes(registry), Is.Zero);
            Assert.That(GetTrackedTransactionBytes(registry), Is.Zero);
            Assert.That(peers[1].Disconnects, Is.Empty);
            Assert.That(peers[2].Disconnects, Is.Empty);
        }
        _transactionPool.Received(cancel ? 1 : 0).RemoveTransaction(transaction.Hash!);
    }

    private static long GetBufferedCellBytes(SparseBlobPoolPeerRegistry registry) =>
        (long)typeof(SparseBlobPoolPeerRegistry).GetField("_earlyCellsBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;

    [Test]
    public void Maximal_announcement_keeps_first_and_last_transaction_shape(
        [Values(0, 4095)] int responseIndex, [Values] bool wrongSize)
    {
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
        byte[] types = new byte[4096];
        int[] sizes = new int[4096];
        ValueHash256[] hashes = new ValueHash256[4096];
        Array.Fill(sizes, 1024);
        for (int i = 0; i < hashes.Length; i++) hashes[i] = HashFromInt(i);
        hashes[responseIndex] = transaction.Hash!;
        sizes[responseIndex] = transaction.GetLength() + (wrongSize ? 9 : 0);
        _transactionPool.NotifyAboutTx(transaction.Hash!, Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        ValueHash256 requestedHash = transaction.Hash!.ValueHash256;
        _session.When(session => session.DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(request =>
            !request.EthMessage.Hashes.Contains(requestedHash))))
            .Do(call =>
            {
                using PooledTransactionsMessage66 emptyResponse = new(call.Arg<GetPooledTransactionsMessage>().RequestId,
                    new PooledTransactionsMessage65(Array.Empty<Transaction>().ToPooledList()));
                HandleZeroMessage(emptyResponse, Eth66MessageCode.PooledTransactions);
            });
        using NewPooledTransactionHashesMessage72 announcement = new(types, sizes, hashes, BlobCellMask.Empty.ToBytes());
        HandleIncomingStatusMessage();
        HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
        long requestId = GetLastGetPooledTransactionsRequestId(transaction.Hash!);
        using PooledTransactionsMessage66 response = new(requestId,
            new PooledTransactionsMessage65(new[] { transaction }.ToPooledList()));

        HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);

        if (wrongSize)
        {
            _session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, "invalid pooled tx type or size");
            _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }
        else
        {
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }
    }

    [Test]
    public async Task Requested_empty_cells_survive_a_full_unsolicited_quota([Values] bool fullUnsolicitedQuota)
    {
        RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
        try
        {
            RecreateHandler(backgroundTaskScheduler: scheduler);
            HandleIncomingStatusMessage();
            int broadcasts = fullUnsolicitedQuota ? 448 : 0;
            QueueBroadcasts(this, broadcasts);
            BlobCellMask requested = BlobCellMask.FromIndices([4]);
            Assert.That(((ISparseBlobPoolPeer)_handler).TrySendGetCells(TestItem.KeccakA, requested), Is.True);
            using CellsMessage72 response = new(GetLastGetCellsRequestId(TestItem.KeccakA, requested),
                [], [], BlobCellMask.Empty.ToBytes());
            HandleZeroMessage(response, Eth72MessageCode.Cells);
            Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(broadcasts + 1));
            await scheduler.Drain(CancellationToken.None);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        finally
        {
            await scheduler.Drain(new CancellationToken(true));
        }
    }

    [Test]
    public async Task Partly_processed_broadcast_resumes_while_the_peer_quota_is_full([Values] bool concurrent)
    {
        RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim firstProcessed = new();
        using ManualResetEventSlim competitorReady = new();
        try
        {
            RecreateHandler(backgroundTaskScheduler: scheduler);
            HandleIncomingStatusMessage();
            _transactionPool.When(pool => pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()))
                .Do(_ =>
                {
                    cancellation.Cancel();
                    if (!concurrent) return;
                    firstProcessed.Set();
                    Assert.That(competitorReady.Wait(TimeSpan.FromSeconds(5)), Is.True);
                });
            using TransactionsMessage batch = new(
                new ArrayPoolList<Transaction>(2)
                {
                    Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject,
                    Build.A.Transaction.WithNonce(2).SignedAndResolved().TestObject
                });
            HandleZeroMessage(batch, Eth62MessageCode.Transactions);
            QueueBroadcasts(this, 447);

            Task competingAdmission = concurrent ? Task.Run(() =>
            {
                Assert.That(firstProcessed.Wait(TimeSpan.FromSeconds(5)), Is.True);
                competitorReady.Set();
                QueueBroadcasts(this, 1000);
            }) : Task.CompletedTask;
            await Task.WhenAll(scheduler.RunNext(cancellation.Token), competingAdmission);
            _transactionPool.Received(1).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(449), "the unprocessed suffix must retain a scheduling slot");
            await scheduler.Drain(CancellationToken.None);
            _transactionPool.Received(2).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        finally
        {
            await scheduler.Drain(new CancellationToken(true));
        }
    }

    [Test]
    public async Task Requested_pooled_replies_survive_a_paused_scheduler([Values(1, 100)] int replies, [Values] bool empty, [Values] bool fullUnsolicitedQuota)
    {
        RecordingBackgroundTaskScheduler scheduler = new() { Defer = true, QueueCapacity = 8192 };
        try
        {
            ManualTimeProvider requestClock = new();
            RecreateHandler(backgroundTaskScheduler: scheduler, pooledRequestClock: requestClock);
            HandleIncomingStatusMessage();
            ManualTimeProvider clock = new();
            await using RetryCache<PooledTransactionRequestMessage, ValueHash256> retry = CreateRetryCache(clock);
            _transactionPool.NotifyAboutTx(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
                .Returns(call =>
                {
                    ValueHash256 hash = call.ArgAt<ValueHash256>(0);
                    return retry.Announced(in hash, call.ArgAt<IMessageHandler<PooledTransactionRequestMessage>>(1));
                });
            _transactionPool.TryDeferTransactionRequest(Arg.Any<ValueHash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
                .Returns(call =>
                {
                    ValueHash256 hash = call.ArgAt<ValueHash256>(0);
                    return TryDeferRequest(retry, hash, call.ArgAt<IMessageHandler<PooledTransactionRequestMessage>>(1));
                });
            _transactionPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
            {
                retry.Received(call.Arg<Transaction>().Hash!.ValueHash256);
                return AcceptTxResult.Accepted;
            });
            if (fullUnsolicitedQuota)
            {
                QueueBroadcasts(this, 448);
                Assert.That(scheduler.ScheduledFulfillFuncs, Has.Count.EqualTo(448));
            }
            int before = scheduler.ScheduledFulfillFuncs.Count;
            Dictionary<ValueHash256, Transaction> transactions = [];
            for (int i = 0; i < replies; i++)
            {
                Transaction tx = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved().TestObject;
                transactions.Add(tx.Hash!.ValueHash256, tx);
                using NewPooledTransactionHashesMessage72 announcement = new([(byte)tx.Type], [tx.GetLength()],
                    [tx.Hash], BlobCellMask.Empty.ToBytes());
                HandleZeroMessage(announcement, Eth72MessageCode.NewPooledTransactionHashes);
            }
            HashSet<long> responded = [];
            HashSet<ValueHash256> requested = [];
            RespondToSentRequests();
            Assert.That(scheduler.ScheduledFulfillFuncs.Count - before, Is.EqualTo(Math.Min(replies, 32)));
            clock.Advance(TimeSpan.FromMilliseconds(500));
            requestClock.Advance(TimeSpan.FromMilliseconds(500));
            ProcessRetryTick(retry);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_deliveredMessages.OfType<GetPooledTransactionsMessage66>().Count(),
                    Is.EqualTo(Math.Min(replies, 32)), "claimed queued responses still own their reply capacity");
                Assert.That(scheduler.ScheduledFulfillFuncs.Count - before, Is.EqualTo(Math.Min(replies, 32)));
            }
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            _transactionPool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            await scheduler.Drain(CancellationToken.None);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            requestClock.Advance(TimeSpan.FromMilliseconds(500));
            ProcessRetryTick(retry);
            RespondToSentRequests();
            await scheduler.Drain(CancellationToken.None);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            requestClock.Advance(TimeSpan.FromMilliseconds(500));
            ProcessRetryTick(retry);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(requested, Is.EquivalentTo(transactions.Keys));
                Assert.That(retry.TrackedRequestsInUse, Is.Zero);
                Assert.That(retry.ResourcesInRetryQueue, Is.Zero);
                _transactionPool.Received(replies).NotifyAboutTx(Arg.Any<ValueHash256>(),
                    Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>());
                _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            _transactionPool.Received(empty ? 0 : replies).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());

            void RespondToSentRequests()
            {
                foreach (GetPooledTransactionsMessage66 request in _deliveredMessages.OfType<GetPooledTransactionsMessage66>().ToArray())
                {
                    if (!responded.Add(request.RequestId)) continue;
                    ArrayPoolList<Transaction> responseTransactions = [with(empty ? 0 : request.EthMessage.Hashes.Count)];
                    foreach (ValueHash256 hash in request.EthMessage.Hashes)
                    {
                        Assert.That(requested.Add(hash), Is.True, "a locally unsent hash is requested once after capacity recovers");
                        if (!empty) responseTransactions.Add(transactions[hash]);
                    }
                    using PooledTransactionsMessage66 response = new(request.RequestId,
                        new PooledTransactionsMessage65(responseTransactions));
                    HandleZeroMessage(response, Eth66MessageCode.PooledTransactions);
                }
            }
        }
        finally
        {
            await scheduler.Drain(new CancellationToken(true));
        }
    }

    private static void QueueBroadcasts(Eth72ProtocolHandlerTests context, int count)
    {
        for (int i = 0; i < count; i++)
        {
            using TransactionsMessage broadcast = new(IOwnedReadOnlyList<Transaction>.Empty);
            context.HandleZeroMessage(broadcast, Eth62MessageCode.Transactions);
        }
    }
}
