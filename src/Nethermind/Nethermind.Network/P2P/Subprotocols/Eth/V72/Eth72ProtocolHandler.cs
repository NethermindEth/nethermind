// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.P2P.Utils;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;
using PooledTransactionsMessage65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;
using PooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>Implements eth/72 sparse blob transaction announcements, retrieval, and cell exchange.</summary>
/// <param name="session">The devp2p session.</param>
/// <param name="serializer">The message serializer.</param>
/// <param name="nodeStatsManager">The peer statistics manager.</param>
/// <param name="syncServer">The synchronization server.</param>
/// <param name="backgroundTaskScheduler">The bounded background task scheduler.</param>
/// <param name="txPool">The transaction pool.</param>
/// <param name="gossipPolicy">The network gossip policy.</param>
/// <param name="forkInfo">The peer fork information provider.</param>
/// <param name="logManager">The log manager.</param>
/// <param name="txPoolConfig">The transaction-pool configuration.</param>
/// <param name="specProvider">The chain specification provider.</param>
/// <param name="blobCustodyTracker">The local blob custody tracker.</param>
/// <param name="sparseBlobPoolPeerRegistry">The node-wide sparse blob peer registry.</param>
/// <param name="transactionsGossipPolicy">The optional transaction gossip policy.</param>
public class Eth72ProtocolHandler(
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
    ITxGossipPolicy? transactionsGossipPolicy = null,
    TimeProvider? timeProvider = null)
    : Eth71ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, txPoolConfig, specProvider, transactionsGossipPolicy, timeProvider), IStaticProtocolInfo
    , ISparseBlobPoolPeer
{
    private const int RequestToAnnouncementRatioThreshold = 5;
    internal const int CellRequestBurstAllowance = MaxCellsRequestHashes;
    internal const int CellRequestBurstRefillPerSecond = CellRequestBurstAllowance / 4;
    private const int MaxCountedBlobAnnouncements = 4096;
    internal const int MaxUnansweredCellRequestRestores = 2;
    private const int MaxPendingCellRequests = 1024;
    internal const int MaxSentCellRequests = 2048;
    private const int MaxPendingCellRequestWork = 4096;
    private const int MaxSentCellRequestWork = 2048;
    private const int CellServeBurstShares = 4;
    // 96 work/s permits a six-blob full mask every 8 s when cached, or 16 s with materialization.
    // Keep sustained per-peer work below the global budget so sampling peers retain capacity.
    private const int CellServeRefillShares = 8;
    private const int CellServeBurstTransactions = 2;
    private const int CellStateQueueSlack = 2;
    internal const int MaxCellsRequestHashes = 64;
    internal const int MaxCellsResponseHashes = MaxCellsRequestHashes;
    internal const int SoftCellsResponseBytes = 2 * 1024 * 1024;
    internal const int MaxCellsMessageBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan CellRequestTtl = Timeouts.Eth;
    private static readonly TimeSpan CellResponseCorrelationTtl = Timeouts.Cleanup;
    private static readonly TimeSpan PartialCellResponseBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingCellRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PendingCellStateTtl = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan SamplerProviderWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestToAnnouncementWarmup = TimeSpan.FromSeconds(60);
    private readonly int _providerProbabilityPercent = txPoolConfig.SparseBlobProviderProbabilityPercent;
    private readonly Dictionary<ValueHash256, CellRequestState> _pendingCellRequests = [];
    private readonly Dictionary<ValueHash256, CellRequestState> _sentCellRequests = [];
    private readonly Dictionary<long, SentCellRequest> _sentCellRequestIds = [];
    private PooledTransactionRequests? _sentPooledTransactionRequests;
    private readonly CellResponseHistory _bodyResponses = new();
    private readonly ClockCache<ValueHash256, byte> _countedBlobAnnouncements = new(MaxCountedBlobAnnouncements, lockPartition: 1);
    // Only transactions in our own blob pool are announced, so remembering more hashes than the pool holds buys nothing.
    private readonly Lazy<ClockCache<ValueHash256, BlobCellMask>> _announcedBlobTransactionMasks = new(
        () => new(Math.Min(txPoolConfig.BlobsSupport.IsPersistentStorage() ? txPoolConfig.PersistentBlobStorageSize : txPoolConfig.InMemoryBlobPoolSize, SparseBlobPoolPeerRegistry.MaxAnnouncementsPerPeer), lockPartition: 1));
    private readonly Lazy<ClockCache<ValueHash256, DateTimeOffset>> _partialCellResponseBackoff = new(
        static () => new(MaxPendingCellRequests, lockPartition: 1));
    private readonly Queue<CellStateKey> _pendingCellRequestOrder = new();
    private readonly Queue<CellStateKey> _sentCellRequestOrder = new();
    private readonly Lock _cellStateLock = new();
    private readonly int _maxCellsPerTransaction = GetMaxCellsPerTransaction(specProvider);
    private readonly int _cellServeTokenCapacity = Math.Max(
        GetMaxCellsPerTransaction(specProvider) * CellServeBurstTransactions,
        SparseBlobPoolPeerRegistry.GlobalCellServeTokenCapacity / CellServeBurstShares);
    private static readonly byte[] EmptyCellMaskBytes = BlobCellMask.Empty.ToBytes();
    private readonly IBlobCustodyTracker _blobCustodyTracker = blobCustodyTracker;
    private readonly ISparseBlobPoolPeerRegistry _sparseBlobPoolPeerRegistry = EnsureNotNull(sparseBlobPoolPeerRegistry, nameof(sparseBlobPoolPeerRegistry));
    private DateTimeOffset _requestRatioWarmupEndsAt;
    private readonly Lock _cellRequestRatioLock = new();
    private DateTimeOffset _cellRequestBurstRefilledAt;
    private long _forgivenCellRequests;
    private Func<ClaimedCellsResponse, CancellationToken, ValueTask>? _handleCells;
    private long _cellStateRevision;
    private long _blobAnnouncementsReceived;
    private long _cellRequestsReceived;
    private readonly CellResponseHistory _cellResponses = new();
    private int _cellQualityDisconnected;
    internal TimeProvider CellQualityClock { get; init; } = TimeProvider.System;
    internal object CellSourceToken { get; } = session.InboundReceipts;
    private int _pendingCellRequestWork;
    private int _sentCellRequestWork;
    private int _pendingCellRequestCount;
    private int _sentCellRequestCount;
    private int _pendingCellRequestQueueCount;
    private int _sentCellRequestQueueCount;
    private double _cellServeTokens;
    private DateTimeOffset _cellServeTokensUpdatedAt;

    /// <inheritdoc/>
    public override string Name => "eth72";

    /// <summary>Gets the eth/72 protocol version.</summary>
    public new static byte Version => EthVersions.Eth72;
    /// <inheritdoc/>
    public override byte ProtocolVersion => Version;
    /// <inheritdoc/>
    public override int MessageIdSpaceSize => 22;

    /// <inheritdoc/>
    public override void Init()
    {
        Session.InboundReceipts.BindClock(_timestamper);
        Session.InboundReceipts.Activate();
        _requestRatioWarmupEndsAt = _timestamper.UtcNowOffset + RequestToAnnouncementWarmup;
        _cellRequestBurstRefilledAt = _requestRatioWarmupEndsAt;
        _cellServeTokens = _cellServeTokenCapacity;
        _cellServeTokensUpdatedAt = _timestamper.UtcNowOffset;
        _handleCells = HandleCells;
        _sparseBlobPoolPeerRegistry.AddPeer(this);
        _txPool.NewPending += OnNewPending;
        base.Init();
    }

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.Length;
        switch (message.PacketType)
        {
            case Eth72MessageCode.NewPooledTransactionHashes:
                if (CanReceiveTransactions)
                {
                    if (IsTransactionGossipAllowed())
                    {
                        using NewPooledTransactionHashesMessage72 newPooledTxHashesMsg = Deserialize<NewPooledTransactionHashesMessage72>(message);
                        ReportIn(newPooledTxHashesMsg, size);
                        Handle(newPooledTxHashesMsg);
                    }
                    else
                    {
                        const string txFlooding = $"Ignoring {nameof(NewPooledTransactionHashesMessage72)} because of transaction flooding.";
                        ReportIn(txFlooding, size);
                    }
                }
                else
                {
                    const string ignored = $"{nameof(NewPooledTransactionHashesMessage72)} ignored, syncing";
                    ReportIn(ignored, size);
                }

                return true;
            case Eth66MessageCode.GetPooledTransactions:
                HandleInBackground<GetPooledTransactionsMessage66, PooledTransactionsMessage66>(message, Handle);
                return true;
            case Eth66MessageCode.PooledTransactions:
                if (CanReceiveTransactions)
                {
                    if (!Eth66RequestId.TryPeek(message.Content.Span, out long requestId))
                    {
                        throw new SubprotocolException($"Could not read request ID from {nameof(PooledTransactionsMessage66)}.");
                    }

                    if (Volatile.Read(ref _sentPooledTransactionRequests)?.TryClaim(requestId, out PooledTransactionRequests.Request? requestedHashes, message.ReceivedAt) != true)
                    {
                        ReportIn($"Uncorrelated {nameof(PooledTransactionsMessage66)} response ID {requestId} ignored", size);
                        return true;
                    }

                    using PooledTransactionRequests.Request snapshot = requestedHashes!;
                    if (!TryDeserializeTransactions(message, out PooledTransactionsMessage66 pooledTransactions, static txMessage => txMessage.EthMessage, pooledResponse: true))
                        return true;
                    ReportIn(pooledTransactions, size);
                    if (!MatchesPooledTransactionRequest(pooledTransactions.EthMessage.Transactions.AsSpan(), snapshot.Hashes))
                    {
                        ReturnUnsubmittedTransactions(pooledTransactions.EthMessage.Transactions.AsSpan());
                        pooledTransactions.Dispose();
                        IgnorePooledTransactionResponse();
                        throw new SubprotocolException($"Mismatched {nameof(PooledTransactionsMessage66)} response ID {pooledTransactions.RequestId}.");
                    }

                    RecordOmittedBodies(snapshot, pooledTransactions.EthMessage.Transactions.AsSpan());
                    HandlePooledTransactions(pooledTransactions.EthMessage, solicited: true, snapshot.Reservation,
                        snapshot.CreateResponseContext(pooledTransactions.EthMessage.Transactions.AsSpan()));
                }
                else
                {
                    if (Eth66RequestId.TryPeek(message.Content.Span, out long ignoredRequestId)
                        && Volatile.Read(ref _sentPooledTransactionRequests)?.TryClaim(ignoredRequestId,
                            out PooledTransactionRequests.Request? ignoredRequest, message.ReceivedAt) == true)
                        ignoredRequest!.Dispose();
                    IgnorePooledTransactionResponse();
                    const string ignored = $"{nameof(PooledTransactionsMessage66)} ignored, syncing";
                    ReportIn(ignored, size);
                }

                return true;
            case Eth72MessageCode.GetCells:
                HandleInBackground<GetCellsMessage72, CellsMessage72>(message, Handle);
                return true;
            case Eth72MessageCode.Cells:
                if (CanReceiveTransactions)
                {
                    if (!Eth66RequestId.TryPeek(message.Content.Span, out long requestId))
                    {
                        throw new SubprotocolException($"Could not read request ID from {nameof(CellsMessage72)}.");
                    }

                    CellRequestClaimResult claimResult = TryClaimSentCellRequest(requestId, message.ReceivedAt, out SentCellRequest sentRequest);
                    if (claimResult == CellRequestClaimResult.Unrequested)
                    {
                        throw new SubprotocolException($"Unrequested or duplicate {nameof(CellsMessage72)} response ID {requestId}.");
                    }

                    if (claimResult != CellRequestClaimResult.Claimed)
                    {
                        if (claimResult == CellRequestClaimResult.Expired)
                        {
                            RequeueClaimedCellRequest(sentRequest, CellRequeueReason.PeerUnanswered);
                        }

                        ReportIn($"Stale {nameof(CellsMessage72)} response ID {requestId} ignored", size);
                        return true;
                    }

                    (IDisposable Lease, Action<BlobCellMask> Complete)? reservation = sentRequest.ClaimReservation?.Invoke();
                    sentRequest = sentRequest with { CompleteReservation = reservation?.Complete, RequestOwnership = reservation?.Lease };
                    IDisposable? claimLease = reservation?.Lease;
                    try
                    {
                        if (!IsCurrentCellRequest(sentRequest))
                        {
                            CompleteCellsReservation(sentRequest);
                            return true;
                        }
                        if (size > sentRequest.MaxResponseBytes)
                        {
                            RequeueClaimedCellRequest(sentRequest, CellRequeueReason.PeerFault);
                            ThrowCellsResponseTooLarge(requestId, sentRequest.MaxResponseBytes, size);
                        }

                        CellsMessage72 cellsMessage;
                        try
                        {
                            cellsMessage = Deserialize<CellsMessage72>(message);
                        }
                        catch (RlpReaderException)
                        {
                            RequeueClaimedCellRequest(sentRequest, CellRequeueReason.PeerFault);
                            throw;
                        }

                        ReportIn(cellsMessage, size);
                        ClaimedCellsResponse response = new(this, cellsMessage, sentRequest, claimLease);
                        claimLease = null;
                        // Cell proof verification is too expensive for the network thread.
                        if (!BackgroundTaskScheduler.TryScheduleBackgroundTask(response, _handleCells!, solicited: true, cellsReply: true))
                        {
                            response.Dispose();
                        }
                    }
                    finally
                    {
                        claimLease?.Dispose();
                    }
                }
                else
                {
                    const string ignored = $"{nameof(CellsMessage72)} ignored, syncing";
                    ReportIn(ignored, size);
                }

                return true;
            default:
                return base.HandleMessageCore(message);
        }
    }

    protected override void SendNewTransactionCore(Transaction tx)
    {
        if (!tx.SupportsBlobs)
        {
            if (tx.CanBeBroadcast())
            {
                base.SendNewTransactionCore(tx);
            }
            else if (tx.Hash is not null)
            {
                SendAnnouncement([tx], EmptyCellMaskBytes);
            }

            return;
        }

        if (tx.Hash is not null)
        {
            SendAnnouncement([tx], GetAnnouncementMask(tx).ToBytes());
        }
    }

    protected override bool ShouldNotifyTransactionCore(Transaction tx)
    {
        if (!tx.SupportsBlobs || tx.Hash is null)
        {
            return base.ShouldNotifyTransactionCore(tx);
        }

        // Light entries persisted before the versioned consensus-size field cannot derive a safe eth/72
        // announcement size. They keep propagating via eth/68-71 sessions until they churn out.
        if (tx is LightTransaction lightTx && lightTx.GetElidedNetworkEncodingSize() == 0)
        {
            return false;
        }

        BlobCellMask mask = GetAnnouncementMask(tx);
        if (mask.IsEmpty)
        {
            return false;
        }

        ValueHash256 hash = tx.Hash.ValueHash256;
        ClockCache<ValueHash256, BlobCellMask> announcedMasks = _announcedBlobTransactionMasks.Value;
        if (announcedMasks.TryGet(hash, out BlobCellMask announcedMask)
            && (announcedMask & mask) == mask)
        {
            return false;
        }

        announcedMasks.Set(hash, announcedMask | mask);
        NotifiedTransactions.Set(hash);
        return true;
    }

    protected override void SendNewTransactionsCore(ReadOnlySpan<Transaction> txs, bool sendFullTx)
    {
        if (sendFullTx)
        {
            base.SendNewTransactionsCore(txs, sendFullTx);
            return;
        }

        using ArrayPoolList<Transaction> pendingTransactions = new(Math.Min(txs.Length, NewPooledTransactionHashesMessage72.MaxCount));
        BlobCellMask? pendingMask = null;
        foreach (Transaction tx in txs)
        {
            BlobCellMask mask = tx.SupportsBlobs ? GetAnnouncementMask(tx) : BlobCellMask.Empty;
            if (tx.SupportsBlobs && mask.IsEmpty)
            {
                continue;
            }

            if (pendingTransactions.Count == NewPooledTransactionHashesMessage72.MaxCount
                || pendingMask is { } currentMask && currentMask != mask)
            {
                FlushAnnouncement(pendingTransactions, pendingMask!.Value);
            }

            pendingMask = mask;
            pendingTransactions.Add(tx);
        }

        if (pendingMask is { } remainingMask)
        {
            FlushAnnouncement(pendingTransactions, remainingMask);
        }
    }

    private void FlushAnnouncement(ArrayPoolList<Transaction> transactions, BlobCellMask mask)
    {
        SendAnnouncement(transactions, mask.IsEmpty ? EmptyCellMaskBytes : mask.ToBytes());
        transactions.Clear();
    }

    protected override void OnDisposed()
    {
        try { SentPooledRequests.Dispose(); }
        catch (Exception exception) { Logger.Error("Failed to retire pooled transaction requests during shutdown.", exception); }
        _txPool.NewPending -= OnNewPending;
        try { _sparseBlobPoolPeerRegistry.RemovePeer(this); }
        catch (Exception exception) { Logger.Error("Failed to recover sparse transactions during peer shutdown.", exception); }
        lock (_cellStateLock)
        {
            _pendingCellRequests.Clear();
            _sentCellRequests.Clear();
            _sentCellRequestIds.Clear();
            _pendingCellRequestOrder.Clear();
            _sentCellRequestOrder.Clear();
            _pendingCellRequestWork = 0;
            _sentCellRequestWork = 0;
            _pendingCellRequestCount = 0;
            _sentCellRequestCount = 0;
            _pendingCellRequestQueueCount = 0;
            _sentCellRequestQueueCount = 0;
        }
        base.OnDisposed();
    }

    private void Handle(NewPooledTransactionHashesMessage72 msg)
    {
        ReadOnlySpan<ValueHash256> hashes = msg.Hashes.AsSpan();
        ReadOnlySpan<byte> types = msg.Types.AsSpan();
        ReadOnlySpan<int> sizes = msg.Sizes.AsSpan();

        if (hashes.Length > NewPooledTransactionHashesMessage72.MaxCount)
        {
            ThrowTooManyAnnouncementHashes(hashes.Length);
        }

        if (hashes.Length != types.Length || hashes.Length != sizes.Length)
        {
            ThrowMismatchedAnnouncementCounts(hashes.Length, types.Length, sizes.Length);
        }

        BlobCellMask announcementMask = ExtractAnnouncementMask(msg, types);
        AddNotifiedTransactions(hashes);
        TxPool.Metrics.PendingTransactionsHashesReceived += hashes.Length;

        int packetSizeLeft = TransactionsMessage.MaxPacketSize;
        int toRequestCount = 0;
        ArrayPoolList<ValueHash256>? hashesToRequest = null;

        // Resolved on the first type-6 byte, so a batch without one does no head lookup.
        bool? frameTxsEnabled = null;

        for (int i = 0; i < hashes.Length; i++)
        {
            ValueHash256 hash = hashes[i];
            TxType txType = (TxType)types[i];
            int txSize = sizes[i];
            if (!CanRequestPooledTransaction(txType, ref frameTxsEnabled) || txSize <= 0)
            {
                continue;
            }

            if (txSize > MaxAnnouncedSize(txType))
            {
                continue;
            }

            bool supportsBlobs = txType.SupportsBlobs();
            bool cellAnnouncementBackedOff = supportsBlobs && IsCellAnnouncementBackedOff(hash);
            bool tracksCells = _blobSupportEnabled && supportsBlobs && !cellAnnouncementBackedOff;
            if (tracksCells)
            {
                bool recorded = _sparseBlobPoolPeerRegistry.RecordAnnouncement(this, hash, announcementMask);
                if (!recorded
                    && (!_txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask localMask) || !localMask.IsFull))
                {
                    continue;
                }

                if (_countedBlobAnnouncements.Set(hash, 1))
                {
                    Interlocked.Increment(ref _blobAnnouncementsReceived);
                }
            }

            bool shouldRequestTx = !_txPool.IsKnown(hash)
                && _txPool.NotifyAboutTx(in hash, this) is AnnounceResult.RequestRequired;

            if (shouldRequestTx
                && (_blobSupportEnabled || !supportsBlobs))
            {
                SetTxShape(in hash, (txSize, txType));
                hashesToRequest ??= [with(Math.Min(hashes.Length - i, 256))];

                if ((txSize > packetSizeLeft && toRequestCount > 0) || toRequestCount >= 256)
                {
                    SendPooledTransactionsRequest(hashesToRequest);
                    hashesToRequest = [with(Math.Min(hashes.Length - i, 256))];
                    packetSizeLeft = TransactionsMessage.MaxPacketSize;
                    toRequestCount = 0;
                }

                hashesToRequest.Add(hash);
                packetSizeLeft -= txSize;
                toRequestCount++;
            }

            if (tracksCells)
            {
                BlobCellMask requestMask = _sparseBlobPoolPeerRegistry.GetRequestMask(hash, announcementMask, _providerProbabilityPercent);
                if (!requestMask.IsEmpty)
                {
                    RequestCellsWhenReady(hash, requestMask);
                }
            }
        }

        if (hashesToRequest is not null)
        {
            SendPooledTransactionsRequest(hashesToRequest);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyAnnouncementHashes(int count) =>
        throw new SubprotocolException(
            $"Too many hashes in {nameof(NewPooledTransactionHashesMessage72)}: {count}, maximum {NewPooledTransactionHashesMessage72.MaxCount}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMismatchedAnnouncementCounts(int hashesCount, int typesCount, int sizesCount) =>
        throw new SubprotocolException(
            $"Wrong format of {nameof(NewPooledTransactionHashesMessage72)} message. Hashes count: {hashesCount} Types count: {typesCount} Sizes count: {sizesCount}");

    private async ValueTask<PooledTransactionsMessage66> Handle(GetPooledTransactionsMessage66 getPooledTransactions, CancellationToken cancellationToken)
    {
        using GetPooledTransactionsMessage66 message = getPooledTransactions;
        PooledTransactionsMessage65 pooledTransactions = await FulfillPooledTransactionsRequest(message.EthMessage, cancellationToken);
        return new PooledTransactionsMessage66(message.RequestId, pooledTransactions);
    }

    private bool SendPooledTransactionsRequest(IOwnedReadOnlyList<ValueHash256> hashes) =>
        SendPooledTransactionRequest<GetPooledTransactionsMessage66>(hashes);

    private protected override bool TryTrackPooledTransactionRequest(P2PMessage message, PeerWorkQuota.Reservation reservation,
        out RetryRequestConfirmation? confirmation)
    {
        confirmation = null;
        if (message is not GetPooledTransactionsMessage66 request
            || !((ISparseBlobPoolPeer)this).CanRequestTransactions) return false;
        ValueHash256 sampleHash = default;
        bool hasSparseRequest = false;
        Dictionary<ValueHash256, BodyRequestAttempt>? attempts = null;
        foreach (ValueHash256 hash in request.EthMessage.Hashes.AsSpan())
        {
            if (_sparseBlobPoolPeerRegistry.CreateBodyRequestAttempt(this, hash) is { } attempt)
            {
                if (!hasSparseRequest) sampleHash = hash;
                hasSparseRequest = true;
                (attempts ??= [])[hash] = attempt;
            }
        }
        PooledTransactionRequests.RequestLifecycle lifecycle = new(Session.InboundReceipts, _timestamper, expired =>
        {
            bool unanswered = expired && hasSparseRequest && CanReceiveTransactions && !Session.IsClosing && Session.IsChannelWritable && !Session.InboundReceipts.IsClosed;
            ExceptionDispatchInfo? failure = null;
            try
            {
                if (unanswered) _bodyResponses.Record(sampleHash, useful: false, CellQualityClock);
            }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            if (attempts is null)
            {
                failure?.Throw();
                return;
            }
            foreach (KeyValuePair<ValueHash256, BodyRequestAttempt> entry in attempts)
            {
                BodyRequestAttempt attempt = entry.Value;
                try
                {
                    if (expired) attempt.ConfirmSent();
                    attempt.Complete(unanswered);
                }
                catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
            }
            failure?.Throw();
        }, () =>
        {
            if (attempts is null) return;
            foreach (KeyValuePair<ValueHash256, BodyRequestAttempt> entry in attempts) entry.Value.ConfirmSent();
        }, (hash, retainCellAnnouncement) =>
        {
            if (attempts?.TryGetValue(hash, out BodyRequestAttempt? attempt) != true) return;
            AnnouncementRestoration? canceled = _sparseBlobPoolPeerRegistry.RecordBodyUnavailable(this, hash.ToHash256(), attempt, retainCellAnnouncement);
            if (canceled is not null)
            {
                lock (_cellStateLock) ClearPendingCellRestoreLocked(hash, canceled);
            }
        });
        using ArrayPoolList<RetryRequestToken<ValueHash256>> tokens = CaptureRequestTokens(request.EthMessage.Hashes.AsSpan());
        confirmation = GetRequestConfirmation(tokens);
        return SentPooledRequests.TryAdd(request.RequestId, request.EthMessage.Hashes.AsSpan(), reservation, lifecycle, tokens.AsSpan());
    }

    private protected override void ExpirePooledTransactionRequests() => Volatile.Read(ref _sentPooledTransactionRequests)?.ExpirePending();

    private protected override void ConfirmPooledTransactionRequestSent(P2PMessage message)
    {
        if (message is GetPooledTransactionsMessage66 request) _sentPooledTransactionRequests?.ConfirmSent(request.RequestId);
    }

    private PooledTransactionRequests SentPooledRequests => LazyInitializer.EnsureInitialized(ref _sentPooledTransactionRequests,
        () => new(32, timeProvider: BackgroundTaskScheduler.TimeProvider, logger: Logger));

    private protected override void CancelPooledTransactionRequest(P2PMessage message)
    {
        if (message is GetPooledTransactionsMessage66 request) _sentPooledTransactionRequests?.Cancel(request.RequestId);
    }

    private void RecordOmittedBodies(PooledTransactionRequests.Request snapshot, ReadOnlySpan<Transaction> received)
    {
        int receivedIndex = 0;
        foreach (ValueHash256 hash in snapshot.Hashes)
        {
            // A size-limited response leaves the unserved suffix eligible for another request.
            if (received.Length != 0 && receivedIndex == received.Length) break;
            bool retainCellAnnouncement = false;
            if (receivedIndex < received.Length && received[receivedIndex].Hash?.ValueHash256 == hash)
            {
                Transaction tx = received[receivedIndex++];
                if (!tx.SupportsBlobs
                    || tx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V0 } wrapper
                    || wrapper.HasFullBlobs()
                    || _txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask localMask) && localMask.IsFull)
                {
                    continue;
                }
                retainCellAnnouncement = true;
            }
            try
            {
                snapshot.Lifecycle?.RecordOmission(hash, retainCellAnnouncement);
            }
            catch (Exception exception) { Logger.Error("Failed to retry an omitted sparse transaction body.", exception); }
        }
    }

    protected override bool CanServePooledTransaction(Transaction tx) => true;

    /// <remarks>Always misses: the stored encoding carries the type-3 blobs that eth/72 strips.</remarks>
    private protected override bool TryGetEncodedPooledTransactionToServe(in ValueHash256 hash, [NotNullWhen(true)] out IOwnedReadOnlyList<byte>? encodedTx, out bool unservable)
    {
        encodedTx = null;
        unservable = false;
        return false;
    }

    /// <summary>Whether <paramref name="tx"/> carries blobs that this protocol version cannot fetch separately.</summary>
    /// <remarks><see cref="Transaction.SupportsBlobs"/> is type-3 only, so it does not answer this for a
    /// type-6 frame transaction, which carries blobs by presence rather than by type.</remarks>
    private static bool IsBlobCarryingFrameTransaction(Transaction tx) => tx.Type is TxType.FrameTx && tx.CarriesBlobs;

    // eth/72 strips type-3 blob payloads from pooled transaction responses, so those are served from
    // the sidecar-free record; a blob-carrying type-6 is served whole, as below.
    protected override bool TryGetPooledTransactionToServe(in ValueHash256 hash, [NotNullWhen(true)] out Transaction? tx)
    {
        if (!_txPool.TryGetPendingTransactionWithoutBlobs(hash, out tx))
        {
            return false;
        }

        // A type-6 is announced at its full network size and never joins the type-3 cell protocol, so the
        // requester can neither size-match nor refill the elided record: its sidecar must travel with it.
        if (!IsBlobCarryingFrameTransaction(tx))
        {
            return true;
        }

        // Deliberately re-reads the row the call above already resolved: a serve answers this node's own
        // announcement, so both reads hit the blob pool's caches unless BlobCacheSize has churned past the hash.
        if (_txPool.TryGetPendingTransaction(hash, out tx)
            && tx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && wrapper.HasFullBlobs())
        {
            return true;
        }

        // Admission requires whole proof material, so an incomplete sidecar here means the pool lost it
        // between announcement and request; the hash is dropped and the peer sees an unanswered request.
        if (Logger.IsTrace) Logger.Trace($"{Node?.ToString("c"):hide} dropping frame tx {hash} from a pooled transactions response: its sidecar is no longer complete.");
        return false;
    }

    /// <inheritdoc/>
    public override void HandleMessage(PooledTransactionRequestMessage message)
    {
        ArrayPoolList<ValueHash256> hashes = new(1) { message.TxHash };
        SendPooledTransactionsRequest(hashes);
    }

    private Task<CellsMessage72> Handle(GetCellsMessage72 getCellsMessage, CancellationToken cancellationToken)
    {
        using GetCellsMessage72 message = getCellsMessage;
        if (ShouldDisconnectForCellRequestAbuse(message.WireHashCount))
        {
            return Task.FromResult(new CellsMessage72(message.RequestId, [], [], EmptyCellMaskBytes));
        }

        BlobCellMask requestedMask = BlobCellMask.FromBytes(message.CellMask);
        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node?.ToString("c"):hide} requested blob cells for {message.Hashes.Length} txs with mask {requestedMask}.");
        }

        int requestHashCount = Math.Min(message.Hashes.Length, MaxCellsRequestHashes);
        int responseCapacity = Math.Min(requestHashCount, MaxCellsResponseHashes);
        List<ValueHash256> responseHashes = [with(responseCapacity)];
        List<byte[][]> cellsByTx = [with(responseCapacity)];
        HashSet<ValueHash256> seenHashes = [with(requestHashCount)];
        int hashesContentLength = 0;
        int cellsContentLength = 0;

        for (int i = 0; i < requestHashCount; i++)
        {
            if (cancellationToken.IsCancellationRequested || responseHashes.Count >= MaxCellsResponseHashes)
            {
                break;
            }

            ValueHash256 valueHash = message.Hashes[i];
            if (!seenHashes.Add(valueHash))
            {
                continue;
            }

            Hash256 hash = valueHash.ToHash256();
            if (!TryGetCellsResponseMetadata(
                    hash,
                    requestedMask,
                    out bool hasMetadata,
                    out int blobCount,
                    out int materializationWork))
            {
                continue;
            }

            int nextHashesContentLength = hashesContentLength + Rlp.LengthOf(hash);
            int nextCellsContentLength = cellsContentLength + GetCellsGroupLength(blobCount, requestedMask.Count);
            if (EstimateCellsResponseLength(message.RequestId, nextHashesContentLength, nextCellsContentLength) > SoftCellsResponseBytes)
            {
                continue;
            }

            if (!TryBuildCellsResponse(
                    hash,
                    requestedMask,
                    hasMetadata,
                    blobCount,
                    materializationWork,
                    out byte[][] cells))
            {
                continue;
            }

            responseHashes.Add(valueHash);
            cellsByTx.Add(cells);
            hashesContentLength = nextHashesContentLength;
            cellsContentLength = nextCellsContentLength;
        }

        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node?.ToString("c"):hide} responding with blob cells for {responseHashes.Count} txs with mask {requestedMask}.");
        }

        return Task.FromResult(new CellsMessage72(message.RequestId, responseHashes.ToArray(), cellsByTx.ToArray(), requestedMask.ToBytes()));
    }

    private bool ShouldDisconnectForCellRequestAbuse(int requestCount)
    {
        if (requestCount <= 0)
        {
            return false;
        }

        long totalRequests = Interlocked.Add(ref _cellRequestsReceived, requestCount);
        DateTimeOffset now = _timestamper.UtcNowOffset;
        if (now < _requestRatioWarmupEndsAt)
        {
            return false;
        }

        long announcements = Volatile.Read(ref _blobAnnouncementsReceived);
        long excess;
        lock (_cellRequestRatioLock)
        {
            // A peer that fetches full blobs announces them back only after admission, so its bursts come before the
            // announcements. Excess requests drain over time, but idle time gives no credit beyond an empty bucket,
            // so only the excess from before this batch drains.
            long refill = (long)((now - _cellRequestBurstRefilledAt).TotalSeconds * CellRequestBurstRefillPerSecond);
            if (refill > 0)
            {
                _cellRequestBurstRefilledAt += TimeSpan.FromSeconds((double)refill / CellRequestBurstRefillPerSecond);
                long previousExcess = totalRequests - requestCount - _forgivenCellRequests - announcements * RequestToAnnouncementRatioThreshold;
                _forgivenCellRequests += Math.Min(refill, Math.Max(0, previousExcess));
            }

            excess = totalRequests - _forgivenCellRequests - announcements * RequestToAnnouncementRatioThreshold;
        }

        if (excess < CellRequestBurstAllowance)
        {
            return false;
        }

        Disconnect(
            DisconnectReason.UselessPeer,
            $"Sparse blob cell request-to-announce ratio exceeded: requests {totalRequests}, announcements {announcements}.");
        return true;
    }

    private ValueTask HandleCells(ClaimedCellsResponse response, CancellationToken cancellationToken)
    {
        if (!response.TryBeginHandling()) return ValueTask.CompletedTask;
        using ClaimedCellsResponse claimedResponse = response;
        if (!cancellationToken.IsCancellationRequested)
        {
            Handle(claimedResponse.Message, claimedResponse.Request);
        }
        else
        {
            RequeueClaimedCellRequest(claimedResponse.Request, CellRequeueReason.LocalBackpressure);
        }

        return ValueTask.CompletedTask;
    }

    private void Handle(CellsMessage72 message, SentCellRequest sentRequest)
    {
        if (!IsCurrentCellRequest(sentRequest))
        {
            CompleteCellsReservation(sentRequest);
            return;
        }
        if (message.RequestId != sentRequest.RequestId)
        {
            ThrowMalformedCellsResponse(sentRequest, $"Wrong request ID in {nameof(CellsMessage72)} response.");
        }

        if (message.Hashes.Length != message.Cells.Length)
        {
            ThrowMalformedCellsResponse(sentRequest, $"Wrong format of {nameof(CellsMessage72)} message. Hashes count: {message.Hashes.Length} Cells count: {message.Cells.Length}");
        }

        BlobCellMask responseMask = BlobCellMask.FromBytes(message.CellMask);
        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node?.ToString("c"):hide} received blob cells for {message.Hashes.Length} txs with mask {responseMask}.");
        }

        if (responseMask.IsEmpty)
        {
            if (message.Hashes.Length != 0 || message.Cells.Length != 0)
            {
                ThrowMalformedCellsResponse(sentRequest, $"Wrong format of {nameof(CellsMessage72)} message. Empty cell mask with non-empty response.");
            }

            RetryEmptyCellRequest(sentRequest, responseMask);
            return;
        }

        if (message.Hashes.Length == 0)
        {
            RetryEmptyCellRequest(sentRequest, responseMask);
            return;
        }

        if (message.Hashes.Length != 1 || message.Hashes[0] != sentRequest.Hash)
        {
            ThrowMalformedCellsResponse(sentRequest, $"Wrong format of {nameof(CellsMessage72)} message. Response does not match requested blob cells.");
        }

        Hash256 hash = message.Hashes[0].ToHash256();
        ValueHash256 key = hash.ValueHash256;
        BlobCellMask requestedMask = sentRequest.Mask;
        if ((responseMask & requestedMask) != responseMask)
        {
            ThrowMalformedCellsResponse(sentRequest, $"Unexpected cell mask in {nameof(CellsMessage72)} for {hash}.");
        }

        byte[][] responseCells = message.Cells[0];
        int cellsPerBlob = responseMask.Count;
        if (responseCells.Length == 0
            || responseCells.Length % cellsPerBlob != 0
            || responseCells.Length > _maxCellsPerTransaction)
        {
            ThrowMalformedCellsResponse(sentRequest, $"Wrong format of {nameof(CellsMessage72)} for {hash}. Cells count: {responseCells.Length}.");
        }

        if (!BlobCellsHelper.TryGetPresentCellMask(responseCells, responseMask, responseCells.Length / cellsPerBlob, out BlobCellMask availableMask))
        {
            ThrowMalformedCellsResponse(sentRequest, $"Invalid cell size in {nameof(CellsMessage72)} for {hash}.");
        }

        if (availableMask.IsEmpty)
        {
            ThrowMalformedCellsResponse(sentRequest, $"No requested sparse blob cells available for {hash}.");
        }

        PendingCellsBuffer pending = availableMask == responseMask
            ? new PendingCellsBuffer(responseMask, responseCells, Id)
            : new PendingCellsBuffer(availableMask, BlobCellsHelper.SelectFlattenedCells(responseCells, responseMask, availableMask, responseCells.Length / cellsPerBlob), Id);
        BlobCellMask missingMask = requestedMask.Except(availableMask);
        if (!missingMask.IsEmpty)
        {
            BackOffAndParkCellRequest(hash, missingMask, sentRequest.RequestOwnership);
        }

        CompleteCellsReservation(sentRequest);
        if (!missingMask.IsEmpty)
        {
            if (IsCurrentCellRequest(sentRequest) && sentRequest.Covered) RecordCellFailure(key);
            if (Volatile.Read(ref _cellQualityDisconnected) != 0) return;
        }
        if (!_sparseBlobPoolPeerRegistry.RecordCells(this, hash, availableMask, pending.Cells, sentRequest.RequestOwnership))
        {
            // The cells were already validated above, so a refusal here is almost always ours: the
            // early-cell byte budget, the accounting replace, or a transient submit. Throttle the
            // retry without taking away what marks the peer as a provider.
            if (IsCurrentCellRequest(sentRequest)) ParkCellRequest(key, availableMask, restoration: null);
            return;
        }


        bool applied = _sparseBlobPoolPeerRegistry.TryApplyRecordedCells(hash);
        if (!applied
            && _txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask localMask)
            && (localMask & availableMask) == availableMask)
        {
            applied = true;
        }

        if (applied)
        {
            OnPendingCellsApplied(hash, key, availableMask, missingMask);
        }
    }

    /// <summary>
    /// Parks an empty-mask response for temporary restoration, or permanently removes an announcement
    /// when a nonempty mask carries no transaction hashes.
    /// </summary>
    private void RetryEmptyCellRequest(SentCellRequest sentRequest, BlobCellMask responseMask)
    {
        if (!responseMask.IsEmpty && (responseMask & sentRequest.Mask) != responseMask)
        {
            ThrowMalformedCellsResponse(sentRequest, $"Unexpected cell mask in empty {nameof(CellsMessage72)} response.");
        }

        RequeueClaimedCellRequest(sentRequest, responseMask.IsEmpty ? CellRequeueReason.PeerEmpty : CellRequeueReason.PeerUnavailable);
    }

    private void OnPendingCellsApplied(Hash256 hash, ValueHash256 key, BlobCellMask availableMask, BlobCellMask missingMask)
    {
        RemovePendingCellRequestMask(key, availableMask);
        ClearSparseRegistryIfFull(hash);
        RequestCellsWhenReady(hash, missingMask);
        TryRequestPendingCells(hash);
    }

    private bool IsCellAnnouncementBackedOff(ValueHash256 hash)
    {
        if (!_partialCellResponseBackoff.IsValueCreated
            || !_partialCellResponseBackoff.Value.TryGet(hash, out DateTimeOffset retryAt))
        {
            return false;
        }

        if (_timestamper.UtcNowOffset < retryAt)
        {
            return true;
        }

        _partialCellResponseBackoff.Value.Delete(hash);
        return false;
    }

    protected override ValueTask HandleSlow(TransactionsRequest request, CancellationToken cancellationToken)
    {
        IOwnedReadOnlyList<Transaction> transactions = request.Transactions;
        ReadOnlySpan<Transaction> transactionsSpan = transactions.AsSpan();
        int currentIdx = request.StartIndex;
        bool isTransferred = false;
        try
        {
            bool isTrace = Logger.IsTrace;

            // Resolved on the first type-6 transaction, so a batch without one does no head lookup.
            bool? frameTxsEnabled = null;

            while (currentIdx < transactionsSpan.Length)
            {
                // A disconnect requested by an earlier transaction (e.g. an invalid one) ends the message here.
                if (Session.IsClosing)
                {
                    return ValueTask.CompletedTask;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    if (currentIdx == request.StartIndex)
                    {
                        return ValueTask.CompletedTask;
                    }

                    if (TryContinueTransactions(request, currentIdx))
                    {
                        isTransferred = true;
                    }

                    return ValueTask.CompletedTask;
                }

                Transaction tx = transactionsSpan[currentIdx];
                if (!ValidateAnnouncedPooledTransaction(tx, ref frameTxsEnabled))
                {
                    throw new InvalidPeerRequestException("invalid pooled tx type or size");
                }

                // Both submission and sparse-blob assembly may retain the transaction, even on failure.
                currentIdx++;
                RetryRequestToken<ValueHash256> token = request.ResponseContext?.GetToken(currentIdx - 1) ?? default;
                if (!tx.SupportsBlobs)
                {
                    PrepareAndSubmitTransaction(tx, isTrace, request.Solicited, token);
                }
                else if (tx.NetworkWrapper is ShardBlobNetworkWrapper { Version: ProofVersion.V0 } wrapper)
                {
                    Hash256? hash = tx.Hash;
                    if (wrapper.HasFullBlobs())
                    {
                        AcceptTxResult result = PrepareAndSubmitTransaction(tx, isTrace, request.Solicited, token);
                        if (hash is not null && (result || result == AcceptTxResult.AlreadyKnown
                            && _txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask localMask) && localMask.IsFull))
                        {
                            RemoveCellState(hash.ValueHash256);
                            _sparseBlobPoolPeerRegistry.Clear(hash);
                        }
                    }
                    else
                    {
                        ReturnUnsubmittedTransactions(transactionsSpan.Slice(currentIdx - 1, 1));
                    }
                }
                else
                {
                    PrepareAndMaybeSubmitSparseBlobTransaction(tx, isTrace);
                }
            }
        }
        finally
        {
            if (!isTransferred)
            {
                ReturnUnsubmittedTransactions(transactionsSpan[currentIdx..]);
                transactions.Dispose();
                request.ResponseContext?.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void OnNewPending(object? sender, TxEventArgs e)
    {
        Transaction tx = e.Transaction;
        if (!tx.SupportsBlobs || tx.Hash is null)
        {
            return;
        }

        TryRequestPendingCells(tx.Hash);
    }

    private void RequestCellsWhenReady(in ValueHash256 hash, BlobCellMask requestMask)
    {
        if (!CanRequestCellsNow(hash, requestMask))
        {
            // A second provider may never announce (small network, or a peer left), so maintenance
            // falls back to a full fetch when the wait ends.
            AddPendingCellRequest(hash, requestMask, _timestamper.UtcNowOffset + SamplerProviderWait, fullFetchWhenDue: true);
            return;
        }

        if (_sparseBlobPoolPeerRegistry.TryRequestCells(hash, requestMask, Id))
        {
            return;
        }

        // No event for this hash may follow, so maintenance retries it once in-flight work drops.
        AddPendingCellRequest(hash, requestMask, _timestamper.UtcNowOffset);
    }

    private bool TryGetCellsResponseMetadata(
        Hash256 hash,
        BlobCellMask requestedMask,
        out bool hasMetadata,
        out int blobCount,
        out int materializationWork)
    {
        hasMetadata = false;
        blobCount = 0;
        materializationWork = 0;
        if (requestedMask.IsEmpty)
        {
            return false;
        }

        hasMetadata = _txPool.TryGetPendingBlobCellMetadata(
            hash,
            out BlobCellMask locallyAvailableMask,
            out blobCount,
            out materializationWork);
        if (!hasMetadata)
        {
            if (!_txPool.TryGetPendingBlobCellMask(hash, out locallyAvailableMask))
            {
                return false;
            }

            blobCount = _maxCellsPerTransaction / BlobCellMask.CellCount;
            materializationWork = _maxCellsPerTransaction;
        }

        if (blobCount <= 0
            || (locallyAvailableMask & requestedMask) != requestedMask)
        {
            return false;
        }

        return true;
    }

    private bool TryBuildCellsResponse(
        Hash256 hash,
        BlobCellMask requestedMask,
        bool hasMetadata,
        int blobCount,
        int materializationWork,
        out byte[][] cells)
    {
        cells = [];

        int work = checked(blobCount * requestedMask.Count + materializationWork);

        if (!TryConsumeCellServeTokens(work))
        {
            return false;
        }

        if (!_sparseBlobPoolPeerRegistry.TryAcquireCellServeWork(work))
        {
            RefundCellServeTokens(work);
            return false;
        }

        bool responseBuilt = false;
        try
        {
            if (!_txPool.TryGetBlobCells(hash, requestedMask, out BlobCellMask availableMask, out byte[][]? availableCells)
                || (availableMask & requestedMask) != requestedMask)
            {
                return false;
            }

            int cellsPerBlob = requestedMask.Count;
            if (availableCells.Length % cellsPerBlob != 0
                || hasMetadata && availableCells.Length != blobCount * cellsPerBlob)
            {
                if (Logger.IsWarn)
                {
                    Logger.Warn($"Wrong format of local blob cells for {hash}. Expected {blobCount * cellsPerBlob} flattened cells, got {availableCells.Length}.");
                }

                return false;
            }

            cells = availableCells;
            responseBuilt = true;
            return true;
        }
        finally
        {
            if (!responseBuilt)
            {
                RefundCellServeTokens(work);
                _sparseBlobPoolPeerRegistry.RefundCellServeWork(work);
            }

            _sparseBlobPoolPeerRegistry.ReleaseCellServeWork();
        }
    }

    private static int GetCellsGroupLength(int blobCount, int cellIndexCount)
    {
        int cellCount = checked(blobCount * cellIndexCount);
        int encodedCellLength = Rlp.LengthOfByteString(CkzgLib.Ckzg.BytesPerCell, 0);
        return Rlp.LengthOfSequence(checked(cellCount * encodedCellLength));
    }

    private bool TryConsumeCellServeTokens(int work)
    {
        if (work <= 0)
        {
            return true;
        }

        lock (_cellStateLock)
        {
            DateTimeOffset now = _timestamper.UtcNowOffset;
            double elapsedSeconds = Math.Max(0, (now - _cellServeTokensUpdatedAt).TotalSeconds);
            _cellServeTokens = Math.Min(
                _cellServeTokenCapacity,
                _cellServeTokens + elapsedSeconds * SparseBlobPoolPeerRegistry.GlobalCellServeTokensPerSecond / CellServeRefillShares);
            _cellServeTokensUpdatedAt = now;
            if (_cellServeTokens < work)
            {
                return false;
            }

            _cellServeTokens -= work;
            return true;
        }
    }

    private void RefundCellServeTokens(int work)
    {
        lock (_cellStateLock)
        {
            _cellServeTokens = Math.Min(_cellServeTokenCapacity, _cellServeTokens + work);
        }
    }

    private bool SendGetCells(Hash256 hash, BlobCellMask requestMask, Func<(IDisposable Lease, Action<BlobCellMask> Complete)>? claimReservation = null)
    {
        if (Logger.IsTrace) TraceRequestingBlobCells(hash, requestMask);

        ValueHash256 key = hash.ValueHash256;
        BlobCellMask sentMask = GetSentCellRequestMask(key, requestMask);
        GetCellsMessage72 message = new([hash.ValueHash256], sentMask.ToBytes());
        AddSentCellRequest(key, sentMask, message.RequestId, claimReservation);
        try
        {
            if (SendAndGetSize(message) > 0) return true;
        }
        catch
        {
            lock (_cellStateLock) RemoveSentCellRequest(key, message.RequestId);
            throw;
        }
        lock (_cellStateLock) RemoveSentCellRequest(key, message.RequestId);
        return false;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceRequestingBlobCells(in ValueHash256 transactionHash, BlobCellMask cellMask) =>
            Logger.Trace($"{Node?.ToString("c"):hide} requesting blob cells for {transactionHash} with mask {cellMask}.");
    }

    bool ISparseBlobPoolPeer.IsClosing => Session.IsClosing;

    bool ISparseBlobPoolPeer.CanRequestTransactions => !Session.IsClosing
        && !Session.InboundReceipts.IsClosed && Session.IsChannelWritable
        && !_bodyResponses.IsBackedOff(CellQualityClock);

    void ISparseBlobPoolPeer.RecordUsefulBody(ValueHash256 hash)
    {
        try { _bodyResponses.Record(hash, useful: true, CellQualityClock); }
        catch (Exception exception) { Logger.Error("Failed to record useful transaction body evidence.", exception); }
    }

    bool ISparseBlobPoolPeer.CanRequestCells => !Session.IsClosing
        && !Session.InboundReceipts.IsClosed
        && Session.IsChannelWritable
        && !_cellResponses.IsBackedOff(CellQualityClock);

    object ISparseBlobPoolPeer.CellSourceToken => CellSourceToken;

    void ISparseBlobPoolPeer.RecordUsefulCells(ValueHash256 hash) => RecordUsefulCells(hash);

    bool ISparseBlobPoolPeer.TrySendGetCells(Hash256 hash, BlobCellMask requestMask)
        => TrySendGetCells(hash, requestMask, null);

    bool ISparseBlobPoolPeer.TrySendGetCells(Hash256 hash, BlobCellMask requestMask,
        Func<(IDisposable Lease, Action<BlobCellMask> Complete)> claimReservation)
        => TrySendGetCells(hash, requestMask, claimReservation);

    private bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask,
        Func<(IDisposable Lease, Action<BlobCellMask> Complete)>? claimReservation)
    {
        if (Session.IsClosing || Session.InboundReceipts.IsClosed || !Session.IsChannelWritable
            || requestMask.IsEmpty || _cellResponses.IsBackedOff(CellQualityClock))
        {
            return false;
        }

        return SendGetCells(hash, requestMask, claimReservation);
    }

    bool ISparseBlobPoolPeer.TrySendPooledTransactionRequest(Hash256 hash)
    {
        if (!((ISparseBlobPoolPeer)this).CanRequestTransactions)
        {
            return false;
        }

        ArrayPoolList<ValueHash256> hashes = new(1) { hash };
        return SendPooledTransactionsRequest(hashes);
    }

    void ISparseBlobPoolPeer.MaintainSparseBlobState(DateTimeOffset now) => MaintainSparseBlobState(now);

    void ISparseBlobPoolPeer.DisconnectSparseBlobPeer(DisconnectReason reason, string details) => Disconnect(reason, details);

    /// <summary>Queues cells to request once a provider and, when delayed, the retry time are available.</summary>
    /// <param name="hash">The transaction whose cells are needed.</param>
    /// <param name="requestMask">The cells to request.</param>
    /// <param name="retryAt">When the request may be re-attempted, or <see langword="null"/> for the next periodic retry.</param>
    /// <param name="restoration">The conditional announcement restoration, when one is owed.</param>
    /// <param name="alreadyAdmittedMask">Previously sent columns whose retry retains admission.</param>
    private void AddPendingCellRequest(
        ValueHash256 hash,
        BlobCellMask requestMask,
        DateTimeOffset? retryAt = null,
        AnnouncementRestoration? restoration = null,
        BlobCellMask alreadyAdmittedMask = default, bool fullFetchWhenDue = false)
    {
        if (requestMask.IsEmpty && restoration is not { IsValid: true })
        {
            return;
        }

        DateTimeOffset now = _timestamper.UtcNowOffset;
        retryAt ??= now + PendingCellRetryInterval;
        lock (_cellStateLock)
        {
            ref CellRequestState state = ref CollectionsMarshal.GetValueRefOrAddDefault(_pendingCellRequests, hash, out bool exists);
            if (exists)
            {
                CellRequestState existing = state;
                BlobCellMask combinedMask = existing.Mask | requestMask;
                _pendingCellRequestWork += combinedMask.Count - existing.Mask.Count;
                DateTimeOffset? combinedRetryAt = existing.RetryAt;
                if (retryAt is { } requestedRetryAt
                    && (combinedRetryAt is null || requestedRetryAt > combinedRetryAt))
                {
                    combinedRetryAt = requestedRetryAt;
                }

                state = existing with
                {
                    Mask = combinedMask,
                    ExpiresAt = now + PendingCellStateTtl,
                    RetryAt = combinedRetryAt,
                    Restoration = restoration is { IsValid: true } ? restoration : existing.Restoration,
                    FullFetchWhenDue = existing.FullFetchWhenDue || fullFetchWhenDue,
                    AlreadyAdmittedMask = (existing.AlreadyAdmittedMask | alreadyAdmittedMask) & combinedMask
                };
                TrimPendingCellRequests();
                return;
            }

            long revision = NextCellStateRevision();
            state = new CellRequestState(
                requestMask,
                revision,
                RequestId: 0,
                ExpiresAt: now + PendingCellStateTtl,
                RetryAt: retryAt,
                Restoration: restoration is { IsValid: true } ? restoration : null,
                    FullFetchWhenDue: fullFetchWhenDue,
                AlreadyAdmittedMask: alreadyAdmittedMask & requestMask);
            _pendingCellRequestCount++;
            _pendingCellRequestWork += requestMask.Count;
            _pendingCellRequestOrder.Enqueue(new CellStateKey(hash, revision));
            _pendingCellRequestQueueCount++;
            TrimPendingCellRequests();
        }
    }

    private BlobCellMask GetSentCellRequestMask(ValueHash256 hash, BlobCellMask requestMask)
    {
        lock (_cellStateLock)
        {
            if (_sentCellRequests.TryGetValue(hash, out CellRequestState existing))
            {
                if (existing.ExpiresAt > _timestamper.UtcNowOffset)
                {
                    return existing.Mask | requestMask;
                }

                RemoveActiveSentCellRequest(hash);
            }

            return requestMask;
        }
    }

    private CellRequestClaimResult TryClaimSentCellRequest(long requestId, DateTimeOffset? receivedAt, out SentCellRequest sentRequest)
    {
        lock (_cellStateLock)
        {
            if (!_sentCellRequestIds.Remove(requestId, out sentRequest))
            {
                return CellRequestClaimResult.Unrequested;
            }

            if (!_sentCellRequests.TryGetValue(sentRequest.Hash, out CellRequestState existing)
                || existing.RequestId != requestId)
            {
                return CellRequestClaimResult.Stale;
            }

            RemoveActiveSentCellRequest(sentRequest.Hash);
            DateTimeOffset now = receivedAt ?? _timestamper.UtcNowOffset;
            return existing.ExpiresAt <= now || sentRequest.CorrelationExpiresAt <= now
                ? CellRequestClaimResult.Expired
                : CellRequestClaimResult.Claimed;
        }
    }

    private void ThrowMalformedCellsResponse(SentCellRequest sentRequest, string message)
    {

        RequeueClaimedCellRequest(sentRequest, CellRequeueReason.PeerFault);
        throw new InvalidPeerRequestException(message);
    }

    private void CompleteCellsReservation(SentCellRequest sentRequest)
    {
        if (sentRequest.CompleteReservation is { } complete) complete(sentRequest.Mask);
        else if (sentRequest.ClaimReservation is { } claim)
        {
            (IDisposable Lease, Action<BlobCellMask> Complete) reservation = claim();
            using (reservation.Lease) reservation.Complete(sentRequest.Mask);
        }
        else _sparseBlobPoolPeerRegistry.OnCellsRequestCompleted(sentRequest.Hash.ToHash256(), sentRequest.Mask, this);
    }

    private static bool IsCurrentCellRequest(SentCellRequest sentRequest)
        => sentRequest.RequestOwnership is null || sentRequest.RequestOwnership is ICellRequestOwnership { IsCurrent: true };

    private void RequeueClaimedCellRequest(SentCellRequest sentRequest, CellRequeueReason reason)
    {
        (IDisposable Lease, Action<BlobCellMask> Complete)? claimed = sentRequest.CompleteReservation is null
            ? sentRequest.ClaimReservation?.Invoke() : null;
        using IDisposable? ownedClaim = claimed?.Lease;
        if (claimed is { } currentClaim)
            sentRequest = sentRequest with { CompleteReservation = currentClaim.Complete, RequestOwnership = currentClaim.Lease };
        if (!IsCurrentCellRequest(sentRequest))
        {
            CompleteCellsReservation(sentRequest);
            return;
        }
        Hash256 requestedHash = sentRequest.Hash.ToHash256();
        CompleteCellsReservation(sentRequest);
        switch (reason)
        {
            case CellRequeueReason.PeerFault:
                _sparseBlobPoolPeerRegistry.RemovePeer(this);
                RequestCellsWhenReady(sentRequest.Hash, sentRequest.Mask);
                break;
            case CellRequeueReason.LocalBackpressure:
            case CellRequeueReason.PeerUnanswered when !Session.IsChannelWritable:
                // Throttling the retry is still right, but the announcement stays: it is what marks
                // the peer as a provider node-wide, and we, not the peer, dropped the response.
                ParkCellRequest(sentRequest.Hash, sentRequest.Mask, restoration: null);
                break;
            case CellRequeueReason.PeerUnanswered:
                AnnouncementRestoration? timedOut = _sparseBlobPoolPeerRegistry.RemoveAnnouncementPermanently(this, requestedHash, sentRequest.RequestOwnership);
                if (timedOut is not null)
                {
                    lock (_cellStateLock) ClearPendingCellRestoreLocked(sentRequest.Hash, timedOut);
                }
                if (!IsCurrentCellRequest(sentRequest)) break;
                RecordCellFailure(sentRequest.Hash);
                AddPendingCellRequest(sentRequest.Hash, sentRequest.Mask);
                TryRequestPendingCells(requestedHash);
                break;
            case CellRequeueReason.PeerUnavailable:
                AnnouncementRestoration? canceled = _sparseBlobPoolPeerRegistry.RemoveAnnouncementPermanently(this, requestedHash, sentRequest.RequestOwnership);
                if (canceled is not null)
                {
                    lock (_cellStateLock) ClearPendingCellRestoreLocked(sentRequest.Hash, canceled);
                }
                if (!IsCurrentCellRequest(sentRequest)) break;
                AddPendingCellRequest(sentRequest.Hash, sentRequest.Mask, alreadyAdmittedMask: sentRequest.Mask);
                TryRequestPendingCells(requestedHash);
                break;
            case CellRequeueReason.PeerEmpty:
                BackOffAndParkCellRequest(requestedHash, sentRequest.Mask, sentRequest.RequestOwnership, recordFailure: sentRequest.Covered);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
        }
    }

    /// <summary>
    /// Backs the peer off for a transaction and parks the cells for a later retry, scheduling the
    /// announcement to be restored so a temporary miss does not permanently lose the provider.
    /// </summary>
    /// <param name="hash">The transaction whose cells are parked.</param>
    /// <param name="parkMask">The cells to request again once the backoff elapses.</param>
    /// <remarks>
    /// The mask restored is the one the peer announced, not the one we requested:
    /// <see cref="ISparseBlobPoolPeerRegistry.RecordAnnouncement"/> overwrites a peer's mask, so
    /// restoring the narrower request mask would downgrade a full provider to a partial one and can
    /// starve samplers gated on <see cref="SparseBlobPoolPeerRegistry.MinSamplerFullProviderAnnouncements"/>.
    /// </remarks>
    private void BackOffAndParkCellRequest(Hash256 hash, BlobCellMask parkMask, IDisposable? requestOwnership = null, bool recordFailure = false)
    {
        _sparseBlobPoolPeerRegistry.TryBeginAnnouncementRestore(this, hash, out AnnouncementRestoration? restoration, requestOwnership);
        if (requestOwnership is ICellRequestOwnership { IsCurrent: false }) return;
        if (recordFailure) RecordCellFailure(hash.ValueHash256);
        ParkCellRequest(hash.ValueHash256, parkMask, restoration);
    }

    /// <summary>Holds cells back until the retry falls due.</summary>
    private void ParkCellRequest(ValueHash256 key, BlobCellMask parkMask, AnnouncementRestoration? restoration)
    {
        DateTimeOffset retryAt = _timestamper.UtcNowOffset + PartialCellResponseBackoff;
        _partialCellResponseBackoff.Value.Set(key, retryAt);
        AddPendingCellRequest(key, parkMask, retryAt, restoration);
    }

    internal void RecordUsefulCells(ValueHash256 hash)
    {
        try { _cellResponses.Record(hash, useful: true, CellQualityClock); }
        catch (Exception exception) { Logger.Error("Failed to record useful cell evidence.", exception); }
    }

    private void RecordCellFailure(ValueHash256 hash)
    {
        int failures;
        bool recentUseful;
        try
        {
            (failures, _) = _cellResponses.Record(hash, useful: false, CellQualityClock);
            recentUseful = failures >= 8 && _cellResponses.HasRecentUseful(CellQualityClock);
        }
        catch (Exception exception)
        {
            Logger.Error("Failed to record cell failure evidence.", exception);
            return;
        }
        if (failures >= 8 && !recentUseful && Interlocked.Exchange(ref _cellQualityDisconnected, 1) == 0)
        {
            _sparseBlobPoolPeerRegistry.RemovePeer(this);
            Disconnect(DisconnectReason.UselessPeer, "Repeated unanswered or covered empty sparse blob cell requests.");
        }
    }

    private void AddSentCellRequest(ValueHash256 hash, BlobCellMask requestMask, long requestId,
        Func<(IDisposable Lease, Action<BlobCellMask> Complete)>? claimReservation)
    {
        bool covered = _sparseBlobPoolPeerRegistry.CoversRequest(this, hash, requestMask);
        lock (_cellStateLock)
        {
            DateTimeOffset now = _timestamper.UtcNowOffset;
            ref CellRequestState state = ref CollectionsMarshal.GetValueRefOrAddDefault(_sentCellRequests, hash, out bool exists);
            if (exists)
            {
                CellRequestState existing = state;
                BlobCellMask combinedMask = existing.Mask | requestMask;
                _sentCellRequestWork += combinedMask.Count - existing.Mask.Count;
                DateTimeOffset updatedExpiresAt = now + CellRequestTtl;
                int updatedMaxResponseBytes = GetExpectedCellsResponseBound(requestId, combinedMask);
                state = existing with { Mask = combinedMask, RequestId = requestId, ExpiresAt = updatedExpiresAt, ExpiryWatermark = null };
                _sentCellRequestIds[requestId] = new SentCellRequest(
                    hash,
                    combinedMask,
                    requestId,
                    now + CellResponseCorrelationTtl,
                    updatedMaxResponseBytes,
                    covered,
                    ClaimReservation: claimReservation);
                TrimSentCellRequests();
                return;
            }

            long revision = NextCellStateRevision();
            DateTimeOffset expiresAt = now + CellRequestTtl;
            int maxResponseBytes = GetExpectedCellsResponseBound(requestId, requestMask);
            state = new CellRequestState(
                requestMask,
                revision,
                requestId,
                expiresAt,
                RetryAt: null,
                Restoration: null);
            _sentCellRequestIds[requestId] = new SentCellRequest(
                hash,
                requestMask,
                requestId,
                now + CellResponseCorrelationTtl,
                maxResponseBytes,
                covered,
                ClaimReservation: claimReservation);
            _sentCellRequestCount++;
            _sentCellRequestWork += requestMask.Count;
            _sentCellRequestOrder.Enqueue(new CellStateKey(hash, revision));
            _sentCellRequestQueueCount++;
            TrimSentCellRequests();
        }
    }

    private void TrimPendingCellRequests()
    {
        while ((_pendingCellRequestCount > MaxPendingCellRequests || _pendingCellRequestWork > MaxPendingCellRequestWork)
            && _pendingCellRequestOrder.TryDequeue(out CellStateKey key))
        {
            _pendingCellRequestQueueCount--;
            if (_pendingCellRequests.TryGetValue(key.Hash, out CellRequestState state)
                && state.Revision == key.Revision)
            {
                RemovePendingCellRequestLocked(key.Hash);
            }
        }

        if (_pendingCellRequestQueueCount > MaxPendingCellRequests * CellStateQueueSlack)
        {
            _pendingCellRequestOrder.Clear();
            _pendingCellRequestQueueCount = 0;
            foreach (KeyValuePair<ValueHash256, CellRequestState> entry in _pendingCellRequests)
            {
                _pendingCellRequestOrder.Enqueue(new CellStateKey(entry.Key, entry.Value.Revision));
                _pendingCellRequestQueueCount++;
            }
        }
    }

    private void TrimSentCellRequests()
    {
        while ((_sentCellRequestCount > MaxSentCellRequests || _sentCellRequestWork > MaxSentCellRequestWork)
            && _sentCellRequestOrder.TryDequeue(out CellStateKey key))
        {
            _sentCellRequestQueueCount--;
            if (_sentCellRequests.TryGetValue(key.Hash, out CellRequestState state)
                && state.Revision == key.Revision)
            {
                RemoveActiveSentCellRequest(key.Hash);
            }
        }

        if (_sentCellRequestQueueCount > MaxSentCellRequests * CellStateQueueSlack)
        {
            _sentCellRequestOrder.Clear();
            _sentCellRequestQueueCount = 0;
            foreach (KeyValuePair<ValueHash256, CellRequestState> entry in _sentCellRequests)
            {
                _sentCellRequestOrder.Enqueue(new CellStateKey(entry.Key, entry.Value.Revision));
                _sentCellRequestQueueCount++;
            }
        }
    }

    private void RemovePendingCellRequestMask(ValueHash256 hash, BlobCellMask completedMask, long expectedRevision = 0)
    {
        lock (_cellStateLock)
        {
            ref CellRequestState state = ref CollectionsMarshal.GetValueRefOrNullRef(_pendingCellRequests, hash);
            if (Unsafe.IsNullRef(ref state) || (expectedRevision != 0 && state.Revision != expectedRevision))
            {
                return;
            }

            BlobCellMask remainingMask = state.Mask.Except(completedMask);
            if (remainingMask == state.Mask)
            {
                return;
            }

            // An entry that still owes an announcement restore outlives the cells it was parked for,
            // otherwise placing the request elsewhere would drop the provider permanently.
            if (remainingMask.IsEmpty && state.Restoration is null)
            {
                RemovePendingCellRequestLocked(hash);
                return;
            }

            _pendingCellRequestWork -= state.Mask.Count - remainingMask.Count;
            state = state with { Mask = remainingMask, AlreadyAdmittedMask = state.AlreadyAdmittedMask & remainingMask };
        }
    }

    private void RemoveCellState(ValueHash256 hash)
    {
        lock (_cellStateLock)
        {
            RemovePendingCellRequestLocked(hash);
            if (_sentCellRequests.TryGetValue(hash, out CellRequestState sentState))
            {
                RemoveSentCellRequest(hash, sentState.RequestId);
            }
        }
    }

    private void RemoveSentCellRequest(ValueHash256 hash, long requestId)
    {
        if (_sentCellRequests.TryGetValue(hash, out CellRequestState state) && state.RequestId == requestId)
            RemoveActiveSentCellRequest(hash);
        _sentCellRequestIds.Remove(requestId);
    }

    private bool RemoveActiveSentCellRequest(ValueHash256 hash)
    {
        if (!_sentCellRequests.Remove(hash, out CellRequestState state))
        {
            return false;
        }

        _sentCellRequestCount--;
        _sentCellRequestWork -= state.Mask.Count;
        return true;
    }

    /// <summary>Clears only the restoration captured by the completed operation.</summary>
    private void ClearPendingCellRestoreLocked(ValueHash256 hash, AnnouncementRestoration expected)
    {
        ref CellRequestState state = ref CollectionsMarshal.GetValueRefOrNullRef(_pendingCellRequests, hash);
        if (Unsafe.IsNullRef(ref state) || !ReferenceEquals(state.Restoration, expected)) return;
        if (state.Mask.IsEmpty)
        {
            RemovePendingCellRequestLocked(hash);
            return;
        }
        state = state with { Restoration = null };
    }

    private void RemovePendingCellRequestLocked(ValueHash256 hash)
    {
        if (_pendingCellRequests.Remove(hash, out CellRequestState state))
        {
            _pendingCellRequestCount--;
            _pendingCellRequestWork -= state.Mask.Count;
        }
    }

    private void MaintainSparseBlobState(DateTimeOffset now)
    {
        Volatile.Read(ref _sentPooledTransactionRequests)?.ExpirePending();
        PacketReceiptTracker receipts = Session.InboundReceipts;
        bool receiptQueueClosed = receipts.IsClosed;
        List<SentCellRequest>? expiredSentRequests = null;
        List<(ValueHash256 Hash, AnnouncementRestoration? Restoration)>? dueRetries = null;
        List<ValueHash256>? expiredPendingKeys = null;
        List<ValueHash256>? expiredSentKeys = null;
        List<long>? expiredCorrelationKeys = null;
        lock (_cellStateLock)
        {
            foreach (KeyValuePair<ValueHash256, CellRequestState> entry in _pendingCellRequests)
            {
                if (entry.Value.ExpiresAt <= now)
                {
                    (expiredPendingKeys ??= []).Add(entry.Key);
                    // Expiry must not swallow an announcement we took away: a restore the registry
                    // kept refusing gets one final attempt on the way out.
                    if (entry.Value.Restoration is not null)
                    {
                        (dueRetries ??= []).Add((entry.Key, entry.Value.Restoration));
                    }
                }
                else if (entry.Value.RetryAt is { } retryAt && retryAt <= now)
                {
                    (dueRetries ??= []).Add((entry.Key, entry.Value.Restoration));
                    int retryAttempt = Math.Min(entry.Value.RetryAttempt + 1, 2);
                    _pendingCellRequests[entry.Key] = entry.Value with
                    {
                        RetryAt = now + TimeSpan.FromSeconds(1 << retryAttempt),
                        RetryAttempt = retryAttempt
                    };
                }
            }

            foreach (KeyValuePair<ValueHash256, CellRequestState> entry in _sentCellRequests)
            {
                CellRequestState state = entry.Value;
                if (receiptQueueClosed || state.ExpiresAt <= now)
                {
                    if (!receiptQueueClosed)
                    {
                        long watermark = state.ExpiryWatermark ?? receipts.CaptureWatermark();
                        if (state.ExpiryWatermark is null)
                            _sentCellRequests[entry.Key] = state with { ExpiryWatermark = watermark };
                        if (!receipts.HasCompleted(watermark)) continue;
                    }
                    if (_sentCellRequestIds.TryGetValue(state.RequestId, out SentCellRequest request))
                    {
                        (expiredSentRequests ??= []).Add(request);
                    }

                    (expiredSentKeys ??= []).Add(entry.Key);
                }
            }

            foreach (KeyValuePair<long, SentCellRequest> entry in _sentCellRequestIds)
            {
                if (receiptQueueClosed || entry.Value.CorrelationExpiresAt <= now)
                {
                    if (!receiptQueueClosed
                        && _sentCellRequests.TryGetValue(entry.Value.Hash, out CellRequestState active)
                        && active.RequestId == entry.Key
                        && active.ExpiryWatermark is { } watermark
                        && !receipts.HasCompleted(watermark)) continue;
                    (expiredCorrelationKeys ??= []).Add(entry.Key);
                }
            }

            if (expiredPendingKeys is not null)
            {
                for (int i = 0; i < expiredPendingKeys.Count; i++)
                {
                    RemovePendingCellRequestLocked(expiredPendingKeys[i]);
                }
            }

            if (expiredSentKeys is not null)
            {
                for (int i = 0; i < expiredSentKeys.Count; i++)
                {
                    RemoveActiveSentCellRequest(expiredSentKeys[i]);
                }
            }

            if (expiredCorrelationKeys is not null)
            {
                for (int i = 0; i < expiredCorrelationKeys.Count; i++)
                {
                    _sentCellRequestIds.Remove(expiredCorrelationKeys[i]);
                }
            }

            TrimPendingCellRequests();
            TrimSentCellRequests();
        }

        if (dueRetries is not null)
        {
            for (int i = 0; i < dueRetries.Count; i++)
            {
                (ValueHash256 key, AnnouncementRestoration? restoration) = dueRetries[i];
                Hash256 hash = key.ToHash256();
                if (restoration is not null)
                {
                    bool restored = _sparseBlobPoolPeerRegistry.TryRestoreAnnouncement(this, hash, restoration);
                    if (restored || !restoration.IsValid)
                    {
                        lock (_cellStateLock) ClearPendingCellRestoreLocked(key, restoration);
                    }
                }

                TryRequestPendingCells(key, retryDue: true);
            }
        }

        if (expiredSentRequests is not null)
        {
            for (int i = 0; i < expiredSentRequests.Count; i++)
            {
                RequeueClaimedCellRequest(expiredSentRequests[i], receiptQueueClosed || receipts.IsClosed
                    ? CellRequeueReason.LocalBackpressure
                    : CellRequeueReason.PeerUnanswered);
            }
        }
    }

    private void ClearSparseRegistryIfFull(Hash256 hash)
    {
        if (_txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask localMask) && localMask.IsFull)
        {
            _sparseBlobPoolPeerRegistry.Clear(hash);
        }
    }

    private long NextCellStateRevision() => ++_cellStateRevision;

    internal static int GetMaxCellsPerTransaction(ISpecProvider specProvider)
    {
        ulong maxBlobsPerTx = specProvider.GetFinalSpec()?.MaxBlobsPerTx ?? Eip4844Constants.DefaultMaxBlobCount;
        if (maxBlobsPerTx == 0)
        {
            maxBlobsPerTx = Eip4844Constants.DefaultMaxBlobCount;
        }

        maxBlobsPerTx = Math.Min(maxBlobsPerTx, (ulong)int.MaxValue / BlobCellMask.CellCount);
        return checked((int)maxBlobsPerTx * BlobCellMask.CellCount);
    }

    private int GetExpectedCellsResponseBound(long requestId, BlobCellMask requestMask)
    {
        int maxBlobCount = _maxCellsPerTransaction / BlobCellMask.CellCount;
        int cellGroupLength = GetCellsGroupLength(maxBlobCount, requestMask.Count);
        return EstimateCellsResponseLength(requestId, Rlp.LengthOf(Hash256.Zero), cellGroupLength);
    }

    private static int EstimateCellsResponseLength(long requestId, int hashesContentLength, int cellsContentLength)
    {
        int contentLength = Rlp.LengthOf(requestId)
            + Rlp.LengthOfSequence(hashesContentLength)
            + Rlp.LengthOfSequence(cellsContentLength)
            + Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, 0);
        return Rlp.LengthOfSequence(contentLength);
    }

    private BlobCellMask ExtractAnnouncementMask(NewPooledTransactionHashesMessage72 message, ReadOnlySpan<byte> types)
    {
        bool containsBlobTransaction = false;
        for (int i = 0; i < types.Length; i++)
        {
            if (((TxType)types[i]).SupportsBlobs())
            {
                containsBlobTransaction = true;
                break;
            }
        }

        if (!containsBlobTransaction)
        {
            return BlobCellMask.Empty;
        }

        if (message.CellMask.Length != BlobCellMask.FixedByteLength)
        {
            throw new SubprotocolException(
                $"Wrong format of {nameof(NewPooledTransactionHashesMessage72)} message. Blob announcements must include a {BlobCellMask.FixedByteLength}-byte cell mask.");
        }

        BlobCellMask cellMask = BlobCellMask.FromBytes(message.CellMask);
        if (cellMask.IsEmpty)
        {
            throw new SubprotocolException(
                $"Wrong format of {nameof(NewPooledTransactionHashesMessage72)} message. Blob announcements must include a non-empty cell mask.");
        }

        return cellMask;
    }

    private void PrepareAndMaybeSubmitSparseBlobTransaction(Transaction tx, bool isTrace)
    {
        PooledBlobBuffers.Disown(tx);
        if (tx.Hash is not null)
        {
            NotifiedTransactions.Set(tx.Hash.ValueHash256);
        }

        AcceptTxResult? accepted = _sparseBlobPoolPeerRegistry.RecordTransaction(this, tx);
        if (accepted.HasValue)
        {
            ReportReceivedTransaction(accepted.Value);
            if (isTrace)
            {
                Logger.Trace($"{Node?.ToString("c"):hide} sent sparse blob tx {tx.Hash} and it was {accepted.Value} (chain ID = {tx.Signature?.ChainId})");
            }
        }

        if (tx.Hash is not null)
        {
            TryRequestPendingCells(tx.Hash);
        }
    }

    private bool ValidateAnnouncedPooledTransaction(Transaction tx, ref bool? frameTxsEnabled)
    {
        if (!CanRequestPooledTransaction(tx.Type, ref frameTxsEnabled))
        {
            return false;
        }

        if (TryTakeTxShape(tx.Hash!, out (int Size, TxType Type) txShape)
            && (!MatchesAnnouncedTransactionSize(tx, txShape.Size) || tx.Type != txShape.Type))
        {
            return false;
        }

        if (!tx.Type.SupportsBlobs())
        {
            // A type-6 sidecar is verified here because it arrives whole; the sparse cell protocol that
            // defers type-3 verification to a later Cells response does not cover it.
            return !IsBlobCarryingFrameTransaction(tx) || ValidateFullPooledBlobSidecar(tx);
        }

        return tx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && wrapper.Version switch
            {
                ProofVersion.V0 => ValidatePooledBlobTransactionV0(tx, wrapper),
                ProofVersion.V1 => ValidateSparsePooledBlobTransaction(tx),
                _ => false
            };
    }

    /// <summary>Verifies a sidecar that arrived complete, as a blob-carrying type-6 always must.</summary>
    /// <remarks>Unlike the type-3 validators this is version-generic, because the proof count per blob
    /// differs between wrapper versions and only the wrapper's own proofs manager knows it.</remarks>
    private static bool ValidateFullPooledBlobSidecar(Transaction tx)
    {
        if (tx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V0 or ProofVersion.V1 } wrapper
            || !wrapper.HasFullBlobs()
            || tx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes
            || wrapper.Commitments.Length != blobVersionedHashes.Length)
        {
            return false;
        }

        return ValidateLengthsAndHashes(wrapper, blobVersionedHashes);
    }

    /// <summary>Checks a sidecar's proof material against the transaction's versioned hashes.</summary>
    /// <remarks>Dispatched on the wrapper's own version, so the per-version length rules (one proof per blob
    /// under V0, one per cell under V1) stay with the proofs manager rather than the caller's guard.</remarks>
    private static bool ValidateLengthsAndHashes(ShardBlobNetworkWrapper wrapper, byte[][] blobVersionedHashes)
    {
        IBlobProofsManager proofsVerifier = IBlobProofsManager.For(wrapper.Version);
        return proofsVerifier.ValidateLengths(wrapper)
            && proofsVerifier.ValidateHashes(wrapper, blobVersionedHashes);
    }

    private static bool ValidatePooledBlobTransactionV0(Transaction tx, ShardBlobNetworkWrapper wrapper)
    {
        if (tx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes
            || wrapper.Commitments.Length != blobVersionedHashes.Length
            || wrapper.Proofs.Length != blobVersionedHashes.Length * ShardBlobNetworkWrapper.BytesPerProof)
        {
            return false;
        }

        IBlobProofsManager proofsVerifier = IBlobProofsManager.For(wrapper.Version);
        if (!proofsVerifier.ValidateHashes(wrapper, blobVersionedHashes))
        {
            return false;
        }

        if (wrapper.HasFullBlobs())
        {
            return proofsVerifier.ValidateLengths(wrapper);
        }

        if (wrapper.Blobs.Length != 0 || wrapper.Cells is not null || !wrapper.CellMask.IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            if (wrapper.Commitments[i].Length != CkzgLib.Ckzg.BytesPerCommitment)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateSparsePooledBlobTransaction(Transaction tx)
    {
        if (tx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1, Blobs.Length: 0, Commitments.Length: > 0, Proofs.Length: > 0 } wrapper
            || tx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes
            || wrapper.Commitments.Length != blobVersionedHashes.Length)
        {
            return false;
        }

        return ValidateLengthsAndHashes(wrapper, blobVersionedHashes);
    }

    private bool CanRequestCellsNow(in ValueHash256 hash, BlobCellMask requestMask)
    {
        if (IsSupernode || requestMask.IsFull)
        {
            return true;
        }

        bool hasValidatedTransaction = _sparseBlobPoolPeerRegistry.HasRecordedTransaction(hash)
            || _txPool.TryGetPendingBlobCellMask(hash, out _);
        return hasValidatedTransaction
            && _sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(hash) >= SparseBlobPoolPeerRegistry.MinSamplerFullProviderAnnouncements;
    }

    private bool TryRequestPendingCells(in ValueHash256 hash, bool retryDue = false)
    {
        CellRequestState pendingRequestState;
        lock (_cellStateLock)
        {
            if (!_pendingCellRequests.TryGetValue(hash, out pendingRequestState)
                || pendingRequestState.Mask.IsEmpty)
            {
                return false;
            }
        }

        BlobCellMask requestMask = pendingRequestState.AlreadyAdmittedMask & pendingRequestState.Mask;
        BlobCellMask newMask = pendingRequestState.Mask.Except(requestMask);
        if (!newMask.IsEmpty && CanRequestCellsNow(hash, newMask)) requestMask |= newMask;
        if (requestMask.IsEmpty && retryDue && pendingRequestState.FullFetchWhenDue
            && _sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(hash) is > 0 and < SparseBlobPoolPeerRegistry.MinSamplerFullProviderAnnouncements)
            requestMask = BlobCellMask.Full;
        if (requestMask.IsEmpty || !_sparseBlobPoolPeerRegistry.TryRequestCells(hash, requestMask, Id)) return false;

        RemovePendingCellRequestMask(hash, requestMask, pendingRequestState.Revision);
        return true;
    }

    private bool IsSupernode => SparseBlobPoolPeerRegistry.HasSupernodeCustody(_blobCustodyTracker.CurrentMask);

    private BlobCellMask GetAnnouncementMask(Transaction tx)
    {
        if (tx.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
        {
            return wrapper.Version == ProofVersion.V1 ? wrapper.GetAvailableCellMask() : BlobCellMask.Full;
        }

        if (tx is LightTransaction lightTx)
        {
            BlobCellMask availableMask = lightTx.BlobCellMask;
            return lightTx.ProofVersion == ProofVersion.V1 ? availableMask : BlobCellMask.Full;
        }

        return BlobCellMask.Full;
    }

    private void SendAnnouncement(IReadOnlyList<Transaction> txs, byte[] cellMask)
    {
        int count = txs.Count;
        ArrayPoolList<byte> types = [with(count)];
        ArrayPoolList<int> sizes = [with(count)];
        ArrayPoolList<ValueHash256> hashes = [with(count)];

        for (int i = 0; i < count; i++)
        {
            Transaction tx = txs[i];
            int announcementSize = GetAnnouncementSize(tx);
            if (announcementSize <= 0)
            {
                continue;
            }

            types.Add((byte)tx.Type);
            sizes.Add(announcementSize);
            hashes.Add(tx.Hash!.ValueHash256);
            TxPool.Metrics.PendingTransactionsHashesSent++;
        }

        if (hashes.Count != 0)
        {
            Send(new NewPooledTransactionHashesMessage72(types, sizes, hashes, cellMask));
        }
        else
        {
            types.Dispose();
            sizes.Dispose();
            hashes.Dispose();
        }
    }

    private static int GetAnnouncementSize(Transaction tx)
    {
        if (!tx.SupportsBlobs)
        {
            return tx.GetLength();
        }

        // eth/72 peers size-check the blob-elided bytes served in PooledTransactions and geth disconnects for
        // larger mismatches. The current devp2p text still says consensus encoding; its correction is tracked at
        // https://github.com/ethereum/devp2p/pull/281
        return tx.GetElidedNetworkEncodingSize();
    }

    // Stay lenient on receive: peers still announcing the bare consensus size (or the geth wrapper estimate)
    // are accepted rather than disconnected, so a mixed-version network keeps propagating blob txs.
    // https://github.com/healthykim/go-ethereum/blob/fdce1ff22f2c2bde0e6a8d1921168f4b7036781f/eth/protocols/eth/broadcast.go#L140-L143
    protected override bool MatchesAnnouncedTransactionSize(Transaction tx, int announcedSize)
        => MatchesAnnouncedSize(tx, announcedSize)
        || tx.SupportsBlobs && tx.GetLength(shouldCountBlobs: false) == announcedSize;

    private readonly record struct SentCellRequest(
        ValueHash256 Hash,
        BlobCellMask Mask,
        long RequestId,
        DateTimeOffset CorrelationExpiresAt,
        int MaxResponseBytes,
        bool Covered,
        Action<BlobCellMask>? CompleteReservation = null,
        Func<(IDisposable Lease, Action<BlobCellMask> Complete)>? ClaimReservation = null,
        IDisposable? RequestOwnership = null);

    /// <remarks>
    /// ExpiryWatermark preserves correlation until queued receipts drain. The registry's InFlightReceiptGuards
    /// protect unclaimed reservations; a successful claim transfers protection to ClaimedCellReservations through
    /// background processing. Each guard covers a different stage between receipt arrival and processing completion.
    /// </remarks>
    private readonly record struct CellRequestState(
        BlobCellMask Mask,
        long Revision,
        long RequestId,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RetryAt,
        AnnouncementRestoration? Restoration,
        BlobCellMask AlreadyAdmittedMask = default,
        int RetryAttempt = 0,
        long? ExpiryWatermark = null,
        bool FullFetchWhenDue = false);

    /// <summary>Why an in-flight cell request is being returned to the pending queue.</summary>
    private enum CellRequeueReason
    {
        /// <summary>The peer missed its response deadline.</summary>
        PeerUnanswered,

        /// <summary>The peer answered without any requested cells.</summary>
        PeerEmpty,

        /// <summary>The peer reported temporary unavailability; a fresh announcement is required.</summary>
        PeerUnavailable,

        /// <summary>The response was dropped locally, e.g. the background scheduler was saturated.</summary>
        LocalBackpressure,

        /// <summary>The peer broke the protocol and is being disconnected.</summary>
        PeerFault,
    }

    private readonly record struct CellStateKey(ValueHash256 Hash, long Revision);

    private enum CellRequestClaimResult
    {
        Claimed,
        Stale,
        Expired,
        Unrequested
    }

    private sealed class ClaimedCellsResponse(Eth72ProtocolHandler handler, CellsMessage72 message, SentCellRequest request, IDisposable? reservation) : IDisposable
    {
        private CellsMessage72? _message = message;
        private IDisposable? _reservation = reservation;
        private int _state;

        public CellsMessage72 Message => _message ?? throw new ObjectDisposedException(nameof(ClaimedCellsResponse));
        public SentCellRequest Request { get; } = request;

        public bool TryBeginHandling() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public void Dispose()
        {
            int state = Interlocked.Exchange(ref _state, 2);
            if (state == 2) return;
            try
            {
                if (state == 0) handler.RequeueClaimedCellRequest(Request, CellRequeueReason.LocalBackpressure);
            }
            finally
            {
                try
                {
                    Interlocked.Exchange(ref _message, null)?.Dispose();
                }
                finally
                {
                    Interlocked.Exchange(ref _reservation, null)?.Dispose();
                }
            }
        }
    }

    private static T EnsureNotNull<T>(T? value, string paramName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowCellsResponseTooLarge(long requestId, int maxResponseBytes, int size) =>
        throw new SubprotocolException(
            $"{nameof(CellsMessage72)} response ID {requestId} exceeds its {maxResponseBytes}-byte request bound: {size} bytes.");
}
