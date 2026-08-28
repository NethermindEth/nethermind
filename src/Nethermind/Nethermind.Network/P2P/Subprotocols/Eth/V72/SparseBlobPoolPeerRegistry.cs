// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>
/// Coordinates bounded sparse blob state, peer selection, custody changes, and cell-serving capacity across eth/72 peers.
/// </summary>
public sealed class SparseBlobPoolPeerRegistry : ISparseBlobPoolPeerRegistry, IDisposable
{
    internal const int SupernodeCustodyColumnThreshold = 64;
    private const int MaxTrackedTransactions = 16384;
    private const int MinProviderProbabilityPercent = 15;
    private const int MaxProviderProbabilityPercent = 100;
    private const int MaxAnnouncementsPerPeer = 2048;
    private const int MaxRetainedAnnouncementsPerPeer = 2048;
    private const int MaxAmbiguousValidationFailures = 3;
    private const int MaxEarlyCellsPerTransactionBytes = 2 * 1024 * 1024;
    private const long MaxEarlyCellsBytes = 64L * 1024 * 1024;
    private const long MaxEarlyCellsBytesPerPeer = 16L * 1024 * 1024;
    private const long MaxTrackedTransactionBytes = 64L * 1024 * 1024;
    private const long MaxTrackedTransactionBytesPerPeer = 16L * 1024 * 1024;
    private const int MaxInFlightCellWork = 8192;
    private const int MaxInFlightCellWorkPerPeer = 2048;
    private const int GlobalCellServeTokenCapacity = 4096;
    private const int GlobalCellServeTokensPerSecond = 768;
    private const int MaxConcurrentCellServeOperations = 2;
    private const int SamplingSecretLength = 32;
    /// <summary>
    /// How long requested cells count as in flight before they may be re-requested,
    /// covering peers that never answer or disconnect mid-request.
    /// </summary>
    private static readonly TimeSpan DefaultCellRequestTimeout = Timeouts.Eth;
    private static readonly TimeSpan ScheduledActionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultMaxAdmissionDelay = TimeSpan.FromMilliseconds(64);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EarlyCellsTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TrackedStateTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxTrackedStateLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AmbiguousCellValidationQuarantine = TimeSpan.FromMinutes(5);
    private static readonly PublicKey NoLastResortPeer = new(new byte[PublicKey.LengthInBytes]);

    private readonly ITxPool _txPool;
    private readonly IBlobCustodyTracker _blobCustodyTracker;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<PublicKey, ISparseBlobPoolPeer> _peers = new();
    private readonly ConcurrentDictionary<ValueHash256, TrackedSparseBlobTx> _transactions = new();
    private readonly ConcurrentQueue<TrackedStateKey> _transactionOrder = new();
    private readonly byte[] _samplingSecret = RandomNumberGenerator.GetBytes(SamplingSecretLength);
    private readonly Lock _custodyLock = new();
    private readonly Lock _peerLifecycleLock = new();
    private readonly Lock _accountingLock = new();
    private readonly Lock _transactionOrderLock = new();
    private readonly Lock _cellServeLock = new();
    private readonly SemaphoreSlim _cellServeConcurrency = new(MaxConcurrentCellServeOperations, MaxConcurrentCellServeOperations);
    private readonly Dictionary<PublicKey, PeerUsage> _peerUsage = [];
    private readonly TimeSpan _maxAdmissionDelay;
    private readonly TimeSpan _cellRequestTimeout;
    private readonly ITimestamper _timestamper;
    private readonly Nethermind.Core.Timers.ITimer _maintenanceTimer;
    private BlobCellMask _custodyMask;
    private long _trackedStateRevision;
    private long _earlyCellsBytes;
    private long _trackedTransactionBytes;
    private int _inFlightCellWork;
    private int _trackedTransactionCount;
    private int _transactionOrderCount;
    private double _cellServeTokens = GlobalCellServeTokenCapacity;
    private DateTimeOffset _cellServeTokensUpdatedAt;
    private int _maintenanceScheduled;
    private int _custodyUpdateScheduled;
    private int _activeCellServeOperations;
    private bool _cellServeConcurrencyDisposed;
    private long _custodyUpdateRevision;
    private long _appliedCustodyUpdateRevision;
    private BlobCellMask _pendingCustodyMask;
    private int _disposed;

    /// <summary>Initializes a node-wide sparse blob peer registry.</summary>
    /// <param name="txPool">The transaction pool used for sparse admission and cell lookup.</param>
    /// <param name="blobCustodyTracker">The source of local custody changes.</param>
    /// <param name="backgroundTaskScheduler">The bounded scheduler for maintenance and custody scans.</param>
    /// <param name="logManager">The log manager.</param>
    public SparseBlobPoolPeerRegistry(
        ITxPool txPool,
        IBlobCustodyTracker blobCustodyTracker,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager)
        : this(txPool, blobCustodyTracker, backgroundTaskScheduler, logManager, DefaultMaxAdmissionDelay)
    {
    }

