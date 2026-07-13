// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;
using PooledTransactionsMessage65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;
using PooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

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
    ISpecProvider specProvider,
    IBlobCustodyTracker blobCustodyTracker,
    ISparseBlobPoolPeerRegistry sparseBlobPoolPeerRegistry,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth71ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, txPoolConfig, specProvider, transactionsGossipPolicy), IStaticProtocolInfo
    , ISparseBlobPoolPeer
{
    private const int MinSamplerFullProviderAnnouncements = 2;
    private const int RequestToAnnouncementRatioThreshold = 5;
    private const int MinCellRequestsBeforeRatioDisconnect = 5;
    private const int MaxCountedBlobAnnouncements = 4096;
    private const int MaxPendingCellRequests = 1024;
    internal const int MaxSentCellRequests = 2048;
    private const int MaxPendingCellRequestWork = 4096;
    private const int MaxSentCellRequestWork = 2048;
    private const int CellServeBurstMultiplier = 8;
    private const int CellServeRefillDivisor = 4;
    internal const int MaxCellsRequestHashes = 256;
    internal const int MaxCellsResponseHashes = 64;
    internal const int MinCellsResponseBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan CellRequestTtl = Timeouts.Eth;
    private static readonly TimeSpan CellResponseCorrelationTtl = Timeouts.Cleanup;
    private static readonly TimeSpan PartialCellResponseBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingCellStateTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestToAnnouncementWarmup = TimeSpan.FromSeconds(60);
    private readonly int _providerProbabilityPercent = txPoolConfig.SparseBlobProviderProbabilityPercent;
    private readonly ConcurrentDictionary<ValueHash256, CellRequestState> _pendingCellRequests = new();
    private readonly ConcurrentDictionary<ValueHash256, CellRequestState> _sentCellRequests = new();
    private readonly ConcurrentDictionary<long, SentCellRequest> _sentCellRequestIds = new();
    private readonly ClockCache<long, HashSet<ValueHash256>> _sentPooledTransactionRequests = new(MaxSentCellRequests, lockPartition: 1);
    private readonly ClockCache<ValueHash256, byte> _countedBlobAnnouncements = new(MaxCountedBlobAnnouncements, lockPartition: 1);
    private readonly ClockCache<ValueHash256, BlobCellMask> _announcedBlobTransactionMasks = new(MemoryAllowance.TxHashCacheSize, lockPartition: 1);
    private readonly ClockCache<ValueHash256, DateTimeOffset> _partialCellResponseBackoff = new(MemoryAllowance.TxHashCacheSize / 10, lockPartition: 1);
    private readonly ConcurrentQueue<CellStateKey> _pendingCellRequestOrder = new();
    private readonly ConcurrentQueue<CellStateKey> _sentCellRequestOrder = new();
    private readonly Lock _cellStateLock = new();
    private readonly int _maxCellsPerTransaction = GetMaxCellsPerTransaction(specProvider);
    private readonly int _maxCellsResponseBytes = GetMaxCellsResponseBytes(GetMaxCellsPerTransaction(specProvider));
    private readonly int _cellServeTokenCapacity = GetMaxCellsPerTransaction(specProvider) * CellServeBurstMultiplier;
    private static readonly byte[] EmptyCellMaskBytes = BlobCellMask.Empty.ToBytes();
    private readonly IBlobCustodyTracker _blobCustodyTracker = blobCustodyTracker;
    private readonly ISparseBlobPoolPeerRegistry _sparseBlobPoolPeerRegistry = sparseBlobPoolPeerRegistry ?? throw new ArgumentNullException(nameof(sparseBlobPoolPeerRegistry));
    private DateTimeOffset _requestRatioWarmupEndsAt;
    private Func<ClaimedCellsResponse, CancellationToken, ValueTask>? _handleCells;
    private long _cellStateRevision;
    private long _blobAnnouncementsReceived;
    private long _cellRequestsReceived;
    private int _pendingCellRequestWork;
    private int _sentCellRequestWork;
    private int _pendingCellRequestCount;
    private int _sentCellRequestCount;
    private int _pendingCellRequestQueueCount;
    private int _sentCellRequestQueueCount;
    private double _cellServeTokens;
    private DateTimeOffset _cellServeTokensUpdatedAt;

    public override string Name => "eth72";

    public new static byte Version => EthVersions.Eth72;
    public override byte ProtocolVersion => Version;
    public override int MessageIdSpaceSize => 22;

    public override void Init()
    {
        _requestRatioWarmupEndsAt = _timestamper.UtcNowOffset + RequestToAnnouncementWarmup;
        _cellServeTokens = _cellServeTokenCapacity;
        _cellServeTokensUpdatedAt = _timestamper.UtcNowOffset;
        _handleCells = HandleCells;
        _sparseBlobPoolPeerRegistry.AddPeer(this);
        _txPool.NewPending += OnNewPending;
        base.Init();
    }

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth72MessageCode.NewPooledTransactionHashes:
                if (CanReceiveTransactions)
                {
                    using NewPooledTransactionHashesMessage72 newPooledTxHashesMsg = Deserialize<NewPooledTransactionHashesMessage72>(message.Content);
                    ReportIn(newPooledTxHashesMsg, size);
                    Handle(newPooledTxHashesMsg);
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
                    PooledTransactionsMessage66 pooledTransactions = Deserialize<PooledTransactionsMessage66>(message.Content);
                    ReportIn(pooledTransactions, size);
                    if (!TryClaimPooledTransactionRequest(pooledTransactions))
                    {
                        pooledTransactions.Dispose();
                        throw new SubprotocolException($"Unrequested, duplicate, or mismatched {nameof(PooledTransactionsMessage66)} response ID {pooledTransactions.RequestId}.");
                    }

                    Handle(pooledTransactions.EthMessage);
                }
                else
                {
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
                    if (!TryPeekRequestId(message.Content, out long requestId))
                    {
                        throw new SubprotocolException($"Could not read request ID from {nameof(CellsMessage72)}.");
                    }

                    CellRequestClaimResult claimResult = TryClaimSentCellRequest(requestId, out SentCellRequest sentRequest);
                    if (claimResult == CellRequestClaimResult.Unrequested)
                    {
                        throw new SubprotocolException($"Unrequested or duplicate {nameof(CellsMessage72)} response ID {requestId}.");
                    }

                    if (claimResult != CellRequestClaimResult.Claimed)
                    {
                        if (claimResult == CellRequestClaimResult.Expired)
                        {
                            RequeueClaimedCellRequest(sentRequest, removePeer: false);
                        }

                        ReportIn($"Stale {nameof(CellsMessage72)} response ID {requestId} ignored", size);
                        return true;
                    }

                    if (size > sentRequest.MaxResponseBytes)
                    {
                        RequeueClaimedCellRequest(sentRequest, removePeer: true);
                        throw new SubprotocolException(
                            $"{nameof(CellsMessage72)} response ID {requestId} exceeds its {sentRequest.MaxResponseBytes}-byte request bound: {size} bytes.");
                    }

                    CellsMessage72 cellsMessage;
                    try
                    {
                        cellsMessage = Deserialize<CellsMessage72>(message.Content);
                    }
                    catch (RlpException)
                    {
                        RequeueClaimedCellRequest(sentRequest, removePeer: true);
                        throw;
                    }
                    catch (IncompleteDeserializationException)
                    {
                        RequeueClaimedCellRequest(sentRequest, removePeer: true);
                        throw;
                    }

                    ReportIn(cellsMessage, size);
                    ClaimedCellsResponse response = new(cellsMessage, sentRequest);
                    // Cell proof verification is too expensive for the network thread;
                    // failures disconnect the peer via the background task wrapper.
                    if (!BackgroundTaskScheduler.TryScheduleBackgroundTask(response, _handleCells!, nameof(CellsMessage72)))
                    {
                        // Scheduler saturated or shutting down: release the in-flight reservation and
                        // park the request so a later announcement can retry it.
                        response.Dispose();
                        RequeueClaimedCellRequest(sentRequest, removePeer: false);
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

        // Light entries persisted before the consensus-size field was added cannot produce a
        // spec-compliant eth/72 size announcement.
        // Such transactions keep propagating via eth/68-71 sessions until they churn out.
        if (tx is LightTransaction lightTx && lightTx.GetConsensusEncodingSize() == 0)
        {
            return false;
        }

        BlobCellMask mask = GetAnnouncementMask(tx);
        if (mask.IsEmpty)
        {
            return false;
        }

        ValueHash256 hash = tx.Hash.ValueHash256;
        if (_announcedBlobTransactionMasks.TryGet(hash, out BlobCellMask announcedMask)
            && (announcedMask & mask) == mask)
        {
            return false;
        }

        _announcedBlobTransactionMasks.Set(hash, announcedMask | mask);
        NotifiedTransactions.Set(hash);
        return true;
    }

    protected override void SendNewTransactionsCore(IEnumerable<Transaction> txs, bool sendFullTx)
    {
        if (sendFullTx)
        {
            base.SendNewTransactionsCore(txs, sendFullTx);
            return;
        }

        using ArrayPoolList<Transaction> pendingTransactions = new(NewPooledTransactionHashesMessage72.MaxCount);
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
        _txPool.NewPending -= OnNewPending;
        _sparseBlobPoolPeerRegistry.RemovePeer(this);
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
        if (msg.Hashes.Length > NewPooledTransactionHashesMessage72.MaxCount)
        {
            throw new SubprotocolException(
                $"Too many hashes in {nameof(NewPooledTransactionHashesMessage72)}: {msg.Hashes.Length}, maximum {NewPooledTransactionHashesMessage72.MaxCount}.");
        }

        if (msg.Hashes.Length != msg.Types.Length || msg.Hashes.Length != msg.Sizes.Length)
        {
            throw new SubprotocolException(
                $"Wrong format of {nameof(NewPooledTransactionHashesMessage72)} message. Hashes count: {msg.Hashes.Length} Types count: {msg.Types.Length} Sizes count: {msg.Sizes.Length}");
        }

        BlobCellMask announcementMask = ExtractAnnouncementMask(msg);
        AddNotifiedTransactions(msg.Hashes.AsSpan());
        TxPool.Metrics.PendingTransactionsHashesReceived += msg.Hashes.Length;

        int packetSizeLeft = TransactionsMessage.MaxPacketSize;
        int toRequestCount = 0;
        ArrayPoolList<Hash256>? hashesToRequest = null;

        for (int i = 0; i < msg.Hashes.Length; i++)
        {
            Hash256 hash = msg.Hashes[i];
            TxType txType = (TxType)msg.Types[i];
            int txSize = msg.Sizes[i];
            long maxTxSize = txType.SupportsBlobs() ? _configuredMaxBlobTxSize : _configuredMaxTxSize;
            if (txSize > maxTxSize)
            {
                continue;
            }

            bool supportsBlobs = txType.SupportsBlobs();
            bool cellAnnouncementBackedOff = supportsBlobs && IsCellAnnouncementBackedOff(hash.ValueHash256);
            if (_blobSupportEnabled && supportsBlobs && !cellAnnouncementBackedOff)
            {
                if (!_sparseBlobPoolPeerRegistry.RecordAnnouncement(this, hash, announcementMask))
                {
                    continue;
                }

                if (_countedBlobAnnouncements.Set(hash.ValueHash256, 1))
                {
                    Interlocked.Increment(ref _blobAnnouncementsReceived);
                }
            }

            bool shouldRequestTx = !_txPool.IsKnown(hash)
                && _txPool.NotifyAboutTx(hash, this) is AnnounceResult.RequestRequired;

            if (shouldRequestTx
                && (_blobSupportEnabled || !supportsBlobs))
            {
                TxShapeAnnouncements.Set(hash, (txSize, txType));
                hashesToRequest ??= new(Math.Min(msg.Hashes.Length - i, 256));

                if ((txSize > packetSizeLeft && toRequestCount > 0) || toRequestCount >= 256)
                {
                    SendPooledTransactionsRequest(hashesToRequest);
                    hashesToRequest = new ArrayPoolList<Hash256>(Math.Min(msg.Hashes.Length - i, 256));
                    packetSizeLeft = TransactionsMessage.MaxPacketSize;
                    toRequestCount = 0;
                }

                hashesToRequest.Add(hash);
                packetSizeLeft -= txSize;
                toRequestCount++;
            }

            if (_blobSupportEnabled && supportsBlobs && !cellAnnouncementBackedOff)
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

    private async Task<PooledTransactionsMessage66> Handle(GetPooledTransactionsMessage66 getPooledTransactions, CancellationToken cancellationToken)
    {
        using GetPooledTransactionsMessage66 message = getPooledTransactions;
        using PooledTransactionsMessage65 pooledTransactions = await FulfillPooledTransactionsRequest(message.EthMessage, cancellationToken);
        ArrayPoolList<Transaction> txs = new(pooledTransactions.Transactions.Count);
        foreach (Transaction tx in pooledTransactions.Transactions.AsSpan())
        {
            txs.Add(ElideBlobPayload(tx));
        }

        return new PooledTransactionsMessage66(message.RequestId, new PooledTransactionsMessage65(txs));
    }

    private void SendPooledTransactionsRequest(IOwnedReadOnlyList<Hash256> hashes)
    {
        GetPooledTransactionsMessage66 message = GetPooledTransactionsMessage66.New(hashes);
        HashSet<ValueHash256> requestedHashes = new(hashes.Count);
        foreach (Hash256 hash in hashes.AsSpan())
        {
            requestedHashes.Add(hash.ValueHash256);
        }

        _sentPooledTransactionRequests.Set(message.RequestId, requestedHashes);
        Send(message);
    }

    private bool TryClaimPooledTransactionRequest(PooledTransactionsMessage66 response)
    {
        if (!_sentPooledTransactionRequests.Delete(response.RequestId, out HashSet<ValueHash256>? requestedHashes))
        {
            return false;
        }

        ReadOnlySpan<Transaction> transactions = response.EthMessage.Transactions.AsSpan();
        for (int i = 0; i < transactions.Length; i++)
        {
            Hash256? hash = transactions[i].Hash;
            if (hash is null || !requestedHashes.Remove(hash.ValueHash256))
            {
                return false;
            }
        }

        return true;
    }

    protected override bool CanServePooledTransaction(Transaction tx) => true;

    public override void HandleMessage(PooledTransactionRequestMessage message)
    {
        ArrayPoolList<Hash256> hashes = new(1) { new Hash256(message.TxHash) };
        SendPooledTransactionsRequest(hashes);
    }

    private Task<CellsMessage72> Handle(GetCellsMessage72 getCellsMessage, CancellationToken cancellationToken)
    {
        using GetCellsMessage72 message = getCellsMessage;
        if (ShouldDisconnectForCellRequestAbuse(message.Hashes.Length))
        {
            return Task.FromResult(new CellsMessage72(message.RequestId, [], [], EmptyCellMaskBytes));
        }

        BlobCellMask requestedMask = BlobCellMask.FromBytes(message.CellMask);
        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node:c} requested blob cells for {message.Hashes.Length} txs with mask {requestedMask}.");
        }

        int requestHashCount = Math.Min(message.Hashes.Length, MaxCellsRequestHashes);
        int responseCapacity = Math.Min(requestHashCount, MaxCellsResponseHashes);
        List<Hash256> responseHashes = new(responseCapacity);
        List<byte[][]> cellsByTx = new(responseCapacity);
        HashSet<ValueHash256> seenHashes = new(requestHashCount);
        int hashesContentLength = 0;
        int cellsContentLength = 0;

        for (int i = 0; i < requestHashCount; i++)
        {
            if (cancellationToken.IsCancellationRequested || responseHashes.Count >= MaxCellsResponseHashes)
            {
                break;
            }

            Hash256 hash = message.Hashes[i];
            if (!seenHashes.Add(hash.ValueHash256))
            {
                continue;
            }

            if (!TryBuildCellsResponse(hash, requestedMask, out byte[][] cells))
            {
                continue;
            }

            int nextHashesContentLength = hashesContentLength + Rlp.LengthOf(hash);
            int nextCellsContentLength = cellsContentLength + Rlp.LengthOf(cells);
            if (EstimateCellsResponseLength(message.RequestId, nextHashesContentLength, nextCellsContentLength) > _maxCellsResponseBytes)
            {
                break;
            }

            responseHashes.Add(hash);
            cellsByTx.Add(cells);
            hashesContentLength = nextHashesContentLength;
            cellsContentLength = nextCellsContentLength;
        }

        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node:c} responding with blob cells for {responseHashes.Count} txs with mask {requestedMask}.");
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
        if (_timestamper.UtcNowOffset < _requestRatioWarmupEndsAt)
        {
            return false;
        }

        long announcements = Math.Max(1, Volatile.Read(ref _blobAnnouncementsReceived));
        if (totalRequests < MinCellRequestsBeforeRatioDisconnect
            || totalRequests < announcements * RequestToAnnouncementRatioThreshold)
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
        using ClaimedCellsResponse claimedResponse = response;
        if (!cancellationToken.IsCancellationRequested)
        {
            Handle(claimedResponse.Message, claimedResponse.Request);
        }
        else
        {
            RequeueClaimedCellRequest(claimedResponse.Request, removePeer: false);
        }

        return ValueTask.CompletedTask;
    }

    private void Handle(CellsMessage72 message, SentCellRequest sentRequest)
    {
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
            Logger.Debug($"{Node:c} received blob cells for {message.Hashes.Length} txs with mask {responseMask}.");
        }

        if (responseMask.IsEmpty)
        {
            if (message.Hashes.Length != 0 || message.Cells.Length != 0)
            {
                ThrowMalformedCellsResponse(sentRequest, $"Wrong format of {nameof(CellsMessage72)} message. Empty cell mask with non-empty response.");
            }

            RetryUnansweredCellRequest(sentRequest, responseMask);
            return;
        }

        if (message.Hashes.Length == 0)
        {
            RetryUnansweredCellRequest(sentRequest, responseMask);
            return;
        }

        if (message.Hashes.Length != 1 || message.Hashes[0].ValueHash256 != sentRequest.Hash)
        {
            ThrowMalformedCellsResponse(sentRequest, $"Wrong format of {nameof(CellsMessage72)} message. Response does not match requested blob cells.");
        }

        Hash256 hash = message.Hashes[0];
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
            DateTimeOffset retryAt = _timestamper.UtcNowOffset + PartialCellResponseBackoff;
            _sparseBlobPoolPeerRegistry.RemoveAnnouncement(this, hash);
            _partialCellResponseBackoff.Set(key, retryAt);
            AddPendingCellRequest(key, missingMask, retryAt, restoreAnnouncement: true);
        }

        _sparseBlobPoolPeerRegistry.OnCellsRequestCompleted(hash, requestedMask, this);
        AddPendingCellRequest(key, missingMask);
        if (!_sparseBlobPoolPeerRegistry.RecordCells(this, hash, availableMask, pending.Cells))
        {
            DateTimeOffset retryAt = _timestamper.UtcNowOffset + PartialCellResponseBackoff;
            _sparseBlobPoolPeerRegistry.RemoveAnnouncement(this, hash);
            _partialCellResponseBackoff.Set(key, retryAt);
            AddPendingCellRequest(key, availableMask, retryAt, restoreAnnouncement: true);
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
    /// Handles a response that carries none of the requested cells: the peer's announcement is
    /// dropped so retries converge on other providers instead of looping on the same peer.
    /// </summary>
    private void RetryUnansweredCellRequest(SentCellRequest sentRequest, BlobCellMask responseMask)
    {
        if (!responseMask.IsEmpty && (responseMask & sentRequest.Mask) != responseMask)
        {
            ThrowMalformedCellsResponse(sentRequest, $"Unexpected cell mask in empty {nameof(CellsMessage72)} response.");
        }

        Hash256 hash = sentRequest.Hash.ToHash256();
        _sparseBlobPoolPeerRegistry.OnCellsRequestCompleted(hash, sentRequest.Mask, this);
        _sparseBlobPoolPeerRegistry.RemoveAnnouncement(this, hash);
        RequestCellsWhenReady(hash, sentRequest.Mask);
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
        if (!_partialCellResponseBackoff.TryGet(hash, out DateTimeOffset retryAt))
        {
            return false;
        }

        if (_timestamper.UtcNowOffset < retryAt)
        {
            return true;
        }

        _partialCellResponseBackoff.Delete(hash);
        return false;
    }

    protected override ValueTask HandleSlow(TransactionsRequest request, CancellationToken cancellationToken)
    {
        IOwnedReadOnlyList<Transaction> transactions = request.Transactions;
        ReadOnlySpan<Transaction> transactionsSpan = transactions.AsSpan();
        try
        {
            int startIdx = request.StartIndex;
            bool isTrace = Logger.IsTrace;

            for (int i = startIdx; i < transactionsSpan.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (i == startIdx)
                    {
                        transactions.Dispose();
                        return ValueTask.CompletedTask;
                    }

                    if (!BackgroundTaskScheduler.TryScheduleBackgroundTask(new TransactionsRequest(transactions, i), HandleSlow, "Transactions"))
                    {
                        transactions.Dispose();
                    }

                    return ValueTask.CompletedTask;
                }

                Transaction tx = transactionsSpan[i];
                if (!ValidateAnnouncedPooledTransaction(tx))
                {
                    throw new SubprotocolException("invalid pooled tx type or size");
                }

                if (!tx.SupportsBlobs)
                {
                    PrepareAndSubmitTransaction(tx, isTrace);
                    continue;
                }

                if (tx.NetworkWrapper is ShardBlobNetworkWrapper { Version: ProofVersion.V0 })
                {
                    if (tx.NetworkWrapper is ShardBlobNetworkWrapper wrapper && wrapper.HasFullBlobs())
                    {
                        PrepareAndSubmitTransaction(tx, isTrace);
                    }

                    if (tx.Hash is not null)
                    {
                        RemoveCellState(tx.Hash.ValueHash256);
                        _sparseBlobPoolPeerRegistry.Clear(tx.Hash);
                    }

                    continue;
                }

                PrepareAndMaybeSubmitSparseBlobTransaction(tx, isTrace);
            }

            transactions.Dispose();
        }
        catch
        {
            transactions.Dispose();
            throw;
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

        _sparseBlobPoolPeerRegistry.TryApplyRecordedCells(tx.Hash);

        TryRequestPendingCells(tx.Hash);
    }

    private void RequestCellsWhenReady(Hash256 hash, BlobCellMask requestMask)
    {
        if (!CanRequestCellsNow(hash, requestMask))
        {
            AddPendingCellRequest(hash.ValueHash256, requestMask);
            return;
        }

        if (_sparseBlobPoolPeerRegistry.TryRequestCells(hash, requestMask, Id))
        {
            return;
        }

        AddPendingCellRequest(hash.ValueHash256, requestMask);
    }

    private bool TryBuildCellsResponse(Hash256 hash, BlobCellMask requestedMask, out byte[][] cells)
    {
        cells = [];
        if (requestedMask.IsEmpty)
        {
            return false;
        }

        bool hasMetadata = _txPool.TryGetPendingBlobCellMetadata(
            hash,
            out BlobCellMask locallyAvailableMask,
            out int blobCount,
            out int materializationWork);
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
                throw new SubprotocolException(
                    $"Wrong format of local blob cells for {hash}. Expected {blobCount * cellsPerBlob} flattened cells, got {availableCells.Length}.");
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
                _cellServeTokens + elapsedSeconds * Math.Max(1, _maxCellsPerTransaction / CellServeRefillDivisor));
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

    private void SendGetCells(Hash256 hash, BlobCellMask requestMask)
    {
        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node:c} requesting blob cells for {hash} with mask {requestMask}.");
        }

        ValueHash256 key = hash.ValueHash256;
        BlobCellMask sentMask = GetSentCellRequestMask(key, requestMask);
        GetCellsMessage72 message = new([hash], sentMask.ToBytes());
        AddSentCellRequest(key, sentMask, message.RequestId);
        Send(message);
    }

    bool ISparseBlobPoolPeer.IsClosing => Session.IsClosing;

    bool ISparseBlobPoolPeer.TrySendGetCells(Hash256 hash, BlobCellMask requestMask)
    {
        if (Session.IsClosing || requestMask.IsEmpty)
        {
            return false;
        }

        SendGetCells(hash, requestMask);
        return true;
    }

    bool ISparseBlobPoolPeer.TrySendPooledTransactionRequest(Hash256 hash)
    {
        if (Session.IsClosing)
        {
            return false;
        }

        ArrayPoolList<Hash256> hashes = new(1) { hash };
        SendPooledTransactionsRequest(hashes);
        return true;
    }

    void ISparseBlobPoolPeer.MaintainSparseBlobState(DateTimeOffset now) => MaintainSparseBlobState(now);

    void ISparseBlobPoolPeer.DisconnectSparseBlobPeer(DisconnectReason reason, string details) => Disconnect(reason, details);

    private void AddPendingCellRequest(
        ValueHash256 hash,
        BlobCellMask requestMask,
        DateTimeOffset? retryAt = null,
        bool restoreAnnouncement = false)
    {
        if (requestMask.IsEmpty)
        {
            return;
        }

        lock (_cellStateLock)
        {
            if (_pendingCellRequests.TryGetValue(hash, out CellRequestState existing))
            {
                BlobCellMask combinedMask = existing.Mask | requestMask;
                _pendingCellRequestWork += combinedMask.Count - existing.Mask.Count;
                DateTimeOffset? combinedRetryAt = existing.RetryAt;
                if (restoreAnnouncement
                    && (combinedRetryAt is null || retryAt > combinedRetryAt))
                {
                    combinedRetryAt = retryAt;
                }

                _pendingCellRequests[hash] = existing with
                {
                    Mask = combinedMask,
                    ExpiresAt = _timestamper.UtcNowOffset + PendingCellStateTtl,
                    RetryAt = combinedRetryAt,
                    RestoreAnnouncement = existing.RestoreAnnouncement || restoreAnnouncement
                };
                TrimPendingCellRequests();
                return;
            }

            long revision = NextCellStateRevision();
            _pendingCellRequests[hash] = new CellRequestState(
                requestMask,
                revision,
                RequestId: 0,
                ExpiresAt: _timestamper.UtcNowOffset + PendingCellStateTtl,
                RetryAt: retryAt,
                RestoreAnnouncement: restoreAnnouncement);
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

    private CellRequestClaimResult TryClaimSentCellRequest(long requestId, out SentCellRequest sentRequest)
    {
        lock (_cellStateLock)
        {
            if (!_sentCellRequestIds.TryRemove(requestId, out sentRequest))
            {
                return CellRequestClaimResult.Unrequested;
            }

            if (!_sentCellRequests.TryGetValue(sentRequest.Hash, out CellRequestState existing)
                || existing.RequestId != requestId)
            {
                return CellRequestClaimResult.Stale;
            }

            RemoveActiveSentCellRequest(sentRequest.Hash);
            DateTimeOffset now = _timestamper.UtcNowOffset;
            return existing.ExpiresAt <= now || sentRequest.CorrelationExpiresAt <= now
                ? CellRequestClaimResult.Expired
                : CellRequestClaimResult.Claimed;
        }
    }

    private static bool TryPeekRequestId(IByteBuffer content, out long requestId)
    {
        requestId = 0;
        RlpReader ctx = new(content.AsSpan());
        try
        {
            ctx.ReadSequenceLength();
            requestId = ctx.DecodeLong();
            return true;
        }
        catch (RlpException)
        {
            return false;
        }
    }

    private void ThrowMalformedCellsResponse(SentCellRequest sentRequest, string message)
    {
        RequeueClaimedCellRequest(sentRequest, removePeer: true);
        throw new SubprotocolException(message);
    }

    private void RequeueClaimedCellRequest(SentCellRequest sentRequest, bool removePeer)
    {
        Hash256 requestedHash = sentRequest.Hash.ToHash256();
        _sparseBlobPoolPeerRegistry.OnCellsRequestCompleted(requestedHash, sentRequest.Mask, this);
        if (removePeer)
        {
            _sparseBlobPoolPeerRegistry.RemovePeer(this);
        }

        RequestCellsWhenReady(requestedHash, sentRequest.Mask);
    }

    private void AddSentCellRequest(ValueHash256 hash, BlobCellMask requestMask, long requestId)
    {
        lock (_cellStateLock)
        {
            DateTimeOffset now = _timestamper.UtcNowOffset;
            if (_sentCellRequests.TryGetValue(hash, out CellRequestState existing))
            {
                BlobCellMask combinedMask = existing.Mask | requestMask;
                _sentCellRequestWork += combinedMask.Count - existing.Mask.Count;
                DateTimeOffset updatedExpiresAt = now + CellRequestTtl;
                int updatedMaxResponseBytes = GetExpectedCellsResponseBound(requestId, combinedMask);
                _sentCellRequests[hash] = existing with { Mask = combinedMask, RequestId = requestId, ExpiresAt = updatedExpiresAt };
                _sentCellRequestIds[requestId] = new SentCellRequest(
                    hash,
                    combinedMask,
                    requestId,
                    now + CellResponseCorrelationTtl,
                    updatedMaxResponseBytes);
                TrimSentCellRequests();
                return;
            }

            long revision = NextCellStateRevision();
            DateTimeOffset expiresAt = now + CellRequestTtl;
            int maxResponseBytes = GetExpectedCellsResponseBound(requestId, requestMask);
            _sentCellRequests[hash] = new CellRequestState(requestMask, revision, requestId, expiresAt, RetryAt: null, RestoreAnnouncement: false);
            _sentCellRequestIds[requestId] = new SentCellRequest(
                hash,
                requestMask,
                requestId,
                now + CellResponseCorrelationTtl,
                maxResponseBytes);
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

        if (_pendingCellRequestQueueCount > MaxPendingCellRequests * 2)
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

        if (_sentCellRequestQueueCount > MaxSentCellRequests * 2)
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
            if (!_pendingCellRequests.TryGetValue(hash, out CellRequestState state)
                || (expectedRevision != 0 && state.Revision != expectedRevision))
            {
                return;
            }

            BlobCellMask remainingMask = state.Mask.Except(completedMask);
            if (remainingMask == state.Mask)
            {
                return;
            }

            if (remainingMask.IsEmpty)
            {
                RemovePendingCellRequestLocked(hash);
                return;
            }

            _pendingCellRequestWork -= state.Mask.Count - remainingMask.Count;
            _pendingCellRequests[hash] = state with { Mask = remainingMask };
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
        RemoveActiveSentCellRequest(hash);
        _sentCellRequestIds.TryRemove(requestId, out _);
    }

    private bool RemoveActiveSentCellRequest(ValueHash256 hash)
    {
        if (!_sentCellRequests.TryRemove(hash, out CellRequestState state))
        {
            return false;
        }

        _sentCellRequestCount--;
        _sentCellRequestWork -= state.Mask.Count;
        return true;
    }

    private void RemovePendingCellRequestLocked(ValueHash256 hash)
    {
        if (_pendingCellRequests.TryRemove(hash, out CellRequestState state))
        {
            _pendingCellRequestCount--;
            _pendingCellRequestWork -= state.Mask.Count;
        }
    }

    private void MaintainSparseBlobState(DateTimeOffset now)
    {
        List<SentCellRequest>? expiredSentRequests = null;
        List<(ValueHash256 Hash, BlobCellMask Mask)>? dueAnnouncementRestores = null;
        lock (_cellStateLock)
        {
            foreach (KeyValuePair<ValueHash256, CellRequestState> entry in _pendingCellRequests)
            {
                if (entry.Value.ExpiresAt <= now)
                {
                    RemovePendingCellRequestLocked(entry.Key);
                }
                else if (entry.Value.RestoreAnnouncement
                    && entry.Value.RetryAt is { } retryAt
                    && retryAt <= now)
                {
                    (dueAnnouncementRestores ??= []).Add((entry.Key, entry.Value.Mask));
                }
            }

            foreach (KeyValuePair<ValueHash256, CellRequestState> entry in _sentCellRequests)
            {
                CellRequestState state = entry.Value;
                if (state.ExpiresAt <= now)
                {
                    if (_sentCellRequestIds.TryGetValue(state.RequestId, out SentCellRequest request))
                    {
                        (expiredSentRequests ??= []).Add(request);
                    }

                    RemoveActiveSentCellRequest(entry.Key);
                }
            }

            foreach (KeyValuePair<long, SentCellRequest> entry in _sentCellRequestIds)
            {
                if (entry.Value.CorrelationExpiresAt <= now)
                {
                    _sentCellRequestIds.TryRemove(entry.Key, out _);
                }
            }

            TrimPendingCellRequests();
            TrimSentCellRequests();
        }

        if (dueAnnouncementRestores is not null)
        {
            for (int i = 0; i < dueAnnouncementRestores.Count; i++)
            {
                (ValueHash256 key, BlobCellMask mask) = dueAnnouncementRestores[i];
                Hash256 hash = key.ToHash256();
                _sparseBlobPoolPeerRegistry.RecordAnnouncement(this, hash, mask);
                TryRequestPendingCells(hash);
            }
        }

        if (expiredSentRequests is not null)
        {
            for (int i = 0; i < expiredSentRequests.Count; i++)
            {
                SentCellRequest request = expiredSentRequests[i];
                Hash256 hash = request.Hash.ToHash256();
                _sparseBlobPoolPeerRegistry.RemoveAnnouncement(this, hash);
                _sparseBlobPoolPeerRegistry.OnCellsRequestCompleted(hash, request.Mask, this);
                RequestCellsWhenReady(hash, request.Mask);
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

    private static int GetMaxCellsPerTransaction(ISpecProvider specProvider)
    {
        ulong maxBlobsPerTx = specProvider.GetFinalSpec()?.MaxBlobsPerTx ?? Eip4844Constants.DefaultMaxBlobCount;
        if (maxBlobsPerTx == 0)
        {
            maxBlobsPerTx = Eip4844Constants.DefaultMaxBlobCount;
        }

        return (int)Math.Min((ulong)int.MaxValue, maxBlobsPerTx * (ulong)BlobCellMask.CellCount);
    }

    private static int GetMaxCellsResponseBytes(int maxCellsPerTransaction)
    {
        long fullTransactionCellsBytes = (long)maxCellsPerTransaction * CkzgLib.Ckzg.BytesPerCell;
        long maxResponseBytes = Math.Max(MinCellsResponseBytes, fullTransactionCellsBytes);
        return (int)Math.Min(int.MaxValue, maxResponseBytes);
    }

    private int GetExpectedCellsResponseBound(long requestId, BlobCellMask requestMask)
    {
        int maxBlobCount = _maxCellsPerTransaction / BlobCellMask.CellCount;
        int maxCellCount = checked(maxBlobCount * requestMask.Count);
        int encodedCellLength = Rlp.LengthOfByteString(CkzgLib.Ckzg.BytesPerCell, 0);
        int cellGroupLength = Rlp.LengthOfSequence(checked(maxCellCount * encodedCellLength));
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

    private BlobCellMask ExtractAnnouncementMask(NewPooledTransactionHashesMessage72 message)
    {
        bool containsBlobTransaction = false;
        for (int i = 0; i < message.Types.Length; i++)
        {
            if (((TxType)message.Types[i]).SupportsBlobs())
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
                Logger.Trace($"{Node:c} sent sparse blob tx {tx.Hash} and it was {accepted.Value} (chain ID = {tx.Signature?.ChainId})");
            }
        }

        if (tx.Hash is not null)
        {
            TryRequestPendingCells(tx.Hash);
        }
    }

    private bool ValidateAnnouncedPooledTransaction(Transaction tx)
    {
        if (TxShapeAnnouncements.Delete(tx.Hash, out (int Size, TxType Type) txShape)
            && (!MatchesAnnouncedTransactionSize(tx, txShape.Size) || tx.Type != txShape.Type))
        {
            return false;
        }

        if (!tx.Type.SupportsBlobs())
        {
            return true;
        }

        return tx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && wrapper.Version switch
            {
                ProofVersion.V0 => ValidatePooledBlobTransactionV0(tx, wrapper),
                ProofVersion.V1 => ValidateSparsePooledBlobTransaction(tx),
                _ => false
            };
    }

    private static bool ValidatePooledBlobTransactionV0(Transaction tx, ShardBlobNetworkWrapper wrapper)
    {
        if (tx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes
            || wrapper.Commitments.Length != blobVersionedHashes.Length
            || wrapper.Proofs.Length != blobVersionedHashes.Length)
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
            if (wrapper.Commitments[i].Length != CkzgLib.Ckzg.BytesPerCommitment
                || wrapper.Proofs[i].Length != CkzgLib.Ckzg.BytesPerProof)
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

        IBlobProofsManager proofsVerifier = IBlobProofsManager.For(wrapper.Version);
        return proofsVerifier.ValidateLengths(wrapper)
            && proofsVerifier.ValidateHashes(wrapper, blobVersionedHashes);
    }

    private bool CanRequestCellsNow(Hash256 hash, BlobCellMask requestMask)
    {
        if (IsSupernode || requestMask.IsFull)
        {
            return true;
        }

        bool hasValidatedTransaction = _sparseBlobPoolPeerRegistry.HasRecordedTransaction(hash)
            || _txPool.TryGetPendingBlobCellMask(hash, out _);
        return hasValidatedTransaction
            && _sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(hash) >= MinSamplerFullProviderAnnouncements;
    }

    private bool TryRequestPendingCells(Hash256 hash)
    {
        ValueHash256 key = hash.ValueHash256;
        CellRequestState pendingRequestState;
        lock (_cellStateLock)
        {
            if (!_pendingCellRequests.TryGetValue(key, out pendingRequestState)
                || pendingRequestState.Mask.IsEmpty)
            {
                return false;
            }
        }

        if (!CanRequestCellsNow(hash, pendingRequestState.Mask))
        {
            return false;
        }

        if (!_sparseBlobPoolPeerRegistry.TryRequestCells(hash, pendingRequestState.Mask, Id))
        {
            return false;
        }

        RemovePendingCellRequestMask(key, pendingRequestState.Mask, pendingRequestState.Revision);
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
        ArrayPoolList<byte> types = new(count);
        ArrayPoolList<int> sizes = new(count);
        ArrayPoolList<Hash256> hashes = new(count);

        for (int i = 0; i < count; i++)
        {
            Transaction tx = txs[i];
            types.Add((byte)tx.Type);
            sizes.Add(GetAnnouncementSize(tx));
            hashes.Add(tx.Hash!);
            TxPool.Metrics.PendingTransactionsHashesSent++;
        }

        Send(new NewPooledTransactionHashesMessage72(types, sizes, hashes, cellMask));
    }

    private static int GetAnnouncementSize(Transaction tx)
    {
        if (tx is LightTransaction lightTx && lightTx.GetConsensusEncodingSize() > 0)
        {
            return lightTx.GetConsensusEncodingSize();
        }

        return tx.GetLength(shouldCountBlobs: false);
    }

    protected override bool MatchesAnnouncedTransactionSize(Transaction tx, int announcedSize)
        => tx.GetLength(shouldCountBlobs: false) == announcedSize;

    private static Transaction ElideBlobPayload(Transaction tx)
    {
        if (!tx.SupportsBlobs || tx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
        {
            return tx;
        }

        Transaction clone = new();
        tx.CopyTo(clone);
        clone.NetworkWrapper = wrapper with { Blobs = [] };
        clone.ClearLengthCache();
        return clone;
    }

    private readonly record struct SentCellRequest(
        ValueHash256 Hash,
        BlobCellMask Mask,
        long RequestId,
        DateTimeOffset CorrelationExpiresAt,
        int MaxResponseBytes);

    private readonly record struct CellRequestState(
        BlobCellMask Mask,
        long Revision,
        long RequestId,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RetryAt,
        bool RestoreAnnouncement);

    private readonly record struct CellStateKey(ValueHash256 Hash, long Revision);

    private enum CellRequestClaimResult
    {
        Claimed,
        Stale,
        Expired,
        Unrequested
    }

    private sealed class ClaimedCellsResponse(CellsMessage72 message, SentCellRequest request) : IDisposable
    {
        private CellsMessage72? _message = message;

        public CellsMessage72 Message => _message ?? throw new ObjectDisposedException(nameof(ClaimedCellsResponse));
        public SentCellRequest Request { get; } = request;

        public void Dispose() => Interlocked.Exchange(ref _message, null)?.Dispose();
    }
}
