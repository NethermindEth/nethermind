// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

public sealed class SparseBlobPoolPeerRegistry : ISparseBlobPoolPeerRegistry, IDisposable
{
    internal const int SupernodeCustodyColumnThreshold = 64;
    private const int MaxTrackedTransactions = 8192;
    private const int MinIndependentProviderAnnouncements = 2;
    private const int MaxFullFallbackRequests = 12;
    private static readonly TimeSpan DefaultSaturationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScheduledActionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultMaxAdmissionDelay = TimeSpan.FromMilliseconds(64);
    private static readonly PublicKey NoLastResortPeer = new(new byte[PublicKey.LengthInBytes]);

    private readonly ITxPool _txPool;
    private readonly IBlobCustodyTracker _blobCustodyTracker;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<PublicKey, ISparseBlobPoolPeer> _peers = new();
    private readonly ConcurrentDictionary<ValueHash256, TrackedSparseBlobTx> _transactions = new();
    private readonly ConcurrentQueue<ValueHash256> _transactionOrder = new();
    private readonly Lock _custodyLock = new();
    private readonly TimeSpan _saturationTimeout;
    private readonly TimeSpan _maxAdmissionDelay;
    private BlobCellMask _custodyMask;

    public SparseBlobPoolPeerRegistry(
        ITxPool txPool,
        IBlobCustodyTracker blobCustodyTracker,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager)
        : this(txPool, blobCustodyTracker, backgroundTaskScheduler, logManager, DefaultSaturationTimeout, DefaultMaxAdmissionDelay)
    {
    }

    internal SparseBlobPoolPeerRegistry(
        ITxPool txPool,
        IBlobCustodyTracker blobCustodyTracker,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager,
        TimeSpan saturationTimeout,
        TimeSpan maxAdmissionDelay)
    {
        if (saturationTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(saturationTimeout));
        }