    internal SparseBlobPoolPeerRegistry(
        ITxPool txPool,
        IBlobCustodyTracker blobCustodyTracker,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager,
        TimeSpan maxAdmissionDelay,
        TimeSpan? cellRequestTimeout = null,
        ITimerFactory? timerFactory = null,
        ITimestamper? timestamper = null)
    {
        ArgumentNullException.ThrowIfNull(txPool);
        ArgumentNullException.ThrowIfNull(blobCustodyTracker);
        ArgumentNullException.ThrowIfNull(backgroundTaskScheduler);
        ArgumentNullException.ThrowIfNull(logManager);

        if (maxAdmissionDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAdmissionDelay));
        }

        if (cellRequestTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cellRequestTimeout));
        }

        _txPool = txPool;
        _blobCustodyTracker = blobCustodyTracker;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _logger = logManager.GetClassLogger<SparseBlobPoolPeerRegistry>();
        _maxAdmissionDelay = maxAdmissionDelay;
        _cellRequestTimeout = cellRequestTimeout ?? DefaultCellRequestTimeout;
        _timestamper = timestamper ?? Timestamper.Default;
        _cellServeTokensUpdatedAt = _timestamper.UtcNowOffset;
        _maintenanceTimer = (timerFactory ?? TimerFactory.Default).CreateTimer(MaintenanceInterval);
        _maintenanceTimer.AutoReset = true;
        _maintenanceTimer.Elapsed += OnMaintenanceElapsed;
        _maintenanceTimer.Start();
        _blobCustodyTracker.CustodyChanged += OnCustodyChanged;
        _custodyMask = _blobCustodyTracker.CurrentMask;
        _txPool.NewPending += OnPendingTransactionAdded;
        _txPool.RemovedPending += OnPendingTransactionRemoved;
        _txPool.EvictedPending += OnPendingTransactionRemoved;
    }

    /// <summary>Stops maintenance and releases registry resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _blobCustodyTracker.CustodyChanged -= OnCustodyChanged;
        _txPool.NewPending -= OnPendingTransactionAdded;
        _txPool.RemovedPending -= OnPendingTransactionRemoved;
        _txPool.EvictedPending -= OnPendingTransactionRemoved;
        _maintenanceTimer.Elapsed -= OnMaintenanceElapsed;
        _maintenanceTimer.Dispose();
        lock (_cellServeLock)
        {
            if (_activeCellServeOperations == 0)
            {
                DisposeCellServeConcurrency();
            }
        }
    }

    internal static bool HasSupernodeCustody(BlobCellMask custodyMask) => custodyMask.Count >= SupernodeCustodyColumnThreshold;

    private void OnCustodyChanged(object? sender, BlobCellMask custodyMask)
    {
        custodyMask = _blobCustodyTracker.CurrentMask;
        lock (_custodyLock)
        {
            _pendingCustodyMask = custodyMask;
            _custodyUpdateRevision++;
        }

        TrySchedulePendingCustodyUpdate();
    }

    private void TrySchedulePendingCustodyUpdate()
    {
        lock (_custodyLock)
        {
            if (_appliedCustodyUpdateRevision == _custodyUpdateRevision)
            {
                return;
            }
        }

        if (Interlocked.CompareExchange(ref _custodyUpdateScheduled, 1, 0) != 0)
        {
            return;
        }

        if (!_backgroundTaskScheduler.TryScheduleTask(
            new ScheduledRegistryRequest(this),
            static (request, cancellationToken) => request.Registry.ApplyPendingCustodyChange(cancellationToken),
            timeout: ScheduledActionTimeout,
            source: nameof(BlobCustodyTracker)))
        {
            Interlocked.Exchange(ref _custodyUpdateScheduled, 0);
        }
    }

    private Task ApplyPendingCustodyChange(CancellationToken cancellationToken)
    {
        bool completedScan = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                BlobCellMask custodyMask;
                long revision;
                lock (_custodyLock)
                {
                    custodyMask = _pendingCustodyMask;
                    revision = _custodyUpdateRevision;
                }

                if (!RequestCellsForCustodyChange(
                    custodyMask,
                    HasSupernodeCustody(custodyMask),
                    cancellationToken,
                    out int requests))
                {
                    break;
                }

                if (requests != 0 && _logger.IsDebug)
                {
                    _logger.Debug($"Scheduled {requests} sparse blob custody cell requests for mask {custodyMask}.");
                }

                lock (_custodyLock)
                {
                    if (revision != _custodyUpdateRevision)
                    {
                        continue;
                    }

                    _custodyMask = custodyMask;
                    _appliedCustodyUpdateRevision = revision;
                    completedScan = true;
                    break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _custodyUpdateScheduled, 0);
        }

        if (completedScan)
        {
            TrySchedulePendingCustodyUpdate();
        }

        return Task.CompletedTask;
    }

    private void OnPendingTransactionRemoved(object? sender, TxEventArgs e)
    {
        if (e.Transaction.SupportsBlobs && e.Transaction.Hash is not null)
        {
            Clear(e.Transaction.Hash);
        }
    }

    private void OnPendingTransactionAdded(object? sender, TxEventArgs e)
    {
        if (e.Transaction.SupportsBlobs && e.Transaction.Hash is not null)
        {
            TryApplyRecordedCells(e.Transaction.Hash);
        }
    }

    /// <inheritdoc/>
    public void AddPeer(ISparseBlobPoolPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        List<PeerCleanupAction>? cleanupActions = null;
        lock (_peerLifecycleLock)
        {
            if (_peers.TryGetValue(peer.Id, out ISparseBlobPoolPeer? current))
            {
                if (ReferenceEquals(current, peer))
                {
                    return;
                }

                ((ICollection<KeyValuePair<PublicKey, ISparseBlobPoolPeer>>)_peers)
                    .Remove(new KeyValuePair<PublicKey, ISparseBlobPoolPeer>(peer.Id, current));
                cleanupActions = CollectPeerCleanupActions(peer.Id);
            }

            _peers[peer.Id] = peer;
        }

        ProcessPeerCleanupActions(cleanupActions, peer.Id);
    }

    /// <inheritdoc/>
    public void RemovePeer(ISparseBlobPoolPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        List<PeerCleanupAction>? cleanupActions = null;
        lock (_peerLifecycleLock)
        {
            if (((ICollection<KeyValuePair<PublicKey, ISparseBlobPoolPeer>>)_peers)
                .Remove(new KeyValuePair<PublicKey, ISparseBlobPoolPeer>(peer.Id, peer)))
            {
                cleanupActions = CollectPeerCleanupActions(peer.Id);
            }
        }

        ProcessPeerCleanupActions(cleanupActions, peer.Id);
    }

    private List<PeerCleanupAction>? CollectPeerCleanupActions(PublicKey peerId)
    {
        ValueHash256[] trackedHashes;
        lock (_accountingLock)
        {
            if (!_peerUsage.TryGetValue(peerId, out PeerUsage? usage) || usage.TrackedHashes.Count == 0)
            {
                return null;
            }

            trackedHashes = [.. usage.TrackedHashes.Keys];
        }

        List<PeerCleanupAction>? cleanupActions = null;
        for (int i = 0; i < trackedHashes.Length; i++)
        {
            ValueHash256 trackedHash = trackedHashes[i];
            if (!_transactions.TryGetValue(trackedHash, out TrackedSparseBlobTx? state))
            {
                continue;
            }

            BlobCellMask retryMask = BlobCellMask.Empty;
            ISparseBlobPoolPeer? transactionRetryPeer = null;
            bool submit = false;
            bool remove = false;
            lock (state.Lock)
            {
                if (state.Announcements.Remove(peerId))
                {
                    ReleaseAnnouncement(peerId, trackedHash, state.Submitted);
                }

                if (state.InFlightByPeer.Remove(peerId, out BlobCellMask inFlightMask))
                {
                    state.InFlightRevisionByPeer.Remove(peerId);
                    state.InFlightMask = state.InFlightMask.Except(inFlightMask);
                    ReleaseInFlight(peerId, trackedHash, inFlightMask.Count, releaseHashReference: true);
                    retryMask = inFlightMask;
                }

                bool transactionRemoved = false;
                if (!state.Submitting && state.TransactionPeer?.Id == peerId)
                {
                    ReleaseTransaction(state);
                    state.Transaction = null;
                    state.TransactionPeer = null;
                    transactionRemoved = true;
                }

                if (!state.Submitting
                    && !state.ApplyingRecordedCells
                    && state.Cells is { } currentCells
                    && currentCells.TryRemoveSource(peerId, out PendingCellsBuffer? remainingCells))
                {
                    BlobCellMask remainingMask = remainingCells?.CellMask ?? BlobCellMask.Empty;
                    retryMask |= currentCells.CellMask.Except(remainingMask);
                    if (remainingCells is { } retained
                        && TryReplaceCellsAccounting(trackedHash, currentCells, retained))
                    {
                        state.Cells = retained;
                    }
                    else
                    {
                        retryMask |= currentCells.CellMask;
                        ReleaseCells(state);
                        state.Cells = null;
                    }
                }

                if (transactionRemoved)
                {
                    transactionRetryPeer = state.Cells is { } cells
                        ? SelectTransactionRetryPeer(cells, transactionPeer: null)
                        : null;
                    transactionRetryPeer ??= SelectTransactionRetryPeer(state);
                }

                submit = state.Transaction is not null
                    && state.Cells is not null
                    && !state.Submitted
                    && !state.Submitting;
                remove = state.Announcements.Count == 0
                    && state.Transaction is null
                    && state.Cells is null
                    && state.InFlightByPeer.Count == 0
                    && !state.Submitted
                    && !state.Submitting;
            }

            Hash256 hash = trackedHash.ToHash256();
            if (remove)
            {
                TryRemoveState(hash, state);
                continue;
            }

            if (transactionRetryPeer is not null || !retryMask.IsEmpty || submit)
            {
                (cleanupActions ??= []).Add(new PeerCleanupAction(
                    hash,
                    state,
                    transactionRetryPeer,
                    retryMask,
                    submit));
            }
        }

        return cleanupActions;
    }

    private void ProcessPeerCleanupActions(List<PeerCleanupAction>? cleanupActions, PublicKey removedPeerId)
    {
        if (cleanupActions is null)
        {
            return;
        }

        for (int i = 0; i < cleanupActions.Count; i++)
        {
            PeerCleanupAction action = cleanupActions[i];
            action.TransactionRetryPeer?.TrySendPooledTransactionRequest(action.Hash);
            if (!action.RetryMask.IsEmpty)
            {
                TryRequestCells(action.Hash, action.RetryMask, removedPeerId);
            }

            if (action.Submit)
            {
                TrySubmit(action.Hash, action.State);
            }
        }
    }

    /// <inheritdoc/>
    public bool RecordAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask announcementMask)
    {
        if (announcementMask.IsEmpty || !IsActivePeer(peer))
        {
            return false;
        }

        if (HasFullLocalBlobTransaction(hash))
        {
            return false;
        }

        TrackedSparseBlobTx state = GetOrAdd(hash, out bool added);
        bool accepted = false;
        lock (state.Lock)
        {
            if (IsCurrentState(hash, state)
                && IsActivePeer(peer)
                && !IsCellValidationQuarantined(state)
                && !state.AmbiguousFailureSources.Contains(peer.Id))
            {
                if (state.Announcements.TryGetValue(peer.Id, out BlobCellMask previousMask))
                {
                    if (previousMask != announcementMask)
                    {
                        state.Announcements[peer.Id] = announcementMask;
                        Touch(state, _timestamper.UtcNowOffset);
                    }

                    accepted = true;
                }
                else if (TryReserveAnnouncement(peer, hash.ValueHash256, state.Submitted))
                {
                    state.Announcements.Add(peer.Id, announcementMask);
                    Touch(state, _timestamper.UtcNowOffset);
                    accepted = true;
                }
            }
        }

        accepted = CompleteRecord(hash, state, added, accepted);
        if (!accepted)
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public BlobCellMask GetRequestMask(Hash256 hash, BlobCellMask announcementMask, int providerProbabilityPercent)
    {
        if (announcementMask.IsEmpty)
        {
            return BlobCellMask.Empty;
        }

        BlobCellMask custodyMask = _blobCustodyTracker.CurrentMask;
        if (HasSupernodeCustody(custodyMask))
        {
            return announcementMask;
        }

        int threshold = Math.Clamp(providerProbabilityPercent, MinProviderProbabilityPercent, MaxProviderProbabilityPercent);
        if (announcementMask.IsFull && ShouldFetchFull(hash, threshold))
        {
            return BlobCellMask.Full;
        }

        BlobCellMask requestMask = custodyMask & announcementMask;
        return announcementMask.IsFull
            ? requestMask | SelectExtraCellMask(hash, announcementMask, requestMask)
            : requestMask;
    }

    private bool ShouldFetchFull(Hash256 hash, int providerProbabilityPercent)
    {
        if (providerProbabilityPercent >= MaxProviderProbabilityPercent)
        {
            return true;
        }

        Hash256 sampleHash = ComputeSamplingHash(hash, domain: 0);
        ushort sample = BinaryPrimitives.ReadUInt16BigEndian(sampleHash.Bytes[..sizeof(ushort)]);
        return sample % MaxProviderProbabilityPercent < providerProbabilityPercent;
    }

    private BlobCellMask SelectExtraCellMask(Hash256 hash, BlobCellMask announcementMask, BlobCellMask alreadyRequested)
    {
        UInt128 candidates = announcementMask.Value & ~alreadyRequested.Value;
        if (candidates == UInt128.Zero)
        {
            return BlobCellMask.Empty;
        }

        int candidateCount = new BlobCellMask(candidates).Count;

        Hash256 sampleHash = ComputeSamplingHash(hash, domain: 1);
        uint sample = BinaryPrimitives.ReadUInt32BigEndian(sampleHash.Bytes[..sizeof(uint)]);
        int selectedCandidate = (int)(sample % (uint)candidateCount);
        for (int i = 0; i < BlobCellMask.CellCount; i++)
        {
            if ((candidates & (UInt128.One << i)) != 0 && selectedCandidate-- == 0)
            {
                return new BlobCellMask(UInt128.One << i);
            }
        }

        return BlobCellMask.Empty;
    }

    private Hash256 ComputeSamplingHash(Hash256 hash, byte domain)
    {
        const int domainLength = sizeof(byte);
        const int domainOffset = SamplingSecretLength;
        const int hashOffset = domainOffset + domainLength;
        Span<byte> input = stackalloc byte[SamplingSecretLength + domainLength + Hash256.Size];
        _samplingSecret.CopyTo(input);
        input[domainOffset] = domain;
        hash.Bytes.CopyTo(input[hashOffset..]);
        return Keccak.Compute(input);
    }

    /// <inheritdoc/>
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
        long firstReservationRevision = 0;
        List<(ISparseBlobPoolPeer Peer, BlobCellMask Mask, long Revision)>? morePeers = null;
        HashSet<PublicKey>? capacityLimitedPeers = null;
        bool placedAll;
        DateTimeOffset now = _timestamper.UtcNowOffset;
        lock (state.Lock)
        {
            if (!IsCurrentState(hash, state))
            {
                return false;
            }

            if (IsCellValidationQuarantined(state))
            {
                return false;
            }

            if (now < state.InFlightUntil)
            {
                requestMask = requestMask.Except(state.InFlightMask);
            }
            else
            {
                ExpireInFlight(hash.ValueHash256, state);
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
                && SelectPeer(state, requestMask, lastResortPeerId, capacityLimitedPeers, out BlobCellMask sendMask) is { } peer)
            {
                // The deadline is armed only on the empty->non-empty transition; extending it on
                // every reservation would let a dead request's bits stay in flight indefinitely.
                if (state.InFlightMask.IsEmpty)
                {
                    state.InFlightUntil = now + _cellRequestTimeout;
                }

                BlobCellMask existingPeerMask = state.InFlightByPeer.GetValueOrDefault(peer.Id);
                BlobCellMask newlyReservedMask = sendMask.Except(existingPeerMask);
                InFlightReservationResult reservationResult = newlyReservedMask.IsEmpty
                    ? InFlightReservationResult.Reserved
                    : TryReserveInFlight(
                        peer,
                        hash.ValueHash256,
                        newlyReservedMask.Count,
                        addHashReference: existingPeerMask.IsEmpty);
                if (reservationResult is InFlightReservationResult.InactivePeer)
                {
                    continue;
                }

                if (reservationResult is InFlightReservationResult.GlobalCapacityExceeded)
                {
                    break;
                }

                if (reservationResult is InFlightReservationResult.PeerCapacityExceeded)
                {
                    (capacityLimitedPeers ??= []).Add(peer.Id);
                    continue;
                }

                long reservationRevision = existingPeerMask.IsEmpty
                    ? ++state.NextInFlightRevision
                    : state.InFlightRevisionByPeer[peer.Id];
                state.InFlightRevisionByPeer[peer.Id] = reservationRevision;
                state.InFlightByPeer[peer.Id] = existingPeerMask | sendMask;
                state.InFlightMask |= newlyReservedMask;
                requestMask = requestMask.Except(sendMask);
                if (firstPeer is null)
                {
                    firstPeer = peer;
                    firstMask = sendMask;
                    firstReservationRevision = reservationRevision;
                }
                else
                {
                    (morePeers ??= []).Add((peer, sendMask, reservationRevision));
                }
            }

            if (firstPeer is null)
            {
                return false;
            }

            placedAll = requestMask.IsEmpty;
            Touch(state, now);
        }

        placedAll &= TrySendReserved(state, hash, firstPeer, firstMask, firstReservationRevision);
        if (morePeers is not null)
        {
            foreach ((ISparseBlobPoolPeer peer, BlobCellMask sendMask, long reservationRevision) in morePeers)
            {
                placedAll &= TrySendReserved(state, hash, peer, sendMask, reservationRevision);
            }
        }

        // A false return makes the caller park the whole mask; the in-flight and local-pool
        // subtraction above deduplicates the already-placed part on retry.
        return placedAll;
    }

    private bool TrySendReserved(
        TrackedSparseBlobTx state,
        Hash256 hash,
        ISparseBlobPoolPeer peer,
        BlobCellMask sendMask,
        long reservationRevision)
    {
        if (peer.TrySendGetCells(hash, sendMask))
        {
            return true;
        }

        lock (state.Lock)
        {
            if (!state.InFlightRevisionByPeer.TryGetValue(peer.Id, out long currentRevision)
                || currentRevision != reservationRevision
                || !state.InFlightByPeer.TryGetValue(peer.Id, out BlobCellMask peerMask))
            {
                return false;
            }

            BlobCellMask rollbackMask = peerMask & sendMask;
            BlobCellMask remaining = peerMask.Except(rollbackMask);
            state.InFlightMask = state.InFlightMask.Except(rollbackMask);
            if (remaining.IsEmpty)
            {
                state.InFlightByPeer.Remove(peer.Id);
                state.InFlightRevisionByPeer.Remove(peer.Id);
            }
            else
            {
                state.InFlightByPeer[peer.Id] = remaining;
            }

            ReleaseInFlight(
                peer.Id,
                hash.ValueHash256,
                rollbackMask.Count,
                releaseHashReference: remaining.IsEmpty);
        }

        return false;
    }

    /// <inheritdoc/>
    public void OnCellsRequestCompleted(Hash256 hash, BlobCellMask completedMask, ISparseBlobPoolPeer peer)
    {
        if (completedMask.IsEmpty
            || !IsActivePeer(peer)
            || !_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return;
        }

        lock (state.Lock)
        {
            if (!IsActivePeer(peer))
            {
                return;
            }

            PublicKey peerId = peer.Id;
            if (state.InFlightByPeer.TryGetValue(peerId, out BlobCellMask peerMask))
            {
                BlobCellMask completedPeerMask = completedMask & peerMask;
                state.InFlightMask = state.InFlightMask.Except(completedPeerMask);
                BlobCellMask remaining = peerMask.Except(completedPeerMask);
                int releasedWork = peerMask.Count - remaining.Count;
                if (remaining.IsEmpty)
                {
                    state.InFlightByPeer.Remove(peerId);
                    state.InFlightRevisionByPeer.Remove(peerId);
                }
                else
                {
                    state.InFlightByPeer[peerId] = remaining;
                }

                ReleaseInFlight(peerId, hash.ValueHash256, releasedWork, releaseHashReference: remaining.IsEmpty);
            }
        }
    }

    /// <inheritdoc/>
    public BlobCellMask RemoveAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash)
    {
        if (IsActivePeer(peer)
            && _transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            lock (state.Lock)
            {
                if (!IsActivePeer(peer))
                {
                    return BlobCellMask.Empty;
                }

                PublicKey peerId = peer.Id;
                if (state.Announcements.Remove(peerId, out BlobCellMask announcedMask))
                {
                    ReleaseAnnouncement(peerId, hash.ValueHash256, state.Submitted);
                    return announcedMask;
                }
            }
        }

        return BlobCellMask.Empty;
    }

    private bool RequestCellsForCustodyChange(
        BlobCellMask newCustodyMask,
        bool requestAllAnnouncedCells,
        CancellationToken cancellationToken,
        out int requests)
    {
        BlobCellMask requestMaskTemplate;
        lock (_custodyLock)
        {
            requestMaskTemplate = requestAllAnnouncedCells
                ? BlobCellMask.Full
                : new BlobCellMask(newCustodyMask.Value & ~_custodyMask.Value);
        }

        requests = 0;
        if (requestMaskTemplate.IsEmpty)
        {
            return true;
        }

        foreach (KeyValuePair<ValueHash256, TrackedSparseBlobTx> entry in _transactions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            Hash256 hash = entry.Key.ToHash256();
            BlobCellMask requestMask = requestAllAnnouncedCells
                ? GetMissingAnnouncedMask(hash, entry.Value)
                : GetMissingLocalMask(hash, requestMaskTemplate);
            if (!requestAllAnnouncedCells && !requestMask.IsEmpty)
            {
                requestMask = AddProviderExtraCell(hash, entry.Value, requestMask);
            }

            if (requestMask.IsEmpty)
            {
                continue;
            }

            if (TryRequestCells(hash, requestMask, NoLastResortPeer))
            {
                requests++;
            }
        }

        return !cancellationToken.IsCancellationRequested;
    }

    private BlobCellMask AddProviderExtraCell(Hash256 hash, TrackedSparseBlobTx state, BlobCellMask requestMask)
    {
        BlobCellMask excludedMask = requestMask;
        if (_txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask localMask))
        {
            excludedMask |= localMask;
        }

        bool hasFullProvider = false;
        lock (state.Lock)
        {
            if (!IsCurrentState(hash, state))
            {
                return requestMask;
            }

            excludedMask |= state.InFlightMask;
            foreach (KeyValuePair<PublicKey, BlobCellMask> announcement in state.Announcements)
            {
                if (announcement.Value.IsFull && IsActivePeer(announcement.Key))
                {
                    hasFullProvider = true;
                    break;
                }
            }
        }

        return hasFullProvider
            ? requestMask | SelectExtraCellMask(hash, BlobCellMask.Full, excludedMask)
            : requestMask;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

        if (!IsActivePeer(peer))
        {
            return null;
        }

        AcceptTxResult validationResult = _txPool.ValidateTxForBlobSampling(transaction);
        if (!validationResult)
        {
            return validationResult;
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

        TrackedSparseBlobTx state = GetOrAdd(hash, out bool added);
        bool accepted = false;
        lock (state.Lock)
        {
            if (IsCurrentState(hash, state)
                && IsActivePeer(peer)
                && !IsCellValidationQuarantined(state)
                && !state.Submitted
                && !state.AmbiguousFailureSources.Contains(peer.Id))
            {
                accepted = state.Transaction is not null;
                if (!accepted)
                {
                    int transactionBytes = transaction.GetLength();
                    if (TryReserveTransaction(peer, hash.ValueHash256, transactionBytes))
                    {
                        state.Transaction = transaction;
                        state.TransactionPeer = peer;
                        state.TransactionBytes = transactionBytes;
                        accepted = true;
                    }
                }

                if (accepted
                    && attachedCells is not null
                    && !state.Submitting
                    && !state.ApplyingRecordedCells)
                {
                    PendingCellsBuffer addedCells = new(attachedCellMask, attachedCells, peer.Id);
                    PendingCellsBuffer replacement = state.Cells is { } existingCells
                        && existingCells.TryMerge(addedCells, out PendingCellsBuffer merged)
                            ? merged
                            : addedCells;
                    if (TryReplaceCellsAccounting(hash.ValueHash256, state.Cells, replacement, peer))
                    {
                        state.Cells = replacement;
                        state.CellsExpiresAt = _timestamper.UtcNowOffset + EarlyCellsTtl;
                    }
                }

                if (accepted)
                {
                    Touch(state, _timestamper.UtcNowOffset);
                }
            }
        }

        accepted = CompleteRecord(hash, state, added, accepted);
        if (!accepted)
        {
            return null;
        }

        return TrySubmit(hash, state);
    }

    /// <inheritdoc/>
    public bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells)
    {
        if (cellMask.IsEmpty
            || cells.Length == 0
            || cells.Length % cellMask.Count != 0
            || !IsActivePeer(peer))
        {
            return false;
        }

        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { Length: CkzgLib.Ckzg.BytesPerCell })
            {
                return false;
            }
        }

        PendingCellsBuffer addedCells = new(cellMask, cells, peer.Id);
        if (addedCells.ByteLength > MaxEarlyCellsPerTransactionBytes)
        {
            return false;
        }

        TrackedSparseBlobTx state = GetOrAdd(hash, out bool added);
        bool accepted = false;
        lock (state.Lock)
        {
            if (IsCurrentState(hash, state)
                && IsActivePeer(peer)
                && !IsCellValidationQuarantined(state)
                && !state.Submitting
                && !state.ApplyingRecordedCells
                && !state.AmbiguousFailureSources.Contains(peer.Id))
            {
                if (state.Cells is { } existingCells
                    && (existingCells.CellMask & cellMask) == cellMask)
                {
                    accepted = true;
                }
                else
                {
                    PendingCellsBuffer replacement = state.Cells is { } current
                        && current.TryMerge(addedCells, out PendingCellsBuffer merged)
                            ? merged
                            : addedCells;
                    if (TryReplaceCellsAccounting(hash.ValueHash256, state.Cells, replacement, peer))
                    {
                        state.Cells = replacement;
                        DateTimeOffset now = _timestamper.UtcNowOffset;
                        state.CellsExpiresAt = now + EarlyCellsTtl;
                        Touch(state, now);
                        accepted = true;
                    }
                }
            }
        }

        accepted = CompleteRecord(hash, state, added, accepted);
        if (!accepted)
        {
            return false;
        }

        TrySubmit(hash, state);
        return true;
    }

    /// <inheritdoc/>
    public bool TryApplyRecordedCells(Hash256 hash)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return false;
        }

        while (true)
        {
            PendingCellsBuffer pending;
            lock (state.Lock)
            {
                if (!IsCurrentState(hash, state)
                    || state.Submitting
                    || state.ApplyingRecordedCells
                    || state.Cells is not { } current)
                {
                    return false;
                }

                state.ApplyingRecordedCells = true;
                pending = current;
            }

            BlobCellMergeResult mergeResult;
            PublicKey? invalidCellSource;
            try
            {
                mergeResult = MergeRecordedCells(hash, pending, out invalidCellSource);
            }
            catch
            {
                lock (state.Lock)
                {
                    state.ApplyingRecordedCells = false;
                }

                throw;
            }

            bool invalidProofTuple = mergeResult == BlobCellMergeResult.InvalidCells;
            bool retrySubmission = mergeResult == BlobCellMergeResult.TransactionUnavailable;
            bool retry;
            bool quarantineCellValidation = false;
            lock (state.Lock)
            {
                state.ApplyingRecordedCells = false;
                if (!IsCurrentState(hash, state))
                {
                    return false;
                }

                if (invalidProofTuple)
                {
                    quarantineCellValidation = RecordAmbiguousFailure(state, invalidCellSource);
                    ReleaseCells(state);
                    state.Cells = null;
                    retry = false;
                }
                else if (mergeResult != BlobCellMergeResult.Accepted)
                {
                    retry = false;
                }
                else
                {
                    state.AmbiguousCellValidationFailures = 0;
                    retry = state.Cells is { } latest && !ReferenceEquals(latest.Cells, pending.Cells);
                    if (!retry)
                    {
                        ReleaseCells(state);
                        state.Cells = null;
                    }
                }
            }

            if (retrySubmission)
            {
                TrySubmit(hash, state);
                return false;
            }

            if (invalidProofTuple)
            {
                if (quarantineCellValidation)
                {
                    return false;
                }

                TryRequestCells(hash, pending.CellMask, invalidCellSource ?? pending.LastSourcePeerId);
                return false;
            }

            if (!retry)
            {
                if (HasFullLocalBlobTransaction(hash))
                {
                    TryRemoveState(hash, state);
                }

                return true;
            }
        }
    }

    private BlobCellMergeResult MergeRecordedCells(
        Hash256 hash,
        in PendingCellsBuffer pending,
        out PublicKey? invalidCellSource)
    {
        invalidCellSource = null;
        int cellsPerBlob = pending.CellMask.Count;
        if (cellsPerBlob == 0
            || pending.Sources.Length == 0
            || pending.Cells.Length % cellsPerBlob != 0)
        {
            return BlobCellMergeResult.InvalidCells;
        }

        int blobCount = pending.Cells.Length / cellsPerBlob;
        for (int i = 0; i < pending.Sources.Length; i++)
        {
            PendingCellsSource source = pending.Sources[i];
            byte[][] sourceCells = source.CellMask == pending.CellMask
                ? pending.Cells
                : BlobCellsHelper.SelectFlattenedCells(
                    pending.Cells,
                    pending.CellMask,
                    source.CellMask,
                    blobCount);
            BlobCellMergeResult result = _txPool.MergeBlobCells(hash, source.CellMask, sourceCells);
            if (result != BlobCellMergeResult.InvalidCells)
            {
                if (result != BlobCellMergeResult.Accepted)
                {
                    return result;
                }

                continue;
            }

            invalidCellSource = source.PeerId;
            return result;
        }

        return BlobCellMergeResult.Accepted;
    }

    /// <inheritdoc/>
    public bool TryAcquireCellServeWork(int work)
    {
        if (work <= 0)
        {
            return false;
        }

        lock (_cellServeLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_cellServeConcurrency.Wait(0))
            {
                return false;
            }

            DateTimeOffset now = _timestamper.UtcNowOffset;
            double elapsedSeconds = Math.Max(0, (now - _cellServeTokensUpdatedAt).TotalSeconds);
            _cellServeTokens = Math.Min(
                GlobalCellServeTokenCapacity,
                _cellServeTokens + elapsedSeconds * GlobalCellServeTokensPerSecond);
            _cellServeTokensUpdatedAt = now;
            if (_cellServeTokens < work)
            {
                _cellServeConcurrency.Release();
                return false;
            }

            _cellServeTokens -= work;
            _activeCellServeOperations++;
            return true;
        }
    }

    /// <inheritdoc/>
    public void ReleaseCellServeWork()
    {
        lock (_cellServeLock)
        {
            _cellServeConcurrency.Release();
            _activeCellServeOperations--;
            if (_activeCellServeOperations == 0 && Volatile.Read(ref _disposed) != 0)
            {
                DisposeCellServeConcurrency();
            }
        }
    }

    /// <inheritdoc/>
    public void RefundCellServeWork(int work)
    {
        if (work <= 0)
        {
            return;
        }

        lock (_cellServeLock)
        {
            _cellServeTokens = Math.Min(GlobalCellServeTokenCapacity, _cellServeTokens + work);
        }
    }

    private void DisposeCellServeConcurrency()
    {
        if (_cellServeConcurrencyDisposed)
        {
            return;
        }

        _cellServeConcurrencyDisposed = true;
        _cellServeConcurrency.Dispose();
    }

    /// <inheritdoc/>
    public void Clear(Hash256 hash)
    {
        if (_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            TryRemoveState(hash, state);
        }
    }

    private TrackedSparseBlobTx GetOrAdd(Hash256 hash, out bool added)
    {
        ValueHash256 key = hash.ValueHash256;
        while (true)
        {
            if (_transactions.TryGetValue(key, out TrackedSparseBlobTx? existing))
            {
                added = false;
                return existing;
            }

            DateTimeOffset now = _timestamper.UtcNowOffset;
            long revision = Interlocked.Increment(ref _trackedStateRevision);
            TrackedSparseBlobTx state = new(key, now, now + GetAdmissionDelay(hash), revision);
            if (_transactions.TryAdd(key, state))
            {
                Interlocked.Increment(ref _trackedTransactionCount);
                added = true;
                return state;
            }
        }
    }

    private bool CompleteRecord(Hash256 hash, TrackedSparseBlobTx state, bool added, bool accepted)
    {
        if (accepted)
        {
            bool enqueue = false;
            bool current;
            lock (state.Lock)
            {
                current = IsCurrentState(hash, state);
                if (current && !state.IsQueued)
                {
                    state.IsQueued = true;
                    enqueue = true;
                }
            }

            if (!current)
            {
                return false;
            }

            if (enqueue)
            {
                lock (_transactionOrderLock)
                {
                    _transactionOrder.Enqueue(new TrackedStateKey(hash.ValueHash256, state.Revision));
                    _transactionOrderCount++;
                }

                TrimTrackedTransactions();
            }

            return true;
        }

        if (!added)
        {
            return false;
        }

        lock (state.Lock)
        {
            if (state.Announcements.Count == 0
                && state.Transaction is null
                && state.Cells is null
                && state.InFlightByPeer.Count == 0
                && !state.Submitted
                && !state.Submitting)
            {
                TryRemoveTrackedState(hash.ValueHash256, state);
            }
        }

        return false;
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
    private ISparseBlobPoolPeer? SelectPeer(
        TrackedSparseBlobTx state,
        BlobCellMask requestMask,
        PublicKey lastResortPeerId,
        HashSet<PublicKey>? excludedPeers,
        out BlobCellMask sendMask)
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
                || excludedPeers?.Contains(announcement.Key) == true
                || state.AmbiguousFailureSources.Contains(announcement.Key)
                || state.InFlightByPeer.ContainsKey(announcement.Key)
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
        bool removeBeforeSubmit;
        lock (state.Lock)
        {
            if (!IsCurrentState(hash, state))
            {
                return null;
            }

            transaction = state.Transaction;
            transactionPeer = state.TransactionPeer;
            cells = state.Cells;
            notBefore = state.NotBefore;
            removeBeforeSubmit = state.RemovalRequested;
            if (state.Submitted || state.Submitting || state.ApplyingRecordedCells)
            {
                return null;
            }

            if (removeBeforeSubmit || transaction is null || cells is null)
            {
                state.Submitting = false;
            }
            else
            {
                DateTimeOffset now = _timestamper.UtcNowOffset;
                if (now < notBefore)
                {
                    state.AdmissionDueAt = notBefore;
                    return null;
                }

                state.Submitting = true;
            }
        }

        if (removeBeforeSubmit)
        {
            TryRemoveState(hash, state);
            return null;
        }

        if (transaction is null || cells is null)
        {
            return null;
        }

        ShardBlobNetworkWrapper originalWrapper = (ShardBlobNetworkWrapper)transaction.NetworkWrapper!;
        if (!TryAttachCells(hash, transaction, cells.Value, out string? error, out SparseBlobAttachFailureSource failureSource))
        {
            return HandleAttachFailure(hash, state, transactionPeer, cells.Value, error, failureSource);
        }

        if (CancelSubmissionIfRequested(hash, state, transaction, originalWrapper))
        {
            return null;
        }

        AcceptTxResult result;
        try
        {
            result = SubmitTransaction(transactionPeer, transaction);
            if (result == AcceptTxResult.AlreadyKnown
                && !_txPool.TryGetPendingBlobTransaction(hash, out _))
            {
                if (CancelSubmissionIfRequested(hash, state, transaction, originalWrapper))
                {
                    return null;
                }

                _txPool.ForgetRejectedBlobTransaction(hash);
                result = SubmitTransaction(transactionPeer, transaction);
            }
        }
        catch
        {
            transaction.NetworkWrapper = originalWrapper;
            transaction.ClearLengthCache();
            bool remove;
            lock (state.Lock)
            {
                state.Submitting = false;
                remove = state.RemovalRequested;
            }

            if (remove)
            {
                TryRemoveState(hash, state);
            }

            throw;
        }

        if (result == AcceptTxResult.AlreadyKnown
            && !_txPool.TryGetPendingBlobTransaction(hash, out _))
        {
            _txPool.ForgetRejectedBlobTransaction(hash);
            transaction.NetworkWrapper = originalWrapper;
            transaction.ClearLengthCache();
            bool remove;
            lock (state.Lock)
            {
                state.Submitting = false;
                remove = state.RemovalRequested;
                if (!remove)
                {
                    state.AdmissionDueAt = _timestamper.UtcNowOffset + MaintenanceInterval;
                }
            }

            if (remove)
            {
                TryRemoveState(hash, state);
            }

            return null;
        }

        if (result == AcceptTxResult.InvalidBlobProofs)
        {
            transaction.NetworkWrapper = originalWrapper;
            transaction.ClearLengthCache();
            return HandleAttachFailure(
                hash,
                state,
                transactionPeer,
                cells.Value,
                $"Invalid sparse blob cell proofs for {hash}.",
                SparseBlobAttachFailureSource.Ambiguous);
        }
        else if (result == AcceptTxResult.Invalid)
        {
            lock (state.Lock)
            {
                state.Submitting = false;
            }

            TryRemoveState(hash, state);
            return null;
        }
        else if (result == AcceptTxResult.Accepted || result == AcceptTxResult.AlreadyKnown)
        {
            bool remove;
            lock (state.Lock)
            {
                remove = state.RemovalRequested;
                if (remove)
                {
                    state.Submitting = false;
                }
                else
                {
                    state.Submitted = true;
                    state.Submitting = false;
                    RetainSubmittedAnnouncements(state);
                    ReleaseTransaction(state);
                    ReleaseCells(state);
                    state.Transaction = null;
                    state.TransactionPeer = null;
                    state.Cells = null;
                    state.AmbiguousValidationFailures = 0;
                    state.ExpiresAt = DateTimeOffset.MaxValue;
                }
            }

            if (remove)
            {
                _txPool.RemoveTransaction(hash);
                TryRemoveState(hash, state);
                return result;
            }

            if (HasFullLocalBlobTransaction(hash))
            {
                TryRemoveState(hash, state);
            }
        }
        else
        {
            lock (state.Lock)
            {
                state.Submitting = false;
            }

            TryRemoveState(hash, state);
        }

        return result;
    }

    private bool CancelSubmissionIfRequested(
        Hash256 hash,
        TrackedSparseBlobTx state,
        Transaction transaction,
        ShardBlobNetworkWrapper originalWrapper)
    {
        lock (state.Lock)
        {
            if (!state.RemovalRequested)
            {
                return false;
            }

            state.Submitting = false;
        }

        transaction.NetworkWrapper = originalWrapper;
        transaction.ClearLengthCache();
        TryRemoveState(hash, state);
        return true;
    }

    private AcceptTxResult? HandleAttachFailure(
        Hash256 hash,
        TrackedSparseBlobTx state,
        ISparseBlobPoolPeer? transactionPeer,
        PendingCellsBuffer cells,
        string? error,
        SparseBlobAttachFailureSource failureSource)
    {
        if (failureSource == SparseBlobAttachFailureSource.Cells && cells.Sources.Length > 1)
        {
            failureSource = SparseBlobAttachFailureSource.Ambiguous;
        }

        bool sameSourceFailure = failureSource == SparseBlobAttachFailureSource.Ambiguous
            && transactionPeer is not null
            && cells.IsFromSinglePeer(transactionPeer.Id);
        if (sameSourceFailure)
        {
            bool sameSourceRemovalRequested;
            lock (state.Lock)
            {
                state.Submitting = false;
                sameSourceRemovalRequested = state.RemovalRequested;
            }

            if (sameSourceRemovalRequested)
            {
                TryRemoveState(hash, state);
                return null;
            }

            _txPool.ForgetRejectedBlobTransaction(hash);
            transactionPeer!.DisconnectSparseBlobPeer(DisconnectReason.BreachOfProtocol, error ?? "invalid sparse blob cells");
            RemovePeer(transactionPeer);
            return null;
        }

        PublicKey retryFallbackPeerId = cells.LastSourcePeerId;
        ISparseBlobPoolPeer? transactionRetryPeer = null;
        ISparseBlobPoolPeer? peerToDisconnect = null;
        PublicKey? peerToRemove = null;
        string? disconnectDetails = null;
        bool removeState = false;
        bool removalRequested;
        lock (state.Lock)
        {
            removalRequested = state.RemovalRequested;
            if (removalRequested)
            {
                state.Submitting = false;
            }
            else if (failureSource == SparseBlobAttachFailureSource.Ambiguous)
            {
                state.AmbiguousValidationFailures++;
                removeState = state.AmbiguousValidationFailures >= MaxAmbiguousValidationFailures;
                transactionRetryPeer = SelectTransactionRetryPeer(cells, transactionPeer);
                ReleaseTransaction(state);
                ReleaseCells(state);
                state.Transaction = null;
                state.TransactionPeer = null;
                state.Cells = null;
            }
            else if (failureSource == SparseBlobAttachFailureSource.Transaction)
            {
                if (transactionPeer is not null)
                {
                    peerToDisconnect = transactionPeer;
                    peerToRemove = transactionPeer.Id;
                    disconnectDetails = error ?? "invalid sparse blob transaction";
                }

                transactionRetryPeer = SelectTransactionRetryPeer(cells, transactionPeer);
                ReleaseTransaction(state);
                state.Transaction = null;
                state.TransactionPeer = null;
            }
            else
            {
                _peers.TryGetValue(cells.LastSourcePeerId, out peerToDisconnect);
                peerToRemove = cells.LastSourcePeerId;
                disconnectDetails = error ?? "invalid sparse blob cells";
                ReleaseCells(state);
                state.Cells = null;
            }

            state.Submitting = false;
        }

        if (removalRequested)
        {
            TryRemoveState(hash, state);
            return null;
        }

        if (peerToRemove is not null)
        {
            peerToDisconnect?.DisconnectSparseBlobPeer(DisconnectReason.BreachOfProtocol, disconnectDetails!);
            if (peerToDisconnect is not null)
            {
                RemovePeer(peerToDisconnect);
            }
        }

        if (failureSource == SparseBlobAttachFailureSource.Ambiguous && !removeState)
        {
            _txPool.ForgetRejectedBlobTransaction(hash);
        }

        if (removeState)
        {
            TryRemoveState(hash, state);
            return null;
        }

        transactionRetryPeer?.TrySendPooledTransactionRequest(hash);
        TryRequestCells(hash, cells.CellMask, retryFallbackPeerId);
        return null;
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

    private bool RecordAmbiguousFailure(
        TrackedSparseBlobTx state,
        PublicKey? sourcePeerId)
    {
        bool hasNewSource = sourcePeerId is null;
        if (sourcePeerId is not null)
        {
            if (state.AmbiguousFailureSources.Add(sourcePeerId))
            {
                hasNewSource = true;
            }

            if (state.Announcements.Remove(sourcePeerId))
            {
                ReleaseAnnouncement(sourcePeerId, state.Hash, state.Submitted);
            }
        }

        if (hasNewSource)
        {
            state.AmbiguousCellValidationFailures++;
        }

        bool quarantined = state.AmbiguousCellValidationFailures >= MaxAmbiguousValidationFailures;
        if (quarantined)
        {
            state.CellValidationQuarantinedUntil = _timestamper.UtcNowOffset + AmbiguousCellValidationQuarantine;
        }

        return quarantined;
    }

    private bool IsCellValidationQuarantined(TrackedSparseBlobTx state)
    {
        if (state.AmbiguousCellValidationFailures < MaxAmbiguousValidationFailures)
        {
            return false;
        }

        if (_timestamper.UtcNowOffset < state.CellValidationQuarantinedUntil)
        {
            return true;
        }

        state.AmbiguousCellValidationFailures = 0;
        state.CellValidationQuarantinedUntil = default;
        state.AmbiguousFailureSources.Clear();
        return false;
    }

    private void OnMaintenanceElapsed(object? sender, EventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _maintenanceScheduled, 1, 0) != 0)
        {
            return;
        }

        if (!_backgroundTaskScheduler.TryScheduleTask(
            new ScheduledRegistryRequest(this),
            static (request, cancellationToken) => request.Registry.RunMaintenance(cancellationToken),
            timeout: ScheduledActionTimeout,
            source: nameof(SparseBlobPoolPeerRegistry)))
        {
            Interlocked.Exchange(ref _maintenanceScheduled, 0);
        }
    }

    private Task RunMaintenance(CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset now = _timestamper.UtcNowOffset;
            foreach (KeyValuePair<PublicKey, ISparseBlobPoolPeer> entry in _peers)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                entry.Value.MaintainSparseBlobState(now);
            }

            foreach (KeyValuePair<ValueHash256, TrackedSparseBlobTx> entry in _transactions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                MaintainTrackedState(entry.Key.ToHash256(), entry.Value, now);
            }

            TrySchedulePendingCustodyUpdate();
        }
        finally
        {
            Interlocked.Exchange(ref _maintenanceScheduled, 0);
        }

        return Task.CompletedTask;
    }

    private void MaintainTrackedState(Hash256 hash, TrackedSparseBlobTx state, DateTimeOffset now)
    {
        BlobCellMask retryMask = BlobCellMask.Empty;
        PublicKey retryFallbackPeer = NoLastResortPeer;
        bool submit = false;
        bool remove = false;
        lock (state.Lock)
        {
            if (!IsCurrentState(hash, state))
            {
                return;
            }

            if (state.Submitted && !_txPool.TryGetPendingBlobCellMask(hash, out _))
            {
                remove = true;
            }
            else if (now >= state.ExpiresAt)
            {
                remove = true;
            }
            else
            {
                if (state.Transaction is null
                    && state.Cells is not null
                    && now >= state.CellsExpiresAt)
                {
                    ReleaseCells(state);
                    state.Cells = null;
                }

                if (!state.InFlightMask.IsEmpty && now >= state.InFlightUntil)
                {
                    retryMask = state.InFlightMask;
                    retryFallbackPeer = ExpireInFlight(hash.ValueHash256, state);
                }

                if (state.AdmissionDueAt is { } admissionDueAt && now >= admissionDueAt)
                {
                    state.AdmissionDueAt = null;
                    submit = true;
                }
            }
        }

        if (remove)
        {
            TryRemoveState(hash, state);
            return;
        }

        if (!retryMask.IsEmpty)
        {
            TryRequestCells(hash, retryMask, retryFallbackPeer);
        }

        if (submit)
        {
            TrySubmit(hash, state);
        }
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
    {
        lock (state.Lock)
        {
            if (state.Submitting)
            {
                state.RemovalRequested = true;
                return false;
            }

            if (!TryRemoveTrackedState(hash.ValueHash256, state))
            {
                return false;
            }

            foreach (PublicKey peerId in state.Announcements.Keys)
            {
                ReleaseAnnouncement(peerId, hash.ValueHash256, state.Submitted);
            }

            foreach (KeyValuePair<PublicKey, BlobCellMask> inFlight in state.InFlightByPeer)
            {
                ReleaseInFlight(inFlight.Key, state.Hash, inFlight.Value.Count, releaseHashReference: true);
            }

            state.Announcements.Clear();
            state.InFlightByPeer.Clear();
            state.InFlightRevisionByPeer.Clear();
            state.InFlightMask = BlobCellMask.Empty;
            ReleaseTransaction(state);
            ReleaseCells(state);
            state.Transaction = null;
            state.TransactionPeer = null;
            state.Cells = null;
        }

        return true;
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

        byte[][] flattenedCells = availableMask == pending.CellMask
            ? pending.Cells
            : BlobCellsHelper.SelectFlattenedCells(pending.Cells, pending.CellMask, availableMask, blobCount);
        tx.NetworkWrapper = wrapper with { CellMask = availableMask, Cells = flattenedCells };
        tx.ClearLengthCache();
        return true;
    }

    private void TrimTrackedTransactions()
    {
        lock (_transactionOrderLock)
        {
            while (Volatile.Read(ref _trackedTransactionCount) > MaxTrackedTransactions
                && _transactionOrder.TryDequeue(out TrackedStateKey key))
            {
                _transactionOrderCount--;
                if (_transactions.TryGetValue(key.Hash, out TrackedSparseBlobTx? state)
                    && state.Revision == key.Revision)
                {
                    TryRemoveState(key.Hash.ToHash256(), state);
                }
            }

            if (_transactionOrderCount <= MaxTrackedTransactions * 2)
            {
                return;
            }

            _transactionOrder.Clear();
            _transactionOrderCount = 0;
            foreach (KeyValuePair<ValueHash256, TrackedSparseBlobTx> entry in _transactions)
            {
                _transactionOrder.Enqueue(new TrackedStateKey(entry.Key, entry.Value.Revision));
                _transactionOrderCount++;
            }
        }
    }

    private bool TryRemoveTrackedState(ValueHash256 hash, TrackedSparseBlobTx state)
    {
        bool removed = ((ICollection<KeyValuePair<ValueHash256, TrackedSparseBlobTx>>)_transactions)
            .Remove(new KeyValuePair<ValueHash256, TrackedSparseBlobTx>(hash, state));
        if (removed)
        {
            Interlocked.Decrement(ref _trackedTransactionCount);
        }

        return removed;
    }

    private bool IsCurrentState(Hash256 hash, TrackedSparseBlobTx state)
        => _transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? current)
            && ReferenceEquals(current, state);

    private void Touch(TrackedSparseBlobTx state, DateTimeOffset now)
    {
        if (state.Submitted)
        {
            return;
        }

        DateTimeOffset slidingExpiration = now + TrackedStateTtl;
        state.ExpiresAt = slidingExpiration < state.MaxExpiresAt ? slidingExpiration : state.MaxExpiresAt;
    }

    private bool TryReserveAnnouncement(ISparseBlobPoolPeer peer, ValueHash256 hash, bool retained)
    {
        lock (_accountingLock)
        {
            if (!IsActivePeer(peer))
            {
                return false;
            }

            PublicKey peerId = peer.Id;
            PeerUsage usage = GetPeerUsage(peerId);
            int count = retained ? usage.RetainedAnnouncements : usage.Announcements;
            int limit = retained ? MaxRetainedAnnouncementsPerPeer : MaxAnnouncementsPerPeer;
            if (count >= limit)
            {
                RemovePeerUsageIfEmpty(peerId, usage);
                return false;
            }

            if (retained)
            {
                usage.RetainedAnnouncements++;
            }
            else
            {
                usage.Announcements++;
            }

            usage.AnnouncedHashes[hash] = usage.AnnouncedHashes.GetValueOrDefault(hash) + 1;
            AddTrackedHashReference(usage, hash);
            return true;
        }
    }

    private void RetainSubmittedAnnouncements(TrackedSparseBlobTx state)
    {
        List<PublicKey>? droppedAnnouncements = null;
        lock (_accountingLock)
        {
            foreach (PublicKey peerId in state.Announcements.Keys)
            {
                if (!_peerUsage.TryGetValue(peerId, out PeerUsage? usage)
                    || usage.Announcements == 0)
                {
                    (droppedAnnouncements ??= []).Add(peerId);
                    continue;
                }

                usage.Announcements--;
                if (usage.RetainedAnnouncements < MaxRetainedAnnouncementsPerPeer)
                {
                    usage.RetainedAnnouncements++;
                }
                else
                {
                    ReleaseAnnouncementReference(usage, state.Hash);
                    (droppedAnnouncements ??= []).Add(peerId);
                }

                RemovePeerUsageIfEmpty(peerId, usage);
            }
        }

        if (droppedAnnouncements is not null)
        {
            for (int i = 0; i < droppedAnnouncements.Count; i++)
            {
                state.Announcements.Remove(droppedAnnouncements[i]);
            }
        }
    }

    private void ReleaseAnnouncement(PublicKey peerId, ValueHash256 hash, bool retained)
    {
        lock (_accountingLock)
        {
            if (!_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
            {
                return;
            }

            if (retained)
            {
                if (usage.RetainedAnnouncements == 0)
                {
                    return;
                }

                usage.RetainedAnnouncements--;
            }
            else
            {
                if (usage.Announcements == 0)
                {
                    return;
                }

                usage.Announcements--;
            }

            ReleaseAnnouncementReference(usage, hash);
            RemovePeerUsageIfEmpty(peerId, usage);
        }
    }

    private static void ReleaseAnnouncementReference(PeerUsage usage, ValueHash256 hash)
    {
        int references = usage.AnnouncedHashes.GetValueOrDefault(hash);
        if (references <= 1)
        {
            usage.AnnouncedHashes.Remove(hash);
        }
        else
        {
            usage.AnnouncedHashes[hash] = references - 1;
        }

        ReleaseTrackedHashReference(usage, hash);
    }

    private bool TryReserveTransaction(ISparseBlobPoolPeer peer, ValueHash256 hash, int byteLength)
    {
        lock (_accountingLock)
        {
            if (!IsActivePeer(peer))
            {
                return false;
            }

            PublicKey peerId = peer.Id;
            PeerUsage usage = GetPeerUsage(peerId);
            if (_trackedTransactionBytes + byteLength > MaxTrackedTransactionBytes
                || usage.TransactionBytes + byteLength > MaxTrackedTransactionBytesPerPeer)
            {
                RemovePeerUsageIfEmpty(peerId, usage);
                return false;
            }

            _trackedTransactionBytes += byteLength;
            usage.TransactionBytes += byteLength;
            AddTrackedHashReference(usage, hash);
            return true;
        }
    }

    private void ReleaseTransaction(TrackedSparseBlobTx state)
    {
        if (state.TransactionPeer is null || state.TransactionBytes == 0)
        {
            return;
        }

        lock (_accountingLock)
        {
            _trackedTransactionBytes -= state.TransactionBytes;
            if (_peerUsage.TryGetValue(state.TransactionPeer.Id, out PeerUsage? usage))
            {
                usage.TransactionBytes -= state.TransactionBytes;
                ReleaseTrackedHashReference(usage, state.Hash);
                RemovePeerUsageIfEmpty(state.TransactionPeer.Id, usage);
            }
        }

        state.TransactionBytes = 0;
    }

    private bool TryReplaceCellsAccounting(
        ValueHash256 hash,
        PendingCellsBuffer? existing,
        in PendingCellsBuffer replacement,
        ISparseBlobPoolPeer? requiredActivePeer = null)
    {
        long globalDelta = replacement.ByteLength - (existing?.ByteLength ?? 0);
        lock (_accountingLock)
        {
            if (requiredActivePeer is not null && !IsActivePeer(requiredActivePeer))
            {
                return false;
            }

            if (_earlyCellsBytes + globalDelta > MaxEarlyCellsBytes)
            {
                return false;
            }

            HashSet<PublicKey> sourcePeers = [];
            if (existing is { } current)
            {
                for (int i = 0; i < current.Sources.Length; i++)
                {
                    sourcePeers.Add(current.Sources[i].PeerId);
                }
            }

            for (int i = 0; i < replacement.Sources.Length; i++)
            {
                sourcePeers.Add(replacement.Sources[i].PeerId);
            }

            foreach (PublicKey peerId in sourcePeers)
            {
                long oldBytes = existing?.GetByteLength(peerId) ?? 0;
                long newBytes = replacement.GetByteLength(peerId);
                PeerUsage usage = GetPeerUsage(peerId);
                if (usage.EarlyCellBytes + newBytes - oldBytes > MaxEarlyCellsBytesPerPeer)
                {
                    foreach (PublicKey reservedPeerId in sourcePeers)
                    {
                        if (_peerUsage.TryGetValue(reservedPeerId, out PeerUsage? reservedUsage))
                        {
                            RemovePeerUsageIfEmpty(reservedPeerId, reservedUsage);
                        }
                    }

                    return false;
                }
            }

            _earlyCellsBytes += globalDelta;
            foreach (PublicKey peerId in sourcePeers)
            {
                long oldBytes = existing?.GetByteLength(peerId) ?? 0;
                long newBytes = replacement.GetByteLength(peerId);
                PeerUsage usage = GetPeerUsage(peerId);
                usage.EarlyCellBytes += newBytes - oldBytes;
                if (oldBytes == 0 && newBytes != 0)
                {
                    AddTrackedHashReference(usage, hash);
                }
                else if (oldBytes != 0 && newBytes == 0)
                {
                    ReleaseTrackedHashReference(usage, hash);
                }

                RemovePeerUsageIfEmpty(peerId, usage);
            }

            return true;
        }
    }

    private void ReleaseCells(TrackedSparseBlobTx state)
    {
        if (state.Cells is not { } cells)
        {
            return;
        }

        lock (_accountingLock)
        {
            _earlyCellsBytes -= cells.ByteLength;
            for (int i = 0; i < cells.Sources.Length; i++)
            {
                PublicKey peerId = cells.Sources[i].PeerId;
                if (_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
                {
                    usage.EarlyCellBytes -= cells.GetByteLength(peerId);
                    ReleaseTrackedHashReference(usage, state.Hash);
                    RemovePeerUsageIfEmpty(peerId, usage);
                }
            }
        }
    }

    private InFlightReservationResult TryReserveInFlight(ISparseBlobPoolPeer peer, ValueHash256 hash, int work, bool addHashReference)
    {
        lock (_accountingLock)
        {
            if (!IsActivePeer(peer))
            {
                return InFlightReservationResult.InactivePeer;
            }

            PublicKey peerId = peer.Id;
            PeerUsage usage = GetPeerUsage(peerId);
            if (_inFlightCellWork + work > MaxInFlightCellWork)
            {
                RemovePeerUsageIfEmpty(peerId, usage);
                return InFlightReservationResult.GlobalCapacityExceeded;
            }

            if (usage.InFlightCellWork + work > MaxInFlightCellWorkPerPeer)
            {
                RemovePeerUsageIfEmpty(peerId, usage);
                return InFlightReservationResult.PeerCapacityExceeded;
            }

            _inFlightCellWork += work;
            usage.InFlightCellWork += work;
            if (addHashReference)
            {
                AddTrackedHashReference(usage, hash);
            }

            return InFlightReservationResult.Reserved;
        }
    }

    private void ReleaseInFlight(PublicKey peerId, ValueHash256 hash, int work, bool releaseHashReference)
    {
        if (work == 0 && !releaseHashReference)
        {
            return;
        }

        lock (_accountingLock)
        {
            _inFlightCellWork -= work;
            if (_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
            {
                usage.InFlightCellWork -= work;
                if (releaseHashReference)
                {
                    ReleaseTrackedHashReference(usage, hash);
                }

                RemovePeerUsageIfEmpty(peerId, usage);
            }
        }
    }

    private PublicKey ExpireInFlight(ValueHash256 hash, TrackedSparseBlobTx state)
    {
        PublicKey fallbackPeer = NoLastResortPeer;
        foreach (KeyValuePair<PublicKey, BlobCellMask> inFlight in state.InFlightByPeer)
        {
            fallbackPeer = inFlight.Key;
            ReleaseInFlight(inFlight.Key, hash, inFlight.Value.Count, releaseHashReference: true);
            if (state.Announcements.Remove(inFlight.Key))
            {
                ReleaseAnnouncement(inFlight.Key, hash, state.Submitted);
            }
        }

        state.InFlightByPeer.Clear();
        state.InFlightRevisionByPeer.Clear();
        state.InFlightMask = BlobCellMask.Empty;
        state.InFlightUntil = default;
        return fallbackPeer;
    }

    private ISparseBlobPoolPeer? SelectTransactionRetryPeer(PendingCellsBuffer cells, ISparseBlobPoolPeer? transactionPeer)
    {
        for (int i = 0; i < cells.Sources.Length; i++)
        {
            PublicKey sourcePeerId = cells.Sources[i].PeerId;
            if ((transactionPeer is null || sourcePeerId != transactionPeer.Id)
                && _peers.TryGetValue(sourcePeerId, out ISparseBlobPoolPeer? peer)
                && !peer.IsClosing)
            {
                return peer;
            }
        }

        return null;
    }

    private ISparseBlobPoolPeer? SelectTransactionRetryPeer(TrackedSparseBlobTx state)
    {
        foreach (PublicKey peerId in state.Announcements.Keys)
        {
            if (_peers.TryGetValue(peerId, out ISparseBlobPoolPeer? peer) && !peer.IsClosing)
            {
                return peer;
            }
        }

        return null;
    }

    private PeerUsage GetPeerUsage(PublicKey peerId)
    {
        if (!_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
        {
            usage = new PeerUsage();
            _peerUsage.Add(peerId, usage);
        }

        return usage;
    }

    private void RemovePeerUsageIfEmpty(PublicKey peerId, PeerUsage usage)
    {
        if (usage.Announcements == 0
            && usage.RetainedAnnouncements == 0
            && usage.AnnouncedHashes.Count == 0
            && usage.TrackedHashes.Count == 0
            && usage.EarlyCellBytes == 0
            && usage.TransactionBytes == 0
            && usage.InFlightCellWork == 0)
        {
            _peerUsage.Remove(peerId);
        }
    }

    private static void AddTrackedHashReference(PeerUsage usage, ValueHash256 hash)
        => usage.TrackedHashes[hash] = usage.TrackedHashes.GetValueOrDefault(hash) + 1;

    private static void ReleaseTrackedHashReference(PeerUsage usage, ValueHash256 hash)
    {
        int references = usage.TrackedHashes.GetValueOrDefault(hash);
        if (references <= 1)
        {
            usage.TrackedHashes.Remove(hash);
        }
        else
        {
            usage.TrackedHashes[hash] = references - 1;
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

    private enum InFlightReservationResult
    {
        Reserved,
        InactivePeer,
        GlobalCapacityExceeded,
        PeerCapacityExceeded
    }

    private readonly record struct TrackedStateKey(ValueHash256 Hash, long Revision);

    private readonly record struct ScheduledRegistryRequest(SparseBlobPoolPeerRegistry Registry);

    private readonly record struct PeerCleanupAction(
        Hash256 Hash,
        TrackedSparseBlobTx State,
        ISparseBlobPoolPeer? TransactionRetryPeer,
        BlobCellMask RetryMask,
        bool Submit);

    private sealed class PeerUsage
    {
        public Dictionary<ValueHash256, int> AnnouncedHashes { get; } = [];
        public Dictionary<ValueHash256, int> TrackedHashes { get; } = [];
        public int Announcements { get; set; }
        public int RetainedAnnouncements { get; set; }
        public long EarlyCellBytes { get; set; }
        public long TransactionBytes { get; set; }
        public int InFlightCellWork { get; set; }
    }

    private sealed class TrackedSparseBlobTx(ValueHash256 hash, DateTimeOffset createdAt, DateTimeOffset notBefore, long revision)
    {
        public Lock Lock { get; } = new();
        public ValueHash256 Hash { get; } = hash;
        public long Revision { get; } = revision;
        public DateTimeOffset NotBefore { get; } = notBefore;
        public DateTimeOffset MaxExpiresAt { get; } = createdAt + MaxTrackedStateLifetime;
        public DateTimeOffset ExpiresAt { get; set; } = createdAt + TrackedStateTtl;
        public DateTimeOffset CellsExpiresAt { get; set; }
        public DateTimeOffset CellValidationQuarantinedUntil { get; set; }
        public Dictionary<PublicKey, BlobCellMask> Announcements { get; } = [];
        public HashSet<PublicKey> AmbiguousFailureSources { get; } = [];
        public Dictionary<PublicKey, BlobCellMask> InFlightByPeer { get; } = [];
        public Dictionary<PublicKey, long> InFlightRevisionByPeer { get; } = [];
        public Transaction? Transaction { get; set; }
        public ISparseBlobPoolPeer? TransactionPeer { get; set; }
        public int TransactionBytes { get; set; }
        public PendingCellsBuffer? Cells { get; set; }
        public DateTimeOffset? AdmissionDueAt { get; set; }
        public int AmbiguousValidationFailures { get; set; }
        public int AmbiguousCellValidationFailures { get; set; }
        public bool IsQueued { get; set; }
        public bool Submitted { get; set; }
        public bool Submitting { get; set; }
        public bool RemovalRequested { get; set; }
        public bool ApplyingRecordedCells { get; set; }
        public BlobCellMask InFlightMask { get; set; }
        public DateTimeOffset InFlightUntil { get; set; }
        public long NextInFlightRevision { get; set; }
    }
}
