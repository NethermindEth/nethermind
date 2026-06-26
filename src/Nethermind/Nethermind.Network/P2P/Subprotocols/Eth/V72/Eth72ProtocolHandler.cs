// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
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
    PublicKey localNodeId,
    ISparseBlobPoolPeerRegistry sparseBlobPoolPeerRegistry,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth71ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, txPoolConfig, specProvider, transactionsGossipPolicy), IStaticProtocolInfo
    , ISparseBlobPoolPeer
{
    private const int MaxProviderProbabilityPercent = 100;
    private const int MinSamplerFullProviderAnnouncements = 2;
    private const int RequestToAnnouncementRatioThreshold = 5;
    private const int MinCellRequestsBeforeRatioDisconnect = 5;
    private const int MaxPendingCellRequests = 4096;
    private const int MaxSentCellRequests = 4096;
    private const int MaxPendingCells = 64;
    internal const int MaxCellsResponseHashes = 64;
    internal const int MinCellsResponseBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan RequestToAnnouncementWarmup = TimeSpan.FromSeconds(60);
    private readonly bool _blobSupportEnabled = txPoolConfig.BlobsSupport.IsEnabled();
    private readonly int _providerThresholdPercent = Math.Clamp(txPoolConfig.SparseBlobProviderProbabilityPercent, 0, MaxProviderProbabilityPercent);
    private readonly long _configuredMaxTxSize = txPoolConfig.MaxTxSize ?? long.MaxValue;
    private readonly long _configuredMaxBlobTxSize = txPoolConfig.MaxBlobTxSize is null
        ? long.MaxValue
        : txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock();
    private readonly ConcurrentDictionary<ValueHash256, CellRequestState> _pendingCellRequests = new();
    private readonly ConcurrentDictionary<ValueHash256, CellRequestState> _sentCellRequests = new();
    private readonly ConcurrentDictionary<long, SentCellRequest> _sentCellRequestIds = new();
    private readonly ConcurrentDictionary<ValueHash256, PendingCellsState> _pendingCells = new();
    private readonly ClockCache<ValueHash256, BlobCellMask> _announcedBlobTransactionMasks = new(MemoryAllowance.TxHashCacheSize, lockPartition: 1);
    private readonly ConcurrentQueue<CellStateKey> _pendingCellRequestOrder = new();
    private readonly ConcurrentQueue<CellStateKey> _sentCellRequestOrder = new();
    private readonly ConcurrentQueue<CellStateKey> _pendingCellsOrder = new();
    private readonly ClockCache<ValueHash256, (int Size, TxType Type)> _txShapeAnnouncements = new(MemoryAllowance.TxHashCacheSize / 10, lockPartition: 1);
    private readonly Lock _cellStateLock = new();
    private readonly int _maxCellsPerTransaction = GetMaxCellsPerTransaction(specProvider);
    private readonly int _maxCellsResponseBytes = GetMaxCellsResponseBytes(GetMaxCellsPerTransaction(specProvider));
    private static readonly byte[] EmptyCellMaskBytes = BlobCellMask.Empty.ToBytes();
    private readonly IBlobCustodyTracker _blobCustodyTracker = blobCustodyTracker;
    private readonly PublicKey _localNodeId = localNodeId;
    private readonly ISparseBlobPoolPeerRegistry _sparseBlobPoolPeerRegistry = sparseBlobPoolPeerRegistry ?? throw new ArgumentNullException(nameof(sparseBlobPoolPeerRegistry));
    private DateTimeOffset _requestRatioWarmupEndsAt;
    private long _cellStateRevision;
    private long _blobAnnouncementsReceived;
    private long _cellRequestsReceived;

    public override string Name => "eth72";

    public new static byte Version => EthVersions.Eth72;
    public override byte ProtocolVersion => Version;
    public override int MessageIdSpaceSize => 22;

    public override void Init()
    {
        _requestRatioWarmupEndsAt = _timestamper.UtcNowOffset + RequestToAnnouncementWarmup;
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
            case Eth72MessageCode.GetCells:
                HandleInBackground<GetCellsMessage72, CellsMessage72>(message, Handle);
                return true;
            case Eth72MessageCode.Cells:
                if (CanReceiveTransactions)
                {
                    bool requestIdAvailable = TryPeekRequestId(message.Content, out long requestId);
                    CellsMessage72 cellsMessage;
                    try
                    {
                        cellsMessage = Deserialize<CellsMessage72>(message.Content);
                    }
                    catch (RlpException) when (requestIdAvailable)
                    {
                        RequeueSentCellRequestFromMalformedResponse(requestId);
                        throw;
                    }
                    catch (IncompleteDeserializationException) when (requestIdAvailable)
                    {
                        RequeueSentCellRequestFromMalformedResponse(requestId);
                        throw;
                    }

                    ReportIn(cellsMessage, size);
                    Handle(cellsMessage);
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
            base.SendNewTransactionCore(tx);
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

        BlobCellMask mask = GetAnnouncementMask(tx);
        if (mask.IsEmpty)
        {
            return base.ShouldNotifyTransactionCore(tx);
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

        List<Transaction> nonBlobTransactions = new(NewPooledTransactionHashesMessage72.MaxCount);
        foreach (Transaction tx in txs)
        {
            if (!tx.SupportsBlobs)
            {
                nonBlobTransactions.Add(tx);
                if (nonBlobTransactions.Count == NewPooledTransactionHashesMessage72.MaxCount)
                {
                    SendAnnouncement(nonBlobTransactions, EmptyCellMaskBytes);
                    nonBlobTransactions.Clear();
                }

                continue;
            }

            if (nonBlobTransactions.Count > 0)
            {
                SendAnnouncement(nonBlobTransactions, EmptyCellMaskBytes);
                nonBlobTransactions.Clear();
            }

            SendAnnouncement([tx], GetAnnouncementMask(tx).ToBytes());
        }

        if (nonBlobTransactions.Count > 0)
        {
            SendAnnouncement(nonBlobTransactions, EmptyCellMaskBytes);
        }
    }

    protected override void OnDisposed()
    {
        _txPool.NewPending -= OnNewPending;
        _sparseBlobPoolPeerRegistry.RemovePeer(Id);
        base.OnDisposed();
    }

    private void Handle(NewPooledTransactionHashesMessage72 msg)
    {
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

            bool shouldRequestTx = !_txPool.IsKnown(hash)
                && _txPool.NotifyAboutTx(hash, this) is AnnounceResult.RequestRequired;

            if (shouldRequestTx
                && (_blobSupportEnabled || !txType.SupportsBlobs()))
            {
                _txShapeAnnouncements.Set(hash, (txSize, txType));
                hashesToRequest ??= new(Math.Min(msg.Hashes.Length - i, 256));

                if ((txSize > packetSizeLeft && toRequestCount > 0) || toRequestCount >= 256)
                {
                    Send(GetPooledTransactionsMessage66.New(hashesToRequest));
                    hashesToRequest = new ArrayPoolList<Hash256>(Math.Min(msg.Hashes.Length - i, 256));
                    packetSizeLeft = TransactionsMessage.MaxPacketSize;
                    toRequestCount = 0;
                }

                hashesToRequest.Add(hash);
                packetSizeLeft -= txSize;
                toRequestCount++;
            }

            if (_blobSupportEnabled && txType.SupportsBlobs())
            {
                Interlocked.Increment(ref _blobAnnouncementsReceived);
                _sparseBlobPoolPeerRegistry.RecordAnnouncement(this, hash, announcementMask);
                BlobCellMask requestMask = GetRequestMask(hash, announcementMask);
                if (!requestMask.IsEmpty)
                {
                    RequestCellsWhenReady(hash, requestMask);
                }
            }
        }

        if (hashesToRequest is not null)
        {
            Send(GetPooledTransactionsMessage66.New(hashesToRequest));
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

    protected override bool CanServePooledTransaction(Transaction tx) => true;

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

        int responseCapacity = Math.Min(message.Hashes.Length, MaxCellsResponseHashes);
        List<Hash256> responseHashes = new(responseCapacity);
        List<byte[][]> cellsByTx = new(responseCapacity);
        int hashesContentLength = 0;
        int cellsContentLength = 0;

        for (int i = 0; i < message.Hashes.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested || responseHashes.Count >= MaxCellsResponseHashes)
            {
                break;
            }

            Hash256 hash = message.Hashes[i];
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

    private void Handle(CellsMessage72 message)
    {
        if (message.Hashes.Length != message.Cells.Length)
        {
            ThrowMalformedCellsResponse(message.RequestId, $"Wrong format of {nameof(CellsMessage72)} message. Hashes count: {message.Hashes.Length} Cells count: {message.Cells.Length}");
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
                ThrowMalformedCellsResponse(message.RequestId, $"Wrong format of {nameof(CellsMessage72)} message. Empty cell mask with non-empty response.");
            }

            if (TryRemoveSentCellRequest(message.RequestId, out ValueHash256 requestedHash, out BlobCellMask requestMask))
            {
                RequestCellsWhenReady(requestedHash.ToHash256(), requestMask);
            }

            return;
        }

        if (message.Hashes.Length == 0)
        {
            if (TryRemoveSentCellRequest(message.RequestId, out ValueHash256 requestedHash, out BlobCellMask requestMask))
            {
                if ((responseMask & requestMask) != responseMask)
                {
                    ThrowMalformedCellsResponse(message.RequestId, $"Unexpected cell mask in empty {nameof(CellsMessage72)} response.");
                }

                RequestCellsWhenReady(requestedHash.ToHash256(), requestMask);
            }

            return;
        }

        if (!TryGetSentCellRequest(message.RequestId, out SentCellRequest sentRequest))
        {
            return;
        }

        if (message.Hashes.Length != 1 || message.Hashes[0].ValueHash256 != sentRequest.Hash)
        {
            ThrowMalformedCellsResponse(message.RequestId, $"Wrong format of {nameof(CellsMessage72)} message. Response does not match requested blob cells.");
        }

        Hash256 hash = message.Hashes[0];
        ValueHash256 key = hash.ValueHash256;
        if (!_sentCellRequests.TryGetValue(key, out CellRequestState sentRequestState)
            || sentRequestState.RequestId != message.RequestId)
        {
            return;
        }

        BlobCellMask requestedMask = sentRequestState.Mask;
        if ((responseMask & requestedMask) != responseMask)
        {
            ThrowMalformedCellsResponse(message.RequestId, $"Unexpected cell mask in {nameof(CellsMessage72)} for {hash}.");
        }

        PendingCellsBuffer responseCells = new(responseMask, message.Cells[0], Id);
        try
        {
            ValidatePendingCellsBuffer(hash, responseCells);
        }
        catch (SubprotocolException)
        {
            RequeueSentCellRequestFromMalformedResponse(message.RequestId);
            throw;
        }

        BlobCellMask availableMask = GetAvailableCellMask(responseCells);
        if (availableMask.IsEmpty)
        {
            ThrowMalformedCellsResponse(message.RequestId, $"No requested sparse blob cells available for {hash}.");
        }

        PendingCellsBuffer pending = ReducePendingCells(responseCells, availableMask);
        BlobCellMask missingMask = GetMissingSentCellMask(key, availableMask, message.RequestId);
        if (_txPool.TryGetPendingBlobTransaction(hash, out Transaction? blobTx))
        {
            if (TryApplyPendingCellsOrRequeue(hash, blobTx, pending, requestedMask))
            {
                OnPendingCellsApplied(hash, key, availableMask, missingMask);
            }

            return;
        }

        AddPendingCells(key, pending);
        AddPendingCellRequest(key, missingMask);
        _sparseBlobPoolPeerRegistry.RecordCells(this, hash, availableMask, pending.Cells);
        if (_txPool.TryGetPendingBlobTransaction(hash, out blobTx)
            && TryApplyPendingCellsOrRequeue(hash, blobTx, pending, requestedMask))
        {
            OnPendingCellsApplied(hash, key, availableMask, missingMask);
        }
    }

    private void OnPendingCellsApplied(Hash256 hash, ValueHash256 key, BlobCellMask availableMask, BlobCellMask missingMask)
    {
        RemovePendingCells(key);
        RemovePendingCellRequest(key);
        ClearSparseRegistryIfFull(hash, availableMask);
        RequestCellsWhenReady(hash, missingMask);
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
                    PrepareAndSubmitTransaction(tx, isTrace);
                    if (tx.Hash is not null)
                    {
                        RemoveCellState(tx.Hash.ValueHash256);
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

        ValueHash256 key = tx.Hash.ValueHash256;
        bool appliedPendingCells = false;
        if (_pendingCells.TryGetValue(key, out PendingCellsState pendingState))
        {
            PendingCellsBuffer pending = pendingState.Buffer;
            if (TryApplyPendingCells(tx.Hash, tx, pending, out PendingCellsValidationFailure failure))
            {
                RemovePendingCells(key);
                RemoveCellRequestState(key);
                ClearSparseRegistryIfFull(tx.Hash, pending.CellMask);
                appliedPendingCells = true;
            }
            else
            {
                HandleInvalidBufferedCells(tx.Hash, key, pending, failure);
            }
        }

        if (!appliedPendingCells
            && _pendingCellRequests.TryGetValue(key, out CellRequestState pendingRequestState)
            && !pendingRequestState.Mask.IsEmpty)
        {
            TryRequestPendingCells(tx.Hash);
        }
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
        if (!_txPool.TryGetPendingBlobTransaction(hash, out Transaction? blobTx)
            || blobTx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes
            || requestedMask.IsEmpty)
        {
            cells = [];
            return false;
        }

        if (!_txPool.TryGetBlobCells(hash, requestedMask, out BlobCellMask availableMask, out byte[][]? availableCells)
            || (availableMask & requestedMask) != requestedMask)
        {
            cells = [];
            return false;
        }

        int expectedCells = blobVersionedHashes.Length * requestedMask.Count;
        if (availableCells.Length != expectedCells)
        {
            throw new SubprotocolException(
                $"Wrong format of local blob cells for {hash}. Expected {expectedCells} flattened cells, got {availableCells.Length}.");
        }

        cells = availableCells;
        return true;
    }

    private bool TryApplyPendingCells(Hash256 hash, Transaction tx, PendingCellsBuffer pending, out PendingCellsValidationFailure failure)
    {
        failure = default;
        if (tx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes || pending.CellMask.IsEmpty)
        {
            return InvalidPendingCells(PendingCellsFailureSource.StoredTransaction, $"Wrong sparse blob transaction form for {hash}.", out failure);
        }

        int requestedCellsPerBlob = pending.CellMask.Count;
        if (requestedCellsPerBlob == 0)
        {
            return InvalidPendingCells(PendingCellsFailureSource.Cells, $"Empty sparse blob cell mask for {hash}.", out failure);
        }

        int blobCount = blobVersionedHashes.Length;
        if (pending.Cells.Length != blobCount * requestedCellsPerBlob)
        {
            return InvalidPendingCells(
                PendingCellsFailureSource.Cells,
                $"Wrong format of {nameof(CellsMessage72)} for {hash}. Expected {blobCount * requestedCellsPerBlob} flattened cells, got {pending.Cells.Length}.",
                out failure);
        }

        BlobCellMask availableMask = BlobCellMask.Empty;
        int availableCount = 0;
        int requestedPosition = 0;
        foreach (int cellIndex in pending.CellMask.EnumerateSetBits())
        {
            bool presentForAllBlobs = true;
            for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
            {
                byte[] cell = pending.Cells[blobIndex * requestedCellsPerBlob + requestedPosition];
                if (cell.Length is not 0 and not CkzgLib.Ckzg.BytesPerCell)
                {
                    return InvalidPendingCells(PendingCellsFailureSource.Cells, $"Invalid cell size {cell.Length} in {nameof(CellsMessage72)}.", out failure);
                }

                presentForAllBlobs &= cell.Length == CkzgLib.Ckzg.BytesPerCell;
            }

            if (presentForAllBlobs)
            {
                availableMask |= new BlobCellMask(UInt128.One << cellIndex);
                availableCount++;
            }

            requestedPosition++;
        }

        if (availableCount == 0)
        {
            return InvalidPendingCells(PendingCellsFailureSource.Cells, $"No requested sparse blob cells available for {hash}.", out failure);
        }

        byte[][] flattenedCells = new byte[blobCount * availableCount][];
        for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
        {
            int outputIndex = blobIndex * availableCount;
            int inputIndex = blobIndex * requestedCellsPerBlob;
            int requestIndex = 0;
            foreach (int cellIndex in pending.CellMask.EnumerateSetBits())
            {
                if (availableMask.Contains(cellIndex))
                {
                    flattenedCells[outputIndex++] = pending.Cells[inputIndex + requestIndex];
                }

                requestIndex++;
            }
        }

        if (tx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper)
        {
            return InvalidPendingCells(PendingCellsFailureSource.StoredTransaction, $"Wrong sparse blob transaction form for {hash}.", out failure);
        }

        ShardBlobNetworkWrapper sparseWrapper = wrapper with { CellMask = availableMask, Cells = flattenedCells };
        if (!BlobCellsHelper.ValidateCells(sparseWrapper))
        {
            return InvalidPendingCells(PendingCellsFailureSource.Ambiguous, $"Invalid sparse blob cell proofs for {hash}.", out failure);
        }

        if (!_txPool.TryMergeBlobCells(hash, availableMask, flattenedCells))
        {
            return InvalidPendingCells(PendingCellsFailureSource.Ambiguous, $"Could not merge sparse blob cells for {hash}.", out failure);
        }

        return true;
    }

    private bool TryApplyPendingCellsOrRequeue(Hash256 hash, Transaction blobTx, PendingCellsBuffer pending, BlobCellMask requestMask)
    {
        if (TryApplyPendingCells(hash, blobTx, pending, out PendingCellsValidationFailure failure))
        {
            return true;
        }

        HandleInvalidPendingCells(hash, hash.ValueHash256, pending, requestMask, failure);
        if (failure.Source == PendingCellsFailureSource.Cells)
        {
            throw new SubprotocolException(failure.Message);
        }

        return false;
    }

    private void HandleInvalidBufferedCells(Hash256 hash, ValueHash256 key, PendingCellsBuffer pending, PendingCellsValidationFailure failure)
        => HandleInvalidPendingCells(hash, key, pending, pending.CellMask, failure);

    private void HandleInvalidPendingCells(Hash256 hash, ValueHash256 key, PendingCellsBuffer pending, BlobCellMask requestMask, PendingCellsValidationFailure failure)
    {
        RemovePendingCells(key);
        if (failure.Source == PendingCellsFailureSource.StoredTransaction)
        {
            _txPool.RemoveTransaction(hash);
        }
        else if (failure.Source == PendingCellsFailureSource.Cells)
        {
            _sparseBlobPoolPeerRegistry.RemovePeer(pending.SourcePeerId);
            Disconnect(DisconnectReason.BreachOfProtocol, $"Invalid buffered sparse blob cells for {hash}.");
        }

        RequestCellsWhenReady(hash, requestMask);
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

    void ISparseBlobPoolPeer.DisconnectSparseBlobPeer(DisconnectReason reason, string details) => Disconnect(reason, details);

    private void ValidatePendingCellsBuffer(Hash256 hash, PendingCellsBuffer pending)
    {
        int cellsPerBlob = pending.CellMask.Count;
        if (cellsPerBlob == 0
            || pending.Cells.Length == 0
            || pending.Cells.Length % cellsPerBlob != 0
            || pending.Cells.Length > _maxCellsPerTransaction)
        {
            throw new SubprotocolException($"Wrong format of {nameof(CellsMessage72)} for {hash}. Cells count: {pending.Cells.Length}.");
        }

        for (int i = 0; i < pending.Cells.Length; i++)
        {
            int length = pending.Cells[i].Length;
            if (length is not 0 and not CkzgLib.Ckzg.BytesPerCell)
            {
                throw new SubprotocolException($"Invalid cell size {length} in {nameof(CellsMessage72)}.");
            }
        }
    }

    private static BlobCellMask GetAvailableCellMask(PendingCellsBuffer pending)
    {
        int cellsPerBlob = pending.CellMask.Count;
        int blobCount = pending.Cells.Length / cellsPerBlob;
        BlobCellMask availableMask = BlobCellMask.Empty;
        int requestedPosition = 0;
        foreach (int cellIndex in pending.CellMask.EnumerateSetBits())
        {
            bool presentForAllBlobs = true;
            for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
            {
                presentForAllBlobs &= pending.Cells[blobIndex * cellsPerBlob + requestedPosition].Length == CkzgLib.Ckzg.BytesPerCell;
            }

            if (presentForAllBlobs)
            {
                availableMask |= new BlobCellMask(UInt128.One << cellIndex);
            }

            requestedPosition++;
        }

        return availableMask;
    }

    private static PendingCellsBuffer ReducePendingCells(PendingCellsBuffer pending, BlobCellMask availableMask)
    {
        if (availableMask == pending.CellMask)
        {
            return pending;
        }

        int inputCellsPerBlob = pending.CellMask.Count;
        int outputCellsPerBlob = availableMask.Count;
        int blobCount = pending.Cells.Length / inputCellsPerBlob;
        byte[][] cells = new byte[blobCount * outputCellsPerBlob][];
        for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
        {
            int outputIndex = blobIndex * outputCellsPerBlob;
            int inputIndex = blobIndex * inputCellsPerBlob;
            int requestedPosition = 0;
            foreach (int cellIndex in pending.CellMask.EnumerateSetBits())
            {
                if (availableMask.Contains(cellIndex))
                {
                    cells[outputIndex++] = pending.Cells[inputIndex + requestedPosition];
                }

                requestedPosition++;
            }
        }

        return new PendingCellsBuffer(availableMask, cells, pending.SourcePeerId);
    }

    private void AddPendingCellRequest(ValueHash256 hash, BlobCellMask requestMask)
    {
        if (requestMask.IsEmpty)
        {
            return;
        }

        lock (_cellStateLock)
        {
            if (_pendingCellRequests.TryGetValue(hash, out CellRequestState existing))
            {
                _pendingCellRequests[hash] = existing with { Mask = existing.Mask | requestMask };
                return;
            }

            long revision = NextCellStateRevision();
            _pendingCellRequests[hash] = new CellRequestState(requestMask, revision, RequestId: 0);
            _pendingCellRequestOrder.Enqueue(new CellStateKey(hash, revision));
            TrimPendingCellRequests();
        }
    }

    private BlobCellMask GetSentCellRequestMask(ValueHash256 hash, BlobCellMask requestMask)
    {
        lock (_cellStateLock)
        {
            return _sentCellRequests.TryGetValue(hash, out CellRequestState existing)
                ? existing.Mask | requestMask
                : requestMask;
        }
    }

    private BlobCellMask GetMissingSentCellMask(ValueHash256 hash, BlobCellMask responseMask, long requestId)
    {
        lock (_cellStateLock)
        {
            if (!_sentCellRequests.TryGetValue(hash, out CellRequestState existing)
                || existing.RequestId != requestId)
            {
                return BlobCellMask.Empty;
            }

            RemoveSentCellRequest(hash, existing.RequestId);
            return new BlobCellMask(existing.Mask.Value & ~responseMask.Value);
        }
    }

    private bool TryGetSentCellRequest(long requestId, out SentCellRequest sentRequest)
    {
        lock (_cellStateLock)
        {
            return _sentCellRequestIds.TryGetValue(requestId, out sentRequest);
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

    private void ThrowMalformedCellsResponse(long requestId, string message)
    {
        RequeueSentCellRequestFromMalformedResponse(requestId);
        throw new SubprotocolException(message);
    }

    private void RequeueSentCellRequestFromMalformedResponse(long requestId)
    {
        if (TryRemoveSentCellRequest(requestId, out ValueHash256 hash, out BlobCellMask requestMask))
        {
            _sparseBlobPoolPeerRegistry.RemovePeer(Id);
            RequestCellsWhenReady(hash.ToHash256(), requestMask);
        }
    }

    private bool TryRemoveSentCellRequest(long requestId, out ValueHash256 hash, out BlobCellMask requestMask)
    {
        lock (_cellStateLock)
        {
            if (!_sentCellRequestIds.TryRemove(requestId, out SentCellRequest sentRequest))
            {
                hash = default;
                requestMask = BlobCellMask.Empty;
                return false;
            }

            if (_sentCellRequests.TryGetValue(sentRequest.Hash, out CellRequestState existing)
                && existing.RequestId == requestId)
            {
                _sentCellRequests.TryRemove(sentRequest.Hash, out _);
            }

            hash = sentRequest.Hash;
            requestMask = sentRequest.Mask;
            return true;
        }
    }

    private void AddSentCellRequest(ValueHash256 hash, BlobCellMask requestMask, long requestId)
    {
        lock (_cellStateLock)
        {
            if (_sentCellRequests.TryGetValue(hash, out CellRequestState existing))
            {
                BlobCellMask combinedMask = existing.Mask | requestMask;
                _sentCellRequestIds.TryRemove(existing.RequestId, out _);
                _sentCellRequests[hash] = existing with { Mask = combinedMask, RequestId = requestId };
                _sentCellRequestIds[requestId] = new SentCellRequest(hash, combinedMask);
                return;
            }

            long revision = NextCellStateRevision();
            _sentCellRequests[hash] = new CellRequestState(requestMask, revision, requestId);
            _sentCellRequestIds[requestId] = new SentCellRequest(hash, requestMask);
            _sentCellRequestOrder.Enqueue(new CellStateKey(hash, revision));
            TrimSentCellRequests();
        }
    }

    private void AddPendingCells(ValueHash256 hash, PendingCellsBuffer pending)
    {
        lock (_cellStateLock)
        {
            if (_pendingCells.TryGetValue(hash, out PendingCellsState existing))
            {
                _pendingCells[hash] = existing with
                {
                    Buffer = TryMergePendingCells(existing.Buffer, pending, out PendingCellsBuffer merged)
                        ? merged
                        : pending
                };
                return;
            }

            long revision = NextCellStateRevision();
            _pendingCells[hash] = new PendingCellsState(pending, revision);
            _pendingCellsOrder.Enqueue(new CellStateKey(hash, revision));
            TrimPendingCells();
        }
    }

    private static bool TryMergePendingCells(PendingCellsBuffer existing, PendingCellsBuffer pending, out PendingCellsBuffer merged)
    {
        merged = pending;
        int existingCellsPerBlob = existing.CellMask.Count;
        int pendingCellsPerBlob = pending.CellMask.Count;
        if (existingCellsPerBlob == 0
            || pendingCellsPerBlob == 0
            || existing.Cells.Length % existingCellsPerBlob != 0
            || pending.Cells.Length % pendingCellsPerBlob != 0)
        {
            return false;
        }

        int blobCount = existing.Cells.Length / existingCellsPerBlob;
        if (pending.Cells.Length / pendingCellsPerBlob != blobCount)
        {
            return false;
        }

        BlobCellMask mergedMask = existing.CellMask | pending.CellMask;
        if (mergedMask == existing.CellMask)
        {
            merged = existing;
            return true;
        }

        byte[][] mergedCells = new byte[blobCount * mergedMask.Count][];
        int outputIndex = 0;
        for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
        {
            int existingIndex = 0;
            int pendingIndex = 0;
            foreach (int cellIndex in mergedMask.EnumerateSetBits())
            {
                bool hasExistingCell = existing.CellMask.Contains(cellIndex);
                bool hasPendingCell = pending.CellMask.Contains(cellIndex);
                if (hasExistingCell)
                {
                    mergedCells[outputIndex] = existing.Cells[blobIndex * existingCellsPerBlob + existingIndex];
                }
                else if (hasPendingCell)
                {
                    mergedCells[outputIndex] = pending.Cells[blobIndex * pendingCellsPerBlob + pendingIndex];
                }

                if (hasExistingCell)
                {
                    existingIndex++;
                }

                if (hasPendingCell)
                {
                    pendingIndex++;
                }

                outputIndex++;
            }
        }

        merged = new PendingCellsBuffer(mergedMask, mergedCells, pending.SourcePeerId);
        return true;
    }

    private void TrimPendingCellRequests()
    {
        while (_pendingCellRequests.Count > MaxPendingCellRequests
            && _pendingCellRequestOrder.TryDequeue(out CellStateKey key))
        {
            if (_pendingCellRequests.TryGetValue(key.Hash, out CellRequestState state)
                && state.Revision == key.Revision)
            {
                _pendingCellRequests.TryRemove(key.Hash, out _);
            }
        }
    }

    private void TrimSentCellRequests()
    {
        while (_sentCellRequests.Count > MaxSentCellRequests
            && _sentCellRequestOrder.TryDequeue(out CellStateKey key))
        {
            if (_sentCellRequests.TryGetValue(key.Hash, out CellRequestState state)
                && state.Revision == key.Revision)
            {
                RemoveSentCellRequest(key.Hash, state.RequestId);
            }
        }
    }

    private void TrimPendingCells()
    {
        while (_pendingCells.Count > MaxPendingCells
            && _pendingCellsOrder.TryDequeue(out CellStateKey key))
        {
            if (_pendingCells.TryGetValue(key.Hash, out PendingCellsState state)
                && state.Revision == key.Revision)
            {
                _pendingCells.TryRemove(key.Hash, out _);
            }
        }
    }

    private void RemoveCellRequestState(ValueHash256 hash)
    {
        lock (_cellStateLock)
        {
            _pendingCellRequests.TryRemove(hash, out _);
            if (_sentCellRequests.TryGetValue(hash, out CellRequestState sentState))
            {
                RemoveSentCellRequest(hash, sentState.RequestId);
            }
        }
    }

    private void RemovePendingCellRequest(ValueHash256 hash)
    {
        lock (_cellStateLock)
        {
            _pendingCellRequests.TryRemove(hash, out _);
        }
    }

    private void RemovePendingCells(ValueHash256 hash)
    {
        lock (_cellStateLock)
        {
            _pendingCells.TryRemove(hash, out _);
        }
    }

    private void RemoveCellState(ValueHash256 hash)
    {
        lock (_cellStateLock)
        {
            _pendingCellRequests.TryRemove(hash, out _);
            if (_sentCellRequests.TryGetValue(hash, out CellRequestState sentState))
            {
                RemoveSentCellRequest(hash, sentState.RequestId);
            }
            _pendingCells.TryRemove(hash, out _);
        }
    }

    private void RemoveSentCellRequest(ValueHash256 hash, long requestId)
    {
        _sentCellRequests.TryRemove(hash, out _);
        _sentCellRequestIds.TryRemove(requestId, out _);
    }

    private void ClearSparseRegistryIfFull(Hash256 hash, BlobCellMask cellMask)
    {
        if (cellMask.IsFull)
        {
            _sparseBlobPoolPeerRegistry.Clear(hash);
        }
    }

    private long NextCellStateRevision() => ++_cellStateRevision;

    private static bool InvalidPendingCells(PendingCellsFailureSource source, string message, out PendingCellsValidationFailure failure)
    {
        failure = new PendingCellsValidationFailure(source, message);
        return false;
    }

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

    private static int EstimateCellsResponseLength(long requestId, int hashesContentLength, int cellsContentLength)
    {
        int payloadContentLength = Rlp.LengthOfSequence(hashesContentLength)
            + Rlp.LengthOfSequence(cellsContentLength)
            + Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, 0);
        int contentLength = Rlp.LengthOf(requestId) + Rlp.LengthOfSequence(payloadContentLength);
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
            BlobCellMask nonBlobMask = BlobCellMask.FromBytes(message.CellMask);
            if (!nonBlobMask.IsEmpty)
            {
                throw new SubprotocolException(
                    $"Wrong format of {nameof(NewPooledTransactionHashesMessage72)} message. Non-blob announcements must not include a non-empty cell mask.");
            }

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

        Disconnect(DisconnectReason.UselessPeer, $"Sparse blob cell request-to-announce ratio exceeded: requests {totalRequests}, announcements {announcements}.");
        return true;
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

    private BlobCellMask GetRequestMask(Hash256 hash, BlobCellMask announcementMask)
    {
        if (announcementMask.IsEmpty)
        {
            return BlobCellMask.Empty;
        }

        return IsSupernode
            ? announcementMask
            : GetNormalRequestMask(hash, announcementMask);
    }

    private BlobCellMask GetNormalRequestMask(Hash256 hash, BlobCellMask announcementMask)
    {
        if (ShouldFetchFull(hash) && announcementMask.IsFull)
        {
            return BlobCellMask.Full;
        }

        return GetSamplerRequestMask(hash, announcementMask);
    }

    private BlobCellMask GetSamplerRequestMask(Hash256 hash, BlobCellMask announcementMask)
    {
        BlobCellMask mask = _blobCustodyTracker.CurrentMask & announcementMask;
        if (announcementMask.IsFull)
        {
            mask |= SelectExtraCellMask(hash, announcementMask, mask);
        }

        return mask;
    }

    private bool ShouldFetchFull(Hash256 hash)
    {
        if (_providerThresholdPercent <= 0)
        {
            return false;
        }

        if (_providerThresholdPercent >= MaxProviderProbabilityPercent)
        {
            return true;
        }

        Span<byte> input = stackalloc byte[PublicKey.LengthInBytes + 32];
        _localNodeId.Bytes.CopyTo(input);
        hash.Bytes.CopyTo(input[PublicKey.LengthInBytes..]);
        Hash256 sampleHash = Keccak.Compute(input);
        ushort sample = BinaryPrimitives.ReadUInt16BigEndian(sampleHash.Bytes[..2]);
        return sample % MaxProviderProbabilityPercent < _providerThresholdPercent;
    }

    private bool ValidateAnnouncedPooledTransaction(Transaction tx)
    {
        if (!_txShapeAnnouncements.Delete(tx.Hash, out (int Size, TxType Type) txShape))
        {
            return true;
        }

        if (tx.GetLength() != txShape.Size || tx.Type != txShape.Type)
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
                ProofVersion.V0 => ValidateFullPooledBlobTransaction(tx, wrapper),
                ProofVersion.V1 => ValidateSparsePooledBlobTransaction(tx),
                _ => false
            };
    }

    private static bool ValidateFullPooledBlobTransaction(Transaction tx, ShardBlobNetworkWrapper wrapper)
    {
        if (!wrapper.HasFullBlobs()
            || tx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes
            || wrapper.Commitments.Length != blobVersionedHashes.Length)
        {
            return false;
        }

        IBlobProofsManager proofsVerifier = IBlobProofsManager.For(wrapper.Version);
        return proofsVerifier.ValidateLengths(wrapper)
            && proofsVerifier.ValidateHashes(wrapper, blobVersionedHashes);
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

        bool hasTransaction = _sparseBlobPoolPeerRegistry.HasRecordedTransaction(hash)
            || _txPool.TryGetPendingBlobTransaction(hash, out _);
        return hasTransaction
            && _sparseBlobPoolPeerRegistry.GetFullProviderAnnouncementCount(hash) >= MinSamplerFullProviderAnnouncements;
    }

    private bool TryRequestPendingCells(Hash256 hash)
    {
        ValueHash256 key = hash.ValueHash256;
        if (!_pendingCellRequests.TryGetValue(key, out CellRequestState pendingRequestState)
            || pendingRequestState.Mask.IsEmpty
            || !CanRequestCellsNow(hash, pendingRequestState.Mask))
        {
            return false;
        }

        if (!_sparseBlobPoolPeerRegistry.TryRequestCells(hash, pendingRequestState.Mask, Id))
        {
            return false;
        }

        RemovePendingCellRequest(key);
        return true;
    }

    private bool IsSupernode => SparseBlobPoolPeerRegistry.HasSupernodeCustody(_blobCustodyTracker.CurrentMask);

    private BlobCellMask SelectExtraCellMask(Hash256 hash, BlobCellMask announcementMask, BlobCellMask alreadyRequested)
    {
        UInt128 candidates = announcementMask.Value & ~alreadyRequested.Value;
        if (candidates == UInt128.Zero)
        {
            return BlobCellMask.Empty;
        }

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
        _localNodeId.Bytes.CopyTo(input);
        hash.Bytes.CopyTo(input[PublicKey.LengthInBytes..^1]);
        input[^1] = 1;
        Hash256 sampleHash = Keccak.Compute(input);
        // The slight modulo bias is acceptable for non-consensus extra-column sampling.
        int selected = sampleHash.Bytes[0] % candidateCount;
        return new BlobCellMask(UInt128.One << candidateIndices[selected]);
    }

    private BlobCellMask GetAnnouncementMask(Transaction tx)
    {
        if (tx.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
        {
            return wrapper.Version == ProofVersion.V1 ? wrapper.GetAvailableCellMask() : BlobCellMask.Full;
        }

        if (tx is LightTransaction lightTx)
        {
            return lightTx.ProofVersion == ProofVersion.V1 ? (lightTx.BlobCellMask.IsEmpty ? BlobCellMask.Full : lightTx.BlobCellMask) : BlobCellMask.Full;
        }

        return BlobCellMask.Full;
    }

    private void SendAnnouncement(IReadOnlyList<Transaction> txs, byte[] cellMask)
    {
        byte[] types = new byte[txs.Count];
        int[] sizes = new int[txs.Count];
        Hash256[] hashes = new Hash256[txs.Count];

        for (int i = 0; i < txs.Count; i++)
        {
            Transaction tx = txs[i];
            types[i] = (byte)tx.Type;
            sizes[i] = GetAnnouncementSize(tx);
            hashes[i] = tx.Hash!;
            TxPool.Metrics.PendingTransactionsHashesSent++;
        }

        Send(new NewPooledTransactionHashesMessage72(types, sizes, hashes, cellMask));
    }

    private static int GetAnnouncementSize(Transaction tx)
    {
        if (tx is LightTransaction { ProofVersion: ProofVersion.V1 } lightTx && lightTx.GetSparseBlobNetworkSize() > 0)
        {
            return lightTx.GetSparseBlobNetworkSize();
        }

        if (!tx.SupportsBlobs || tx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
        {
            return tx.GetLength();
        }

        return wrapper.Version == ProofVersion.V1
            ? tx.TryCalculateSparseBlobNetworkSize() ?? tx.GetLength()
            : tx.GetLength();
    }

    private static Transaction ElideBlobPayload(Transaction tx)
    {
        if (!tx.SupportsBlobs || tx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper)
        {
            return tx;
        }

        Transaction clone = new();
        tx.CopyTo(clone);
        clone.NetworkWrapper = wrapper with { Blobs = [] };
        clone.ClearLengthCache();
        return clone;
    }

    private readonly record struct PendingCellsBuffer(BlobCellMask CellMask, byte[][] Cells, PublicKey SourcePeerId);
    private readonly record struct SentCellRequest(ValueHash256 Hash, BlobCellMask Mask);
    private readonly record struct CellRequestState(BlobCellMask Mask, long Revision, long RequestId);
    private readonly record struct PendingCellsState(PendingCellsBuffer Buffer, long Revision);
    private readonly record struct CellStateKey(ValueHash256 Hash, long Revision);
    private readonly record struct PendingCellsValidationFailure(PendingCellsFailureSource Source, string Message);

    private enum PendingCellsFailureSource
    {
        Cells,
        StoredTransaction,
        Ambiguous
    }
}