        if (maxAdmissionDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAdmissionDelay));
        }

        _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        _blobCustodyTracker = blobCustodyTracker ?? throw new ArgumentNullException(nameof(blobCustodyTracker));
        _backgroundTaskScheduler = backgroundTaskScheduler ?? throw new ArgumentNullException(nameof(backgroundTaskScheduler));
        _logger = (logManager ?? throw new ArgumentNullException(nameof(logManager))).GetClassLogger<SparseBlobPoolPeerRegistry>();
        _saturationTimeout = saturationTimeout;
        _maxAdmissionDelay = maxAdmissionDelay;
        _blobCustodyTracker.CustodyChanged += OnCustodyChanged;
    }

    public void Dispose() => _blobCustodyTracker.CustodyChanged -= OnCustodyChanged;

    internal static bool HasSupernodeCustody(BlobCellMask custodyMask) => custodyMask.Count >= SupernodeCustodyColumnThreshold;

    private void OnCustodyChanged(object? sender, BlobCellMask custodyMask)
    {
        int requests = RequestCellsForCustodyChange(custodyMask, HasSupernodeCustody(custodyMask));
        if (requests != 0 && _logger.IsDebug)
        {
            _logger.Debug($"Scheduled {requests} sparse blob custody cell requests for mask {custodyMask}.");
        }
    }

    public void AddPeer(ISparseBlobPoolPeer peer) => _peers[peer.Id] = peer;

    public void RemovePeer(PublicKey peerId)
    {
        _peers.TryRemove(peerId, out _);
        foreach (KeyValuePair<ValueHash256, TrackedSparseBlobTx> transaction in _transactions)
        {
            TrackedSparseBlobTx state = transaction.Value;
            lock (state.Lock)
            {
                state.Announcements.Remove(peerId);
            }
        }
    }

    public void RecordAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask announcementMask)
    {
        if (announcementMask.IsEmpty || !IsActivePeer(peer))
        {
            return;
        }

        if (HasFullLocalBlobTransaction(hash))
        {
            return;
        }

        TrackedSparseBlobTx state = GetOrAdd(hash);
        lock (state.Lock)
        {
            if (!IsActivePeer(peer))
            {
                return;
            }

            state.Announcements[peer.Id] = announcementMask;
        }

        ScheduleSaturationCheck(hash);
    }

    public bool TryRequestCells(Hash256 hash, BlobCellMask requestMask, PublicKey lastResortPeerId)
    {
        if (requestMask.IsEmpty || !_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return false;
        }

        ISparseBlobPoolPeer? peer = SelectPeer(state, requestMask, lastResortPeerId);
        if (peer is null)
        {
            return false;
        }

        return peer.TrySendGetCells(hash, requestMask);
    }

    public int RequestCellsForCustodyChange(BlobCellMask newCustodyMask, bool requestAllAnnouncedCells)
    {
        BlobCellMask requestMaskTemplate;
        lock (_custodyLock)
        {
            requestMaskTemplate = requestAllAnnouncedCells
                ? BlobCellMask.Full
                : new BlobCellMask(newCustodyMask.Value & ~_custodyMask.Value);
            _custodyMask = newCustodyMask;
        }

        if (requestMaskTemplate.IsEmpty)
        {
            return 0;
        }

        int requests = 0;
        foreach (KeyValuePair<ValueHash256, TrackedSparseBlobTx> entry in _transactions)
        {
            Hash256 hash = entry.Key.ToHash256();
            BlobCellMask requestMask = requestAllAnnouncedCells
                ? GetMissingAnnouncedMask(hash, entry.Value)
                : GetMissingLocalMask(hash, requestMaskTemplate);
            if (requestMask.IsEmpty)
            {
                continue;
            }

            if (TryRequestCells(hash, requestMask, NoLastResortPeer))
            {
                requests++;
            }
        }

        return requests;
    }

    public bool HasRecordedTransaction(Hash256 hash)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return false;
        }

        lock (state.Lock)
        {
            return state.Transaction is not null;
        }
    }

    public int GetFullProviderAnnouncementCount(Hash256 hash)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return 0;
        }

        int count = 0;
        lock (state.Lock)
        {
            foreach (KeyValuePair<PublicKey, BlobCellMask> announcement in state.Announcements)
            {
                if (announcement.Value.IsFull && IsActivePeer(announcement.Key))
                {
                    count++;
                }
            }
        }

        return count;
    }

    public AcceptTxResult? RecordTransaction(ISparseBlobPoolPeer peer, Transaction transaction)
    {
        Hash256? hash = transaction.Hash;
        if (hash is null || !transaction.SupportsBlobs)
        {
            return SubmitTransaction(peer, transaction);
        }

        if (transaction.NetworkWrapper is ShardBlobNetworkWrapper wrapper && wrapper.HasFullBlobs())
        {
            return SubmitTransaction(peer, transaction);
        }

        BlobCellMask attachedCellMask = BlobCellMask.Empty;
        byte[][]? attachedCells = null;
        if (transaction.NetworkWrapper is ShardBlobNetworkWrapper sparseWrapper
            && sparseWrapper.Cells is { Length: > 0 } wrapperCells)
        {
            attachedCellMask = sparseWrapper.GetAvailableCellMask();
            if (!attachedCellMask.IsEmpty)
            {
                attachedCells = wrapperCells;
            }
        }

        TrackedSparseBlobTx state = GetOrAdd(hash);
        lock (state.Lock)
        {
            state.Transaction ??= transaction;
            state.TransactionPeer ??= peer;
            if (attachedCells is not null
                && (state.Cells is not { } existingCells
                    || (existingCells.CellMask & attachedCellMask) != attachedCellMask))
            {
                state.Cells = new PendingCellsBuffer(attachedCellMask, attachedCells, peer.Id);
            }
        }

        return TrySubmit(hash, state);
    }

    public bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells)
    {
        if (cellMask.IsEmpty || cells.Length == 0)
        {
            return false;
        }

        TrackedSparseBlobTx state = GetOrAdd(hash);
        lock (state.Lock)
        {
            if (state.Cells is { } existingCells
                && (existingCells.CellMask & cellMask) == cellMask)
            {
                return true;
            }

            state.Cells = new PendingCellsBuffer(cellMask, cells, peer.Id);
        }

        TrySubmit(hash, state);
        return true;
    }

    public void Clear(Hash256 hash) => _transactions.TryRemove(hash.ValueHash256, out _);

    private TrackedSparseBlobTx GetOrAdd(Hash256 hash)
    {
        ValueHash256 key = hash.ValueHash256;
        if (_transactions.TryGetValue(key, out TrackedSparseBlobTx? existing))
        {
            return existing;
        }

        TrackedSparseBlobTx state = new(DateTimeOffset.UtcNow + GetAdmissionDelay(hash));
        if (_transactions.TryAdd(key, state))
        {
            _transactionOrder.Enqueue(key);
            TrimTrackedTransactions();
            return state;
        }

        return _transactions[key];
    }

    private ISparseBlobPoolPeer? SelectPeer(TrackedSparseBlobTx state, BlobCellMask requestMask, PublicKey lastResortPeerId)
    {
        lock (state.Lock)
        {
            if (state.Announcements.Count == 0)
            {
                return null;
            }

            ISparseBlobPoolPeer? lastResortPeer = null;
            ISparseBlobPoolPeer? selectedPeer = null;
            int candidateCount = 0;
            foreach (KeyValuePair<PublicKey, BlobCellMask> announcement in state.Announcements)
            {
                if ((announcement.Value & requestMask).IsEmpty
                    || !_peers.TryGetValue(announcement.Key, out ISparseBlobPoolPeer? peer)
                    || peer.IsClosing)
                {
                    continue;
                }

                if (peer.Id == lastResortPeerId)
                {
                    lastResortPeer ??= peer;
                    continue;
                }

                candidateCount++;
                if (Random.Shared.Next(candidateCount) == 0)
                {
                    selectedPeer = peer;
                }
            }

            // Prefer a random announced provider rather than always leaning on the same peer.
            return selectedPeer ?? lastResortPeer;
        }
    }

    private AcceptTxResult? TrySubmit(Hash256 hash, TrackedSparseBlobTx state)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? current)
            || !ReferenceEquals(current, state))
        {
            return null;
        }

        Transaction? transaction;
        ISparseBlobPoolPeer? transactionPeer;
        PendingCellsBuffer? cells;
        DateTimeOffset notBefore;
        bool requiresSaturation;
        lock (state.Lock)
        {
            transaction = state.Transaction;
            transactionPeer = state.TransactionPeer;
            cells = state.Cells;
            notBefore = state.NotBefore;
            if (state.Submitted || state.Submitting)
            {
                return null;
            }

            if (transaction is null || cells is null)
            {
                return null;
            }

            requiresSaturation = !cells.Value.CellMask.IsFull;

            if (DateTimeOffset.UtcNow < notBefore)
            {
                ScheduleAdmission(hash, state, notBefore - DateTimeOffset.UtcNow);
                return null;
            }

            state.Submitting = true;
        }

        if (!TryAttachCells(hash, transaction, cells.Value, out string? error, out SparseBlobAttachFailureSource failureSource))
        {
            PublicKey retryFallbackPeerId = cells.Value.SourcePeerId;
            if (failureSource != SparseBlobAttachFailureSource.Ambiguous)
            {
                retryFallbackPeerId = GetBadPeerId(transactionPeer, cells.Value.SourcePeerId, failureSource);
                DisconnectPeer(retryFallbackPeerId, DisconnectReason.BreachOfProtocol, error ?? "invalid sparse blob cells");
                RemovePeer(retryFallbackPeerId);
            }

            lock (state.Lock)
            {
                state.Cells = null;
                if (failureSource == SparseBlobAttachFailureSource.Transaction)
                {
                    state.Transaction = null;
                    state.TransactionPeer = null;
                }

                state.Submitting = false;
            }

            TryRequestCells(hash, cells.Value.CellMask, retryFallbackPeerId);
            return null;
        }

        AcceptTxResult result = SubmitTransaction(transactionPeer, transaction);
        if (result == AcceptTxResult.Invalid)
        {
            TryRemoveState(hash, state);
            transactionPeer?.DisconnectSparseBlobPeer(DisconnectReason.InvalidTxReceived, $"Invalid sparse blob transaction {hash}");
        }
        else if (result == AcceptTxResult.Accepted || result == AcceptTxResult.AlreadyKnown)
        {
            lock (state.Lock)
            {
                state.Submitted = true;
                state.Submitting = false;
                state.Transaction = null;
                state.TransactionPeer = null;
                state.Cells = null;
            }

            if (requiresSaturation)
            {
                ScheduleSaturationCheck(hash);
            }
            else
            {
                TryRemoveState(hash, state);
            }
        }
        else
        {
            TryRemoveState(hash, state);
        }

        return result;
    }

    private AcceptTxResult SubmitTransaction(ISparseBlobPoolPeer? peer, Transaction transaction)
    {
        transaction.Timestamp = Timestamper.Default.UnixTime.Seconds;
        AcceptTxResult result = _txPool.SubmitTx(transaction, TxHandlingOptions.None);
        if (_logger.IsTrace)
        {
            _logger.Trace($"{peer?.Id} sent sparse blob tx {transaction.Hash} and it was {result}");
        }

        return result;
    }

    private void ScheduleAdmission(Hash256 hash, TrackedSparseBlobTx state, TimeSpan delay)
    {
        lock (state.Lock)
        {
            if (state.AdmissionScheduled)
            {
                return;
            }

            state.AdmissionScheduled = true;
        }

        ScheduleDelayedTask(
            (Registry: this, Hash: hash, State: state),
            delay,
            static (request, _) =>
            {
                lock (request.State.Lock)
                {
                    request.State.AdmissionScheduled = false;
                }

                request.Registry.TrySubmit(request.Hash, request.State);
                return Task.CompletedTask;
            });
    }

    private void ScheduleSaturationCheck(Hash256 hash)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return;
        }

        lock (state.Lock)
        {
            if (state.SaturationCheckScheduled)
            {
                return;
            }

            state.SaturationCheckScheduled = true;
        }

        ScheduleDelayedTask(
            (Registry: this, Hash: hash, State: state),
            _saturationTimeout,
            static (request, _) =>
            {
                request.Registry.CheckSaturation(request.Hash, request.State);
                return Task.CompletedTask;
            });
    }

    private void ScheduleDelayedTask<TReq>(
        TReq request,
        TimeSpan delay,
        Func<TReq, CancellationToken, Task> fulfillFunc)
        => _ = ScheduleDelayedTaskAsync(request, delay, fulfillFunc);

    private async Task ScheduleDelayedTaskAsync<TReq>(
        TReq request,
        TimeSpan delay,
        Func<TReq, CancellationToken, Task> fulfillFunc)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            if (!_backgroundTaskScheduler.TryScheduleTask(request, fulfillFunc, timeout: ScheduledActionTimeout, source: nameof(SparseBlobPoolPeerRegistry)))
            {
                await fulfillFunc(request, CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Error processing delayed sparse blob pool task.", e);
        }
    }

    private void CheckSaturation(Hash256 hash, TrackedSparseBlobTx state)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? current)
            || !ReferenceEquals(current, state))
        {
            return;
        }

        if (HasFullLocalBlobTransaction(hash))
        {
            TryRemoveState(hash, state);
            return;
        }

        int providers = 0;
        bool hasFullProvider = false;
        bool shouldRequestFull = false;
        bool submitted;
        int fullFallbackRequests;
        lock (state.Lock)
        {
            state.SaturationCheckScheduled = false;

            foreach (KeyValuePair<PublicKey, BlobCellMask> announcement in state.Announcements)
            {
                if (announcement.Value.IsFull && IsActivePeer(announcement.Key))
                {
                    providers++;
                    hasFullProvider = true;
                }
            }

            submitted = state.Submitted;
            fullFallbackRequests = state.FullFallbackRequests;

            if (!submitted && providers >= MinIndependentProviderAnnouncements)
            {
                return;
            }

            shouldRequestFull = hasFullProvider && fullFallbackRequests < MaxFullFallbackRequests;
            if (shouldRequestFull)
            {
                state.FullFallbackRequests++;
                fullFallbackRequests = state.FullFallbackRequests;
            }
        }

        if (_logger.IsDebug)
        {
            _logger.Debug(
                $"Sparse blob tx {hash} saturation check: submitted={submitted}, full providers={providers}, full fallback requests={fullFallbackRequests}, requesting full={shouldRequestFull}.");
        }

        if (shouldRequestFull)
        {
            bool requestSent = TryRequestCells(hash, BlobCellMask.Full, NoLastResortPeer);
            if (_logger.IsDebug)
            {
                _logger.Debug($"Sparse blob tx {hash} full-cell fallback request sent={requestSent}.");
            }

            if (requestSent)
            {
                ScheduleSaturationCheck(hash);
                return;
            }
        }

        if (submitted)
        {
            if (_logger.IsDebug)
            {
                _logger.Debug($"Keeping sparse blob transaction {hash} after saturation timeout with {providers} independent provider announcements.");
            }

            return;
        }

        TryRemoveState(hash, state);
    }

    private bool HasFullLocalBlobTransaction(Hash256 hash)
        => _txPool.TryGetPendingBlobTransaction(hash, out Transaction? blobTx)
            && blobTx is not null
            && blobTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && wrapper.GetAvailableCellMask().IsFull;

    private bool IsActivePeer(ISparseBlobPoolPeer peer)
        => _peers.TryGetValue(peer.Id, out ISparseBlobPoolPeer? registeredPeer)
            && ReferenceEquals(registeredPeer, peer)
            && !peer.IsClosing;

    private bool IsActivePeer(PublicKey peerId)
        => _peers.TryGetValue(peerId, out ISparseBlobPoolPeer? peer)
            && !peer.IsClosing;

    private BlobCellMask GetMissingAnnouncedMask(Hash256 hash, TrackedSparseBlobTx state)
    {
        BlobCellMask announcedMask = BlobCellMask.Empty;
        lock (state.Lock)
        {
            foreach (KeyValuePair<PublicKey, BlobCellMask> announcement in state.Announcements)
            {
                if (IsActivePeer(announcement.Key))
                {
                    announcedMask |= announcement.Value;
                }
            }
        }

        return GetMissingLocalMask(hash, announcedMask);
    }

    private BlobCellMask GetMissingLocalMask(Hash256 hash, BlobCellMask requestedMask)
    {
        if (_txPool.TryGetBlobCells(hash, requestedMask, out BlobCellMask availableMask, out _))
        {
            return new BlobCellMask(requestedMask.Value & ~availableMask.Value);
        }

        return requestedMask;
    }

    private bool TryRemoveState(Hash256 hash, TrackedSparseBlobTx state)
        => ((ICollection<KeyValuePair<ValueHash256, TrackedSparseBlobTx>>)_transactions)
            .Remove(new KeyValuePair<ValueHash256, TrackedSparseBlobTx>(hash.ValueHash256, state));

    private void DisconnectPeer(PublicKey peerId, DisconnectReason reason, string details)
    {
        if (_peers.TryGetValue(peerId, out ISparseBlobPoolPeer? peer))
        {
            peer.DisconnectSparseBlobPeer(reason, details);
        }
    }

    private static bool TryAttachCells(
        Hash256 hash,
        Transaction tx,
        PendingCellsBuffer pending,
        out string? error,
        out SparseBlobAttachFailureSource failureSource)
    {
        error = null;
        failureSource = SparseBlobAttachFailureSource.Cells;
        if (tx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes
            || tx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper
            || pending.CellMask.IsEmpty)
        {
            error = $"Wrong sparse blob transaction form for {hash}.";
            failureSource = SparseBlobAttachFailureSource.Transaction;
            return false;
        }

        int requestedCellsPerBlob = pending.CellMask.Count;
        int blobCount = blobVersionedHashes.Length;
        if (pending.Cells.Length != blobCount * requestedCellsPerBlob)
        {
            error = $"Wrong sparse blob cells count for {hash}.";
            return false;
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
                if (cell.Length is not 0 and not Ckzg.BytesPerCell)
                {
                    error = $"Invalid sparse blob cell size {cell.Length} for {hash}.";
                    return false;
                }

                presentForAllBlobs &= cell.Length == Ckzg.BytesPerCell;
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
            error = $"No requested sparse blob cells available for {hash}.";
            return false;
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

        ShardBlobNetworkWrapper sparseWrapper = wrapper with { CellMask = availableMask, Cells = flattenedCells };
        if (!BlobCellsHelper.ValidateCells(sparseWrapper))
        {
            error = $"Invalid sparse blob cell proofs for {hash}.";
            failureSource = SparseBlobAttachFailureSource.Ambiguous;
            return false;
        }

        tx.NetworkWrapper = sparseWrapper;
        tx.ClearLengthCache();
        return true;
    }

    private static PublicKey GetBadPeerId(
        ISparseBlobPoolPeer? transactionPeer,
        PublicKey cellsPeerId,
        SparseBlobAttachFailureSource failureSource)
        => failureSource == SparseBlobAttachFailureSource.Transaction && transactionPeer is not null
            ? transactionPeer.Id
            : cellsPeerId;

    private void TrimTrackedTransactions()
    {
        while (_transactions.Count > MaxTrackedTransactions && _transactionOrder.TryDequeue(out ValueHash256 hash))
        {
            _transactions.TryRemove(hash, out _);
        }
    }

    private TimeSpan GetAdmissionDelay(Hash256 hash)
    {
        int maxMilliseconds = (int)_maxAdmissionDelay.TotalMilliseconds;
        if (maxMilliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        int delay = (hash.Bytes[0] << 8 | hash.Bytes[1]) % (maxMilliseconds + 1);
        return TimeSpan.FromMilliseconds(delay);
    }

    private readonly record struct PendingCellsBuffer(BlobCellMask CellMask, byte[][] Cells, PublicKey SourcePeerId);

    private enum SparseBlobAttachFailureSource
    {
        Cells,
        Transaction,
        Ambiguous
    }

    private sealed class TrackedSparseBlobTx(DateTimeOffset notBefore)
    {
        public Lock Lock { get; } = new();
        public DateTimeOffset NotBefore { get; } = notBefore;
        public Dictionary<PublicKey, BlobCellMask> Announcements { get; } = [];
        public Transaction? Transaction { get; set; }
        public ISparseBlobPoolPeer? TransactionPeer { get; set; }
        public PendingCellsBuffer? Cells { get; set; }
        public bool AdmissionScheduled { get; set; }
        public bool SaturationCheckScheduled { get; set; }
        public int FullFallbackRequests { get; set; }
        public bool Submitted { get; set; }
        public bool Submitting { get; set; }
    }
}
