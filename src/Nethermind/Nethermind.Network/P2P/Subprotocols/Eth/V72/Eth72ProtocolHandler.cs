// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.Rlpx;
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
    private const int MaxBasisPoints = 10_000;
    private const int MinSamplerFullProviderAnnouncements = 2;
    private const int RequestToAnnouncementRatioThreshold = 5;
    private const int MinCellRequestsBeforeRatioDisconnect = 5;
    private const int MaxPendingCellRequests = 4096;
    private const int MaxSentCellRequests = 4096;
    private const int MaxPendingCells = 64;
    private const int SupernodeCustodyColumnThreshold = 64;
    private static readonly TimeSpan RequestToAnnouncementWarmup = TimeSpan.FromSeconds(60);
    private readonly bool _blobSupportEnabled = txPoolConfig.BlobsSupport.IsEnabled();
    private readonly int _providerThresholdBasisPoints = Math.Clamp(txPoolConfig.SparseBlobProviderProbabilityBasisPoints, 0, MaxBasisPoints);
    private readonly long _configuredMaxTxSize = txPoolConfig.MaxTxSize ?? long.MaxValue;
    private readonly long _configuredMaxBlobTxSize = txPoolConfig.MaxBlobTxSize is null
        ? long.MaxValue
        : txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock();
    private readonly ConcurrentDictionary<ValueHash256, CellRequestState> _pendingCellRequests = new();
    private readonly ConcurrentDictionary<ValueHash256, CellRequestState> _sentCellRequests = new();
    private readonly ConcurrentDictionary<ValueHash256, PendingCellsState> _pendingCells = new();
    private readonly ClockCache<ValueHash256, BlobCellMask> _announcedBlobTransactionMasks = new(MemoryAllowance.TxHashCacheSize, lockPartition: 1);
    private readonly ConcurrentQueue<CellStateKey> _pendingCellRequestOrder = new();
    private readonly ConcurrentQueue<CellStateKey> _sentCellRequestOrder = new();
    private readonly ConcurrentQueue<CellStateKey> _pendingCellsOrder = new();
    private readonly Lock _cellStateLock = new();
    private readonly int _maxCellsPerTransaction = GetMaxCellsPerTransaction(specProvider);
    private readonly IBlobCustodyTracker _blobCustodyTracker = blobCustodyTracker;
    private readonly PublicKey _localNodeId = localNodeId;
    private readonly ISparseBlobPoolPeerRegistry _sparseBlobPoolPeerRegistry = sparseBlobPoolPeerRegistry ?? throw new ArgumentNullException(nameof(sparseBlobPoolPeerRegistry));
    private readonly DateTimeOffset _requestRatioWarmupEndsAt = Timestamper.Default.UtcNowOffset + RequestToAnnouncementWarmup;
    private long _cellStateRevision;
    private long _blobAnnouncementsReceived;
    private long _cellRequestsReceived;

    public override string Name => "eth72";

    public new static byte Version => EthVersions.Eth72;
    public override byte ProtocolVersion => Version;
    public override int MessageIdSpaceSize => 22;

    public override void Init()
    {
        _sparseBlobPoolPeerRegistry.AddPeer(this);
        _txPool.NewPending += OnNewPending;
        _blobCustodyTracker.CustodyChanged += OnBlobCustodyChanged;
        base.Init();
    }

    protected override void HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth72MessageCode.NewPooledTransactionHashes:
                if (CanReceiveTransactions)
                {
                    NewPooledTransactionHashesMessage72 newPooledTxHashesMsg = Deserialize<NewPooledTransactionHashesMessage72>(message.Content);
                    ReportIn(newPooledTxHashesMsg, size);
                    Handle(newPooledTxHashesMsg);
                }
                else
                {
                    const string ignored = $"{nameof(NewPooledTransactionHashesMessage72)} ignored, syncing";
                    ReportIn(ignored, size);
                }

                break;
            case Eth66MessageCode.GetPooledTransactions:
                HandleInBackground<GetPooledTransactionsMessage66, PooledTransactionsMessage66>(message, Handle);
                break;
            case Eth72MessageCode.GetCells:
                HandleInBackground<GetCellsMessage72, CellsMessage72>(message, Handle);
                break;
            case Eth72MessageCode.Cells:
                if (CanReceiveTransactions)
                {
                    CellsMessage72 cellsMessage = Deserialize<CellsMessage72>(message.Content);
                    ReportIn(cellsMessage, size);
                    Handle(cellsMessage);
                }
                else
                {
                    const string ignored = $"{nameof(CellsMessage72)} ignored, syncing";
                    ReportIn(ignored, size);
                }

                break;
            default:
                base.HandleMessageCore(message);
                break;
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
                    SendAnnouncement(nonBlobTransactions, []);
                    nonBlobTransactions.Clear();
                }

                continue;
            }

            if (nonBlobTransactions.Count > 0)
            {
                SendAnnouncement(nonBlobTransactions, []);
                nonBlobTransactions.Clear();
            }

            SendAnnouncement([tx], GetAnnouncementMask(tx).ToBytes());
        }

        if (nonBlobTransactions.Count > 0)
        {
            SendAnnouncement(nonBlobTransactions, []);
        }
    }

    protected override void OnDisposed()
    {
        _txPool.NewPending -= OnNewPending;
        _blobCustodyTracker.CustodyChanged -= OnBlobCustodyChanged;
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
        AddNotifiedTransactions(msg.Hashes);
        TxPool.Metrics.PendingTransactionsHashesReceived += msg.Hashes.Length;

        int packetSizeLeft = TransactionsMessage.MaxPacketSize;
        int toRequestCount = 0;
        ArrayPoolList<Hash256> hashesToRequest = new(msg.Hashes.Length);

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
                if ((txSize > packetSizeLeft && toRequestCount > 0) || toRequestCount >= 256)
                {
                    Send(GetPooledTransactionsMessage66.New(hashesToRequest));
                    hashesToRequest = new ArrayPoolList<Hash256>(msg.Hashes.Length);
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

        if (hashesToRequest.Count != 0)
        {
            Send(GetPooledTransactionsMessage66.New(hashesToRequest));
        }
        else
        {
            hashesToRequest.Dispose();
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

    private Task<CellsMessage72> Handle(GetCellsMessage72 getCellsMessage, CancellationToken cancellationToken)
    {
        using GetCellsMessage72 message = getCellsMessage;
        if (ShouldDisconnectForCellRequestAbuse(message.Hashes.Length))
        {
            return Task.FromResult(new CellsMessage72(message.RequestId, [], [], BlobCellMask.Empty.ToBytes()));
        }

        BlobCellMask requestedMask = BlobCellMask.FromBytes(message.CellMask);
        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node:c} requested blob cells for {message.Hashes.Length} txs with mask {requestedMask}.");
        }

        List<Hash256> responseHashes = new(message.Hashes.Length);
        List<byte[][]> cellsByTx = new(message.Hashes.Length);
        BlobCellMask responseMask = BlobCellMask.Empty;

        for (int i = 0; i < message.Hashes.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Hash256 hash = message.Hashes[i];
            BlobCellMask cellsMask = responseHashes.Count == 0 ? requestedMask : responseMask;
            if (!TryBuildCellsResponse(hash, cellsMask, out BlobCellMask availableMask, out byte[][] cells))
            {
                continue;
            }

            if (responseHashes.Count == 0)
            {
                responseMask = availableMask;
                responseHashes.Add(hash);
                cellsByTx.Add(cells);
                continue;
            }

            if (availableMask == responseMask)
            {
                responseHashes.Add(hash);
                cellsByTx.Add(cells);
            }
        }

        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node:c} responding with blob cells for {responseHashes.Count} txs with mask {responseMask}.");
        }

        return Task.FromResult(new CellsMessage72(message.RequestId, responseHashes.ToArray(), cellsByTx.ToArray(), responseMask.ToBytes()));
    }

    private void Handle(CellsMessage72 message)
    {
        if (message.Hashes.Length != message.Cells.Length)
        {
            throw new SubprotocolException($"Wrong format of {nameof(CellsMessage72)} message. Hashes count: {message.Hashes.Length} Cells count: {message.Cells.Length}");
        }

        BlobCellMask responseMask = BlobCellMask.FromBytes(message.CellMask);
        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node:c} received blob cells for {message.Hashes.Length} txs with mask {responseMask}.");
        }

        if (responseMask.IsEmpty)
        {
            return;
        }

        for (int i = 0; i < message.Hashes.Length; i++)
        {
            Hash256 hash = message.Hashes[i];
            ValueHash256 key = hash.ValueHash256;
            if (!_sentCellRequests.TryGetValue(key, out CellRequestState sentRequestState))
            {
                continue;
            }

            BlobCellMask requestedMask = sentRequestState.Mask;
            if ((responseMask & requestedMask) != responseMask)
            {
                throw new SubprotocolException($"Unexpected cell mask in {nameof(CellsMessage72)} for {hash}.");
            }

            PendingCellsBuffer pending = new(responseMask, message.Cells[i], Id);
            if (_txPool.TryGetPendingBlobTransaction(hash, out Transaction? blobTx))
            {
                if (TryApplyPendingCells(hash, blobTx, pending, throwOnInvalid: true))
                {
                    RemovePendingCells(key);
                    RemoveCellRequestState(key);
                    ClearSparseRegistryIfFull(hash, responseMask);
                }

                continue;
            }

            ValidatePendingCellsBuffer(hash, pending);
            RemoveSentCellRequest(key);
            AddPendingCells(key, pending);
            _sparseBlobPoolPeerRegistry.RecordCells(this, hash, responseMask, message.Cells[i]);
            if (_txPool.TryGetPendingBlobTransaction(hash, out blobTx)
                && TryApplyPendingCells(hash, blobTx, pending, throwOnInvalid: true))
            {
                RemovePendingCells(key);
                RemoveCellRequestState(key);
                ClearSparseRegistryIfFull(hash, responseMask);
            }
        }
    }

    protected override ValueTask HandleSlow((IOwnedReadOnlyList<Transaction> txs, int startIndex) request, CancellationToken cancellationToken)
    {
        IOwnedReadOnlyList<Transaction> transactions = request.txs;
        ReadOnlySpan<Transaction> transactionsSpan = transactions.AsSpan();
        try
        {
            int startIdx = request.startIndex;
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

                    if (!BackgroundTaskScheduler.TryScheduleBackgroundTask((transactions, i), HandleSlow, "Transactions"))
                    {
                        transactions.Dispose();
                    }

                    return ValueTask.CompletedTask;
                }

                Transaction tx = transactionsSpan[i];
                if (!tx.SupportsBlobs)
                {
                    PrepareAndSubmitTransaction(tx, isTrace);
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
            if (TryApplyPendingCells(tx.Hash, tx, pending, throwOnInvalid: false))
            {
                RemovePendingCells(key);
                RemoveCellRequestState(key);
                ClearSparseRegistryIfFull(tx.Hash, pending.CellMask);
                appliedPendingCells = true;
            }
            else
            {
                Disconnect(DisconnectReason.BreachOfProtocol, $"Invalid buffered sparse blob cells for {tx.Hash}.");
                RemovePendingCells(key);
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

    private bool TryBuildCellsResponse(Hash256 hash, BlobCellMask requestedMask, out BlobCellMask availableMask, out byte[][] cells)
    {
        if (!_txPool.TryGetPendingBlobTransaction(hash, out Transaction? blobTx)
            || blobTx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes
            || requestedMask.IsEmpty)
        {
            availableMask = BlobCellMask.Empty;
            cells = [];
            return false;
        }

        if (!_txPool.TryGetBlobCells(hash, requestedMask, out availableMask, out byte[][]? availableCells)
            || availableMask.IsEmpty)
        {
            availableMask = BlobCellMask.Empty;
            cells = [];
            return false;
        }

        int expectedCells = blobVersionedHashes.Length * availableMask.Count;
        if (availableCells.Length != expectedCells)
        {
            throw new SubprotocolException(
                $"Wrong format of local blob cells for {hash}. Expected {expectedCells} flattened cells, got {availableCells.Length}.");
        }

        cells = availableCells;
        return true;
    }

    private bool TryApplyPendingCells(Hash256 hash, Transaction tx, PendingCellsBuffer pending, bool throwOnInvalid)
    {
        if (tx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes || pending.CellMask.IsEmpty)
        {
            return false;
        }

        int requestedCellsPerBlob = pending.CellMask.Count;
        if (requestedCellsPerBlob == 0)
        {
            return false;
        }

        int blobCount = blobVersionedHashes.Length;
        if (pending.Cells.Length != blobCount * requestedCellsPerBlob)
        {
            return InvalidPendingCells(throwOnInvalid,
                $"Wrong format of {nameof(CellsMessage72)} for {hash}. Expected {blobCount * requestedCellsPerBlob} flattened cells, got {pending.Cells.Length}.");
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
                    return InvalidPendingCells(throwOnInvalid, $"Invalid cell size {cell.Length} in {nameof(CellsMessage72)}.");
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
            return true;
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

        return _txPool.TryMergeBlobCells(hash, availableMask, flattenedCells);
    }

    private void SendGetCells(Hash256 hash, BlobCellMask requestMask)
    {
        if (Logger.IsDebug)
        {
            Logger.Debug($"{Node:c} requesting blob cells for {hash} with mask {requestMask}.");
        }

        AddSentCellRequest(hash.ValueHash256, requestMask);
        Send(new GetCellsMessage72([hash], requestMask.ToBytes()));
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

    private void AddPendingCellRequest(ValueHash256 hash, BlobCellMask requestMask)
    {
        lock (_cellStateLock)
        {
            if (_pendingCellRequests.TryGetValue(hash, out CellRequestState existing))
            {
                _pendingCellRequests[hash] = existing with { Mask = existing.Mask | requestMask };
                return;
            }

            long revision = NextCellStateRevision();
            _pendingCellRequests[hash] = new CellRequestState(requestMask, revision);
            _pendingCellRequestOrder.Enqueue(new CellStateKey(hash, revision));
            TrimPendingCellRequests();
        }
    }

    private void AddSentCellRequest(ValueHash256 hash, BlobCellMask requestMask)
    {
        lock (_cellStateLock)
        {
            if (_sentCellRequests.TryGetValue(hash, out CellRequestState existing))
            {
                _sentCellRequests[hash] = existing with { Mask = existing.Mask | requestMask };
                return;
            }

            long revision = NextCellStateRevision();
            _sentCellRequests[hash] = new CellRequestState(requestMask, revision);
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
                _pendingCells[hash] = existing with { Buffer = pending };
                return;
            }

            long revision = NextCellStateRevision();
            _pendingCells[hash] = new PendingCellsState(pending, revision);
            _pendingCellsOrder.Enqueue(new CellStateKey(hash, revision));
            TrimPendingCells();
        }
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
                _sentCellRequests.TryRemove(key.Hash, out _);
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
            _sentCellRequests.TryRemove(hash, out _);
        }
    }

    private void RemoveSentCellRequest(ValueHash256 hash)
    {
        lock (_cellStateLock)
        {
            _sentCellRequests.TryRemove(hash, out _);
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

    private void ClearSparseRegistryIfFull(Hash256 hash, BlobCellMask cellMask)
    {
        if (cellMask.IsFull)
        {
            _sparseBlobPoolPeerRegistry.Clear(hash);
        }
    }

    private long NextCellStateRevision() => ++_cellStateRevision;

    private static bool InvalidPendingCells(bool throwOnInvalid, string message)
    {
        if (throwOnInvalid)
        {
            throw new SubprotocolException(message);
        }

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

        return BlobCellMask.FromBytes(message.CellMask);
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
        if (_providerThresholdBasisPoints <= 0)
        {
            return false;
        }

        if (_providerThresholdBasisPoints >= MaxBasisPoints)
        {
            return true;
        }

        Span<byte> input = stackalloc byte[PublicKey.LengthInBytes + 32];
        _localNodeId.Bytes.CopyTo(input);
        hash.Bytes.CopyTo(input[PublicKey.LengthInBytes..]);
        Hash256 sampleHash = Keccak.Compute(input);
        ushort sample = BinaryPrimitives.ReadUInt16BigEndian(sampleHash.Bytes[..2]);
        return sample % MaxBasisPoints < _providerThresholdBasisPoints;
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

    private void OnBlobCustodyChanged(object? sender, BlobCellMask custodyMask)
    {
        int requests = _sparseBlobPoolPeerRegistry.RequestCellsForCustodyChange(custodyMask, HasSupernodeCustody(custodyMask));
        if (requests != 0 && Logger.IsDebug)
        {
            Logger.Debug($"{Node:c} scheduled {requests} sparse blob custody cell requests for mask {custodyMask}.");
        }
    }

    private bool IsSupernode => HasSupernodeCustody(_blobCustodyTracker.CurrentMask);

    private static bool HasSupernodeCustody(BlobCellMask custodyMask) => custodyMask.Count >= SupernodeCustodyColumnThreshold;

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
            sizes[i] = tx.GetLength();
            hashes[i] = tx.Hash!;
            TxPool.Metrics.PendingTransactionsHashesSent++;
        }

        Send(new NewPooledTransactionHashesMessage72(types, sizes, hashes, cellMask));
    }

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

    private readonly record struct PendingCellsBuffer(BlobCellMask CellMask, byte[][] Cells, PublicKey SourcePeerId);
    private readonly record struct CellRequestState(BlobCellMask Mask, long Revision);
    private readonly record struct PendingCellsState(PendingCellsBuffer Buffer, long Revision);
    private readonly record struct CellStateKey(ValueHash256 Hash, long Revision);
}
