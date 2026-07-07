// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>
    /// How long requested cells count as in flight before they may be re-requested,
    /// covering peers that never answer or disconnect mid-request.
    /// </summary>
    private static readonly TimeSpan DefaultCellRequestTimeout = TimeSpan.FromSeconds(5);
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
    private readonly TimeSpan _cellRequestTimeout;
    // Mirrors the custody tracker's all-cells default so the first real custody update does not
    // request the whole delta as if nothing had been fetched yet.
    private BlobCellMask _custodyMask = BlobCellMask.Full;

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
        TimeSpan maxAdmissionDelay,
        TimeSpan? cellRequestTimeout = null)
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
        _cellRequestTimeout = cellRequestTimeout ?? DefaultCellRequestTimeout;
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

        if (_txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask localMask))
        {
            requestMask = requestMask.Except(localMask);
        }

        ISparseBlobPoolPeer? firstPeer = null;
        BlobCellMask firstMask = BlobCellMask.Empty;
        List<(ISparseBlobPoolPeer Peer, BlobCellMask Mask)>? morePeers = null;
        bool placedAll;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (state.Lock)
        {
            if (now < state.InFlightUntil)
            {
                requestMask = requestMask.Except(state.InFlightMask);
            }
            else
            {
                state.InFlightMask = BlobCellMask.Empty;
            }

            if (requestMask.IsEmpty)
            {
                // Everything is already held locally or being fetched.
                return true;
            }

            // Requests are trimmed per peer, so keep selecting until the whole mask is placed or
            // no announcer remains; each selected peer takes its entire overlap, bounding the loop
            // by the number of announcers. Reserving before sending deduplicates concurrent
            // requests for the same cells.
            while (!requestMask.IsEmpty
                && SelectPeer(state, requestMask, lastResortPeerId, out BlobCellMask sendMask) is { } peer)
            {
                // The deadline is armed only on the empty->non-empty transition; extending it on
                // every reservation would let a dead request's bits stay in flight indefinitely.
                if (state.InFlightMask.IsEmpty)
                {
                    state.InFlightUntil = now + _cellRequestTimeout;
                }

                state.InFlightMask |= sendMask;
                requestMask = requestMask.Except(sendMask);
                if (firstPeer is null)
                {
                    firstPeer = peer;
                    firstMask = sendMask;
                }
                else
                {
                    (morePeers ??= []).Add((peer, sendMask));
                }
            }

            if (firstPeer is null)
            {
                return false;
            }

            placedAll = requestMask.IsEmpty;
        }

        placedAll &= TrySendReserved(state, hash, firstPeer, firstMask);
        if (morePeers is not null)
        {
            foreach ((ISparseBlobPoolPeer peer, BlobCellMask sendMask) in morePeers)
            {
                placedAll &= TrySendReserved(state, hash, peer, sendMask);
            }
        }

        // A false return makes the caller park the whole mask; the in-flight and local-pool
        // subtraction above deduplicates the already-placed part on retry.
        return placedAll;
    }

    private static bool TrySendReserved(TrackedSparseBlobTx state, Hash256 hash, ISparseBlobPoolPeer peer, BlobCellMask sendMask)
    {
        if (peer.TrySendGetCells(hash, sendMask))
        {
            return true;
        }

        lock (state.Lock)
        {
            state.InFlightMask = state.InFlightMask.Except(sendMask);
        }

        return false;
    }

    public void OnCellsRequestCompleted(Hash256 hash, BlobCellMask completedMask)
    {
        if (completedMask.IsEmpty || !_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return;
        }

        lock (state.Lock)
        {
            state.InFlightMask = state.InFlightMask.Except(completedMask);
        }
    }

    public void RemoveAnnouncement(PublicKey peerId, Hash256 hash)
    {
        if (_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            lock (state.Lock)
            {
                state.Announcements.Remove(peerId);
            }
        }
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

    /// <summary>
    /// Picks a peer to serve <paramref name="requestMask"/> and trims the request to what the
    /// peer announced. Must be called under <c>state.Lock</c>.
    /// </summary>
    /// <remarks>
    /// Cell responses are all-or-nothing per transaction, so a peer asked for cells outside its
    /// announcement replies empty. Peers whose announcement covers the whole request are preferred;
    /// otherwise the request is trimmed to a partial announcer's mask and the caller keeps
    /// selecting peers for the remainder.
    /// </remarks>
    private ISparseBlobPoolPeer? SelectPeer(TrackedSparseBlobTx state, BlobCellMask requestMask, PublicKey lastResortPeerId, out BlobCellMask sendMask)
    {
        sendMask = requestMask;
        if (state.Announcements.Count == 0)
        {
            return null;
        }

        ISparseBlobPoolPeer? lastResortPeer = null;
        BlobCellMask lastResortMask = BlobCellMask.Empty;
        ISparseBlobPoolPeer? coveringPeer = null;
        ISparseBlobPoolPeer? partialPeer = null;
        BlobCellMask partialMask = BlobCellMask.Empty;
        int coveringCount = 0;
        int partialCount = 0;
        foreach (KeyValuePair<PublicKey, BlobCellMask> announcement in state.Announcements)
        {
            BlobCellMask overlap = announcement.Value & requestMask;
            if (overlap.IsEmpty
                || !_peers.TryGetValue(announcement.Key, out ISparseBlobPoolPeer? peer)
                || peer.IsClosing)
            {
                continue;
            }

            if (peer.Id == lastResortPeerId)
            {
                lastResortPeer ??= peer;
                lastResortMask = overlap;
                continue;
            }

            // Reservoir sampling keeps the pick uniform without materializing candidate lists.
            if (overlap == requestMask)
            {
                coveringCount++;
                if (Random.Shared.Next(coveringCount) == 0)
                {
                    coveringPeer = peer;
                }
            }
            else
            {
                partialCount++;
                if (Random.Shared.Next(partialCount) == 0)
                {
                    partialPeer = peer;
                    partialMask = overlap;
                }
            }
        }

        if (coveringPeer is not null)
        {
            return coveringPeer;
        }

        if (partialPeer is not null)
        {
            sendMask = partialMask;
            return partialPeer;
        }

        sendMask = lastResortMask;
        return lastResortPeer;
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
        => _txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask availableMask)
            && availableMask.IsFull;

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
        => _txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask availableMask)
            ? requestedMask.Except(availableMask)
            : requestedMask;

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

        int blobCount = blobVersionedHashes.Length;
        if (!BlobCellsHelper.TryGetPresentCellMask(pending.Cells, pending.CellMask, blobCount, out BlobCellMask availableMask))
        {
            error = $"Wrong sparse blob cells shape for {hash}.";
            return false;
        }

        if (availableMask.IsEmpty)
        {
            error = $"No requested sparse blob cells available for {hash}.";
            return false;
        }

        byte[][] flattenedCells = BlobCellsHelper.SelectFlattenedCells(pending.Cells, pending.CellMask, availableMask, blobCount);
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
        public BlobCellMask InFlightMask { get; set; }
        public DateTimeOffset InFlightUntil { get; set; }
    }
}
