// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>
/// Coordinates bounded sparse blob state, peer selection, custody changes, and cell-serving capacity across eth/72 peers.
/// </summary>
public sealed class SparseBlobPoolPeerRegistry : ISparseBlobPoolPeerRegistry, IDisposable
{
    private static readonly (IDisposable Lease, Action<BlobCellMask> Complete) MissingCellReservation =
        (new MissingCellLease(), static _ => { }
    );

    internal const int SupernodeCustodyColumnThreshold = 64;
    internal const int MinSamplerFullProviderAnnouncements = 2;
    private const int MaxTrackedTransactions = 16384;
    private const int MaxTransactionRetryAttempts = 3;
    private const int TrackedStateQueueSlack = 2;
    private const int MaxTrimCandidates = 256;
    private const int MinProviderProbabilityPercent = 15;
    private const int MaxProviderProbabilityPercent = 100;
    internal const int MaxAnnouncementsPerPeer = 2048;
    private const int MaxRetainedAnnouncementsPerPeer = 2048;
    private const int MaxAmbiguousFailureSources = 64;
    private const int MaxBodyUnavailableSources = 64;
    private const int MaxRestorationTicketsPerPeer = 1024;
    private const int MaxEarlyCellsPerTransactionBytes = 2 * 1024 * 1024;
    private const long MaxEarlyCellsBytes = 64L * 1024 * 1024;
    private const long MaxEarlyCellsBytesPerPeer = 16L * 1024 * 1024;
    private const long MaxTrackedTransactionBytes = 64L * 1024 * 1024;
    private const long MaxTrackedTransactionBytesPerPeer = 16L * 1024 * 1024;
    private const int MaxInFlightCellWork = 8192;
    private const int MaxInFlightCellWorkPerPeer = 2048;
    private const int MaxSingleProviderCellWork = MaxInFlightCellWork / 2;
    private const int MinimumCellWorkShares = 8;
    internal const int GlobalCellServeTokenCapacity = 4096;
    internal const int GlobalCellServeTokensPerSecond = 768;
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
    private static readonly TimeSpan AmbiguousBodyRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EarlyCellsTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RestorationTicketTtl = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan TrackedStateTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxTrackedStateLifetime = TimeSpan.FromMinutes(5);
    private static readonly PublicKey NoLastResortPeer = new(new byte[PublicKey.LengthInBytes]);

    private readonly ITxPool _txPool;
    private readonly IBlobCustodyTracker _blobCustodyTracker;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<PublicKey, ISparseBlobPoolPeer> _peers = new();
    private readonly ConcurrentDictionary<PublicKey, long> _peerSessionSequences = new();
    private readonly ConditionalWeakTable<ISparseBlobPoolPeer, PacketReceiptTracker> _peerReceipts = [];
    private readonly ConcurrentDictionary<ValueHash256, TrackedSparseBlobTx> _transactions = new();
    private readonly byte[] _samplingSecret = RandomNumberGenerator.GetBytes(SamplingSecretLength);
    private readonly Lock _custodyLock = new();
    private readonly Lock _peerLifecycleLock = new();
    private readonly Lock _accountingLock = new();
    private readonly Lock _transactionOrderLock = new();
    private readonly ConcurrentQueue<TrackedStateKey> _transactionOrder = new();
    private readonly Lock _cellServeLock = new();
    private readonly SemaphoreSlim _cellServeConcurrency = new(MaxConcurrentCellServeOperations, MaxConcurrentCellServeOperations);
    private readonly Dictionary<PublicKey, PeerUsage> _peerUsage = [];
    private readonly TimeSpan _maxAdmissionDelay;
    private readonly TimeSpan _cellRequestTimeout;
    private readonly ITimestamper _timestamper;
    private readonly Nethermind.Core.Timers.ITimer _maintenanceTimer;
    private BlobCellMask _custodyMask;
    private long _trackedStateRevision;
    private long _peerSessionSequence;
    private long _earlyCellsBytes;
    private long _trackedTransactionBytes;
    private int _inFlightCellWork;
    private int _singleProviderCellWork;
    private int _trackedTransactionCount;
    private int _transactionOrderCount;
    internal int LastTrimStateCount { get; private set; }
    private int _peerCount;
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
        foreach (KeyValuePair<ValueHash256, TrackedSparseBlobTx> entry in _transactions)
        {
            TrackedSparseBlobTx state = entry.Value;
            lock (state.Lock)
            {
                foreach (AnnouncementRestoration restoration in state.RestorationTickets.Values)
                    ReleaseAnnouncementRestoration(state, restoration);
                state.RestorationTickets.Clear();
                ReleaseBodyRequestAttempt(state);
            }
        }
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
            new CustodyUpdateRequest(this),
            static (request, cancellationToken) => request.Registry.ApplyPendingCustodyChange(cancellationToken),
            timeout: ScheduledActionTimeout))
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
        if (peer.CellSourceToken is PacketReceiptTracker receipts)
        {
            _peerReceipts.GetValue(peer, _ => receipts);
        }

        List<PeerCleanupAction>? cleanupActions = null;
        ExceptionDispatchInfo? cleanupFailure = null;
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
                cleanupActions = CollectPeerCleanupActions(peer.Id, out cleanupFailure);
            }

            _peers[peer.Id] = peer;
            _peerSessionSequences[peer.Id] = ++_peerSessionSequence;
            Volatile.Write(ref _peerCount, _peers.Count);
        }

        ProcessPeerCleanupActions(cleanupActions, peer.Id, cleanupFailure);
    }

    /// <inheritdoc/>
    public void RemovePeer(ISparseBlobPoolPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        List<PeerCleanupAction>? cleanupActions = null;
        ExceptionDispatchInfo? cleanupFailure = null;
        lock (_peerLifecycleLock)
        {
            if (((ICollection<KeyValuePair<PublicKey, ISparseBlobPoolPeer>>)_peers)
                .Remove(new KeyValuePair<PublicKey, ISparseBlobPoolPeer>(peer.Id, peer)))
            {
                _peerSessionSequences.TryRemove(peer.Id, out _);
                Volatile.Write(ref _peerCount, _peers.Count);
                cleanupActions = CollectPeerCleanupActions(peer.Id, out cleanupFailure);
            }
        }

        ProcessPeerCleanupActions(cleanupActions, peer.Id, cleanupFailure);
    }

    private List<PeerCleanupAction>? CollectPeerCleanupActions(PublicKey peerId, out ExceptionDispatchInfo? failure)
    {
        failure = null;
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
                CancelAnnouncementRestoration(state, peerId);
                bool bodyAttemptRemoved = state.CurrentBodyAttemptPeer == peerId;
                if (state.CurrentBodyAttemptPeer == peerId)
                {
                    ReleaseBodyRequestAttempt(state);
                }
                if (state.Announcements.Remove(peerId))
                {
                    ReleaseAnnouncement(peerId, state);
                }

                if (state.InFlightByPeer.Remove(peerId, out BlobCellMask inFlightMask))
                {
                    state.InFlightRevisionByPeer.Remove(peerId);
                    state.InFlightMask = state.InFlightMask.Except(inFlightMask);
                    ReleaseInFlight(state, peerId, inFlightMask.Count, releaseHashReference: true);
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

                if (!state.Submitting && !state.ApplyingRecordedCells)
                {
                    state.Cells = RemoveCellSource(state.Cells);
                    state.AlternativeCells = RemoveCellSource(state.AlternativeCells);
                    if (state.Cells is null)
                    {
                        state.Cells = state.AlternativeCells;
                        state.AlternativeCells = null;
                    }
                }

                PendingCellsBuffer? RemoveCellSource(PendingCellsBuffer? buffer)
                {
                    if (buffer is not { } current || !current.TryRemoveSource(peerId, out PendingCellsBuffer? remaining)) return buffer;
                    retryMask |= current.CellMask.Except(remaining?.CellMask ?? BlobCellMask.Empty);
                    if (remaining is { } retained && TryReplaceCellsAccounting(trackedHash, current, retained)) return retained;
                    ReleaseCells(trackedHash, current);
                    return null;
                }

                if (transactionRemoved || (bodyAttemptRemoved && state.Transaction is null))
                {
                    transactionRetryPeer = PlanBodyRetry(state, out ExceptionDispatchInfo? retryFailure, state.Cells);
                    failure ??= retryFailure;
                }

                submit = state.Transaction is not null
                    && state.Cells is not null
                    && !state.Submitted
                    && !state.Submitting;
                remove = state.Announcements.Count == 0
                    && state.RestorationTickets.Count == 0
                    && state.CurrentBodyAttempt is null
                    && state.Transaction is null
                    && state.Cells is null
                    && state.InFlightByPeer.Count == 0
                    && !state.Submitted
                    && !state.Submitting;
            }

            if (remove)
            {
                TryRemoveState(trackedHash, state);
                continue;
            }

            if (transactionRetryPeer is not null || !retryMask.IsEmpty || submit)
            {
                (cleanupActions ??= []).Add(new PeerCleanupAction(
                    trackedHash.ToHash256(),
                    state,
                    transactionRetryPeer,
                    retryMask,
                    submit));
            }
        }

        return cleanupActions;
    }

    private void ProcessPeerCleanupActions(List<PeerCleanupAction>? cleanupActions, PublicKey removedPeerId,
        ExceptionDispatchInfo? failure)
    {
        if (cleanupActions is not null)
        {
            for (int i = 0; i < cleanupActions.Count; i++)
            {
                PeerCleanupAction action = cleanupActions[i];
                try { SendBodyRetry(action.Hash, action.State, action.TransactionRetryPeer); }
                catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
                if (!action.RetryMask.IsEmpty)
                {
                    try { TryRequestCells(action.Hash, action.RetryMask, removedPeerId); }
                    catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
                }
                if (action.Submit)
                {
                    try { TrySubmit(action.Hash, action.State); }
                    catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
                }
            }
        }
        failure?.Throw();
    }

    /// <inheritdoc/>
    public bool RecordAnnouncement(ISparseBlobPoolPeer peer, in ValueHash256 hash, BlobCellMask announcementMask)
    {
        if (announcementMask.IsEmpty || !IsActivePeer(peer))
        {
            return false;
        }

        if (HasFullLocalBlobTransaction(hash))
        {
            return false;
        }

        bool added = false;
        if (!_transactions.TryGetValue(hash, out TrackedSparseBlobTx? state))
        {
            lock (_accountingLock)
            {
                if (_peerUsage.TryGetValue(peer.Id, out PeerUsage? usage)
                    && usage.Announcements >= MaxAnnouncementsPerPeer
                    && !_transactions.TryGetValue(hash, out state))
                {
                    return false;
                }
            }

            state ??= GetOrAdd(hash, out added);
        }

        bool accepted = false;
        lock (state.Lock)
        {
            if (IsCurrentState(hash, state)
                && IsActivePeer(peer)
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
                else if (TryReserveAnnouncement(peer, state))
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
    public BlobCellMask GetRequestMask(in ValueHash256 hash, BlobCellMask announcementMask, int providerProbabilityPercent)
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
        // With fewer peers than providers a sampler waits for, sampling never starts.
        if (announcementMask.IsFull && (Volatile.Read(ref _peerCount) < MinSamplerFullProviderAnnouncements || ShouldFetchFull(hash, threshold)))
        {
            return BlobCellMask.Full;
        }

        BlobCellMask requestMask = custodyMask & announcementMask;
        return announcementMask.IsFull
            ? requestMask | SelectExtraCellMask(hash, announcementMask, requestMask)
            : requestMask;
    }

    private bool ShouldFetchFull(in ValueHash256 hash, int providerProbabilityPercent)
    {
        if (providerProbabilityPercent >= MaxProviderProbabilityPercent)
        {
            return true;
        }

        ValueHash256 sampleHash = ComputeSamplingHash(hash, domain: 0);
        ushort sample = BinaryPrimitives.ReadUInt16BigEndian(sampleHash.Bytes[..sizeof(ushort)]);
        return sample % MaxProviderProbabilityPercent < providerProbabilityPercent;
    }

    private BlobCellMask SelectExtraCellMask(in ValueHash256 hash, BlobCellMask announcementMask, BlobCellMask alreadyRequested)
    {
        UInt128 candidates = announcementMask.Value & ~alreadyRequested.Value;
        if (candidates == UInt128.Zero)
        {
            return BlobCellMask.Empty;
        }

        int candidateCount = new BlobCellMask(candidates).Count;

        ValueHash256 sampleHash = ComputeSamplingHash(hash, domain: 1);
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

    private ValueHash256 ComputeSamplingHash(in ValueHash256 hash, byte domain)
    {
        const int domainLength = sizeof(byte);
        const int domainOffset = SamplingSecretLength;
        const int hashOffset = domainOffset + domainLength;
        Span<byte> input = stackalloc byte[SamplingSecretLength + domainLength + Hash256.Size];
        _samplingSecret.CopyTo(input);
        input[domainOffset] = domain;
        hash.Bytes.CopyTo(input[hashOffset..]);
        return ValueKeccak.Compute(input);
    }

    /// <inheritdoc/>
    public bool TryRequestCells(in ValueHash256 hash, BlobCellMask requestMask, PublicKey lastResortPeerId)
    {
        if (requestMask.IsEmpty || !_transactions.TryGetValue(hash, out TrackedSparseBlobTx? state))
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

            state.PendingCellMask |= ExpireInFlight(state, now);
            requestMask = requestMask.Except(state.InFlightMask);
            if (state.Cells is { } pendingCells) requestMask = requestMask.Except(pendingCells.CellMask);

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
                BlobCellMask existingPeerMask = state.InFlightByPeer.GetValueOrDefault(peer.Id);
                BlobCellMask newlyReservedMask = sendMask.Except(existingPeerMask);
                bool singleProvider = state.Announcements.Count == 1;
                InFlightReservationResult reservationResult = newlyReservedMask.IsEmpty
                    ? InFlightReservationResult.Reserved
                    : TryReserveInFlight(
                        peer,
                        hash,
                        newlyReservedMask.Count,
                        singleProvider,
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
                if (existingPeerMask.IsEmpty && state.ClaimedCellReservations is { } claims)
                {
                    foreach (KeyValuePair<(PublicKey PeerId, long Revision), ClaimedCellReservation> entry in claims)
                    {
                        if (entry.Key.PeerId == peer.Id && entry.Key.Revision < reservationRevision)
                            entry.Value.MutationValid = false;
                    }
                }
                state.InFlightUntilByPeer.TryAdd(peer.Id, now + _cellRequestTimeout);
                if (existingPeerMask.IsEmpty && _peerReceipts.TryGetValue(peer, out PacketReceiptTracker? receipts))
                {
                    state.InFlightReceiptGuards[peer.Id] = receipts.CreateExpiryGuard();
                }
                if (singleProvider) state.SingleProviderInFlightPeers.Add(peer.Id);
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
                state.PendingCellMask |= requestMask;
                return false;
            }

            placedAll = requestMask.IsEmpty;
            state.PendingCellMask |= requestMask;
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
        in ValueHash256 hash,
        ISparseBlobPoolPeer peer,
        BlobCellMask sendMask,
        long reservationRevision)
    {
        PublicKey peerId = peer.Id;
        if (peer.TrySendGetCells(hash.ToHash256(), sendMask, () => HoldCellsRequest(state, peer, reservationRevision, sendMask)))
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
            state.PendingCellMask |= rollbackMask;
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
                state,
                peer.Id,
                rollbackMask.Count,
                releaseHashReference: remaining.IsEmpty);
        }

        return false;
    }

    private (IDisposable Lease, Action<BlobCellMask> Complete) HoldCellsRequest(
        TrackedSparseBlobTx state, ISparseBlobPoolPeer peer, long revision, BlobCellMask mask)
    {
        PublicKey peerId = peer.Id;
        lock (state.Lock)
        {
            if (!IsCurrentState(state.Hash, state)
                || !state.InFlightRevisionByPeer.TryGetValue(peerId, out long currentRevision) || currentRevision != revision
                || !state.InFlightByPeer.TryGetValue(peerId, out BlobCellMask reservedMask)) return MissingCellReservation;
            mask &= reservedMask;
            if (mask.IsEmpty) return MissingCellReservation;
            state.ClaimedCellReservations ??= [];
            if (!state.ClaimedCellReservations.TryGetValue((peerId, revision), out ClaimedCellReservation? claimed))
                state.ClaimedCellReservations[(peerId, revision)] = claimed = new();
            claimed.HoldCount++;
            claimed.Mask |= mask;
            ClaimedCellLease lease = new(this, state, peerId, revision, peer);
            return (lease, lease.Complete);
        }
    }

    private void CompleteClaimedCells(TrackedSparseBlobTx state, PublicKey peerId, long revision, BlobCellMask mask)
    {
        lock (state.Lock)
        {
            if (!IsCurrentState(state.Hash, state)
                || state.ClaimedCellReservations?.TryGetValue((peerId, revision), out ClaimedCellReservation? claimed) != true) return;
            claimed.CompletedMask |= mask & claimed.Mask;
            if (state.InFlightRevisionByPeer.TryGetValue(peerId, out long currentRevision) && currentRevision == revision)
                CompleteCellsRequest(state, peerId, claimed.CompletedMask);
        }
    }

    private void ReleaseClaimedCells(TrackedSparseBlobTx state, PublicKey peerId, long revision)
    {
        lock (state.Lock)
        {
            if (!IsCurrentState(state.Hash, state)
                || state.ClaimedCellReservations is not { } claims
                || !claims.TryGetValue((peerId, revision), out ClaimedCellReservation? claimed)) return;
            if (--claimed.HoldCount != 0) return;
            claims.Remove((peerId, revision));
            if (state.InFlightRevisionByPeer.TryGetValue(peerId, out long currentRevision) && currentRevision == revision)
            {
                BlobCellMask abandoned = claimed.Mask.Except(claimed.CompletedMask);
                state.PendingCellMask |= abandoned;
                CompleteCellsRequest(state, peerId, abandoned);
            }
        }
    }

    /// <inheritdoc/>
    public void OnCellsRequestCompleted(in ValueHash256 hash, BlobCellMask completedMask, ISparseBlobPoolPeer peer)
    {
        if (completedMask.IsEmpty
            || !IsActivePeer(peer)
            || !_transactions.TryGetValue(hash, out TrackedSparseBlobTx? state))
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
            CompleteCellsRequest(state, peerId, completedMask);
        }
    }

    private void CompleteCellsRequest(TrackedSparseBlobTx state, PublicKey peerId, BlobCellMask completedMask)
    {
        if (!state.InFlightByPeer.TryGetValue(peerId, out BlobCellMask peerMask)) return;
        BlobCellMask completedPeerMask = completedMask & peerMask;
        state.InFlightMask = state.InFlightMask.Except(completedPeerMask);
        BlobCellMask remaining = peerMask.Except(completedPeerMask);
        int releasedWork = peerMask.Count - remaining.Count;
        if (remaining.IsEmpty)
        {
            state.InFlightByPeer.Remove(peerId);
            state.InFlightRevisionByPeer.Remove(peerId);
        }
        else state.InFlightByPeer[peerId] = remaining;
        ReleaseInFlight(state, peerId, releasedWork, releaseHashReference: remaining.IsEmpty);
    }

    /// <inheritdoc/>
    public bool TryBeginAnnouncementRestore(ISparseBlobPoolPeer peer, Hash256 hash, out AnnouncementRestoration? restoration, IDisposable? requestOwnership = null)
    {
        restoration = null;
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state)) return true;
        lock (state.Lock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !IsCurrentState(hash, state) || !IsActivePeer(peer)
                || !MatchesCellRequestOwnership(requestOwnership, peer, state)
                || !state.Announcements.TryGetValue(peer.Id, out BlobCellMask mask)) return true;

            DateTimeOffset createdAt = _timestamper.UtcNowOffset;
            CancelAnnouncementRestoration(state, peer.Id);
            lock (_accountingLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || !IsActivePeer(peer)) return true;
                PeerUsage usage = GetPeerUsage(peer.Id);
                if (usage.RestorationTickets >= MaxRestorationTicketsPerPeer) return false;
                usage.RestorationTickets++;
                AddTrackedHashReference(usage, state.Hash);
            }
            restoration = new AnnouncementRestoration(peer, state, mask, createdAt);
            state.RestorationTickets.Add(peer.Id, restoration);
            state.Announcements.Remove(peer.Id);
            ReleaseAnnouncement(peer.Id, state);
            return true;
        }
    }

    /// <inheritdoc/>
    public bool TryRestoreAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash, AnnouncementRestoration restoration)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state)) return false;
        lock (state.Lock)
        {
            if (!ReferenceEquals(restoration.Peer, peer) || !ReferenceEquals(restoration.State, state)
                || !state.RestorationTickets.TryGetValue(peer.Id, out AnnouncementRestoration? current)
                || !ReferenceEquals(current, restoration)) return false;

            if (!restoration.IsValid || Volatile.Read(ref _disposed) != 0 || !IsCurrentState(hash, state) || !IsActivePeer(peer)
                || _timestamper.UtcNowOffset - restoration.CreatedAt >= RestorationTicketTtl)
            {
                CancelAnnouncementRestoration(state, peer.Id);
                return false;
            }

            if (!state.Announcements.ContainsKey(peer.Id))
            {
                if (state.AmbiguousFailureSources.Contains(peer.Id) || !TryReserveAnnouncement(peer, state)) return false;
                state.Announcements.Add(peer.Id, restoration.Mask);
            }
            CancelAnnouncementRestoration(state, peer.Id);
            return true;
        }
    }

    /// <inheritdoc/>
    public AnnouncementRestoration? RemoveAnnouncementPermanently(ISparseBlobPoolPeer peer, Hash256 hash, IDisposable? requestOwnership = null)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state)) return null;
        lock (state.Lock)
        {
            if (!IsCurrentState(hash, state) || !IsActivePeer(peer)
                || !MatchesCellRequestOwnership(requestOwnership, peer, state)) return null;
            AnnouncementRestoration? canceled = CancelAnnouncementRestoration(state, peer.Id);
            if (state.Announcements.Remove(peer.Id)) ReleaseAnnouncement(peer.Id, state);
            return canceled;
        }
    }

    /// <inheritdoc/>
    public BodyRequestAttempt? CreateBodyRequestAttempt(ISparseBlobPoolPeer peer, Hash256 hash) =>
        CreateBodyRequestAttempt(peer, hash.ValueHash256);

    /// <inheritdoc/>
    public BodyRequestAttempt? CreateBodyRequestAttempt(ISparseBlobPoolPeer peer, in ValueHash256 valueHash)
    {
        if (!_transactions.TryGetValue(valueHash, out TrackedSparseBlobTx? state)) return null;
        Hash256 hash = valueHash.ToHash256();
        long sequence;
        lock (state.Lock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !IsCurrentState(hash, state) || !IsActivePeer(peer)) return null;
            sequence = ++state.NextBodyAttempt;
        }
        return new(this, state, peer, sequence, attempt =>
        {
            lock (state.Lock)
            {
                if (Volatile.Read(ref _disposed) == 0 && IsCurrentState(hash, state) && IsActivePeer(peer) && !attempt.IsCompleted && sequence > state.LastConfirmedBodyAttempt)
                {
                    lock (_accountingLock)
                    {
                        if (!IsActivePeer(peer)) return;
                        ReleaseBodyRequestAttempt(state);
                        AddTrackedHashReference(GetPeerUsage(peer.Id), state.Hash);
                        state.LastConfirmedBodyAttempt = sequence;
                        state.CurrentBodyAttempt = attempt;
                        state.CurrentBodyAttemptPeer = peer.Id;
                        state.LastBodyRequestPeer = peer.Id;
                    }
                }
            }
        }, (attempt, unanswered) =>
        {
            ISparseBlobPoolPeer? retryPeer = null;
            ExceptionDispatchInfo? failure = null;
            lock (state.Lock)
            {
                if (!IsCurrentState(hash, state) || !ReferenceEquals(state.CurrentBodyAttempt, attempt)) return;
                ReleaseBodyRequestAttempt(state);
                if ((unanswered || !IsActivePeer(peer)) && state.Transaction is null)
                {
                    retryPeer = PlanBodyRetry(state, out failure);
                }
            }
            SendBodyRetry(hash, state, retryPeer);
            failure?.Throw();
        });
    }

    private void ReleaseBodyRequestAttempt(TrackedSparseBlobTx state)
    {
        if (state.CurrentBodyAttemptPeer is not { } peerId) return;
        state.CurrentBodyAttempt = null;
        state.CurrentBodyAttemptPeer = null;
        lock (_accountingLock)
        {
            if (_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
            {
                ReleaseTrackedHashReference(usage, state.Hash);
                RemovePeerUsageIfEmpty(peerId, usage);
            }
        }
    }

    /// <inheritdoc/>
    public AnnouncementRestoration? RecordBodyUnavailable(ISparseBlobPoolPeer peer, Hash256 hash, BodyRequestAttempt? requestAttempt = null, bool retainCellAnnouncement = false)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state)) return null;
        AnnouncementRestoration? canceled;
        ISparseBlobPoolPeer? retryPeer = null;
        ExceptionDispatchInfo? failure = null;
        lock (state.Lock)
        {
            if (!IsCurrentState(hash, state) || !IsActivePeer(peer)) return null;
            if (requestAttempt is not null)
            {
                if (!ReferenceEquals(requestAttempt.Owner, this) || !ReferenceEquals(requestAttempt.State, state)
                    || !ReferenceEquals(requestAttempt.Peer, peer) || requestAttempt.Sequence < state.LastConfirmedBodyAttempt
                    || !requestAttempt.TryConsumeOmission()) return null;
                state.LastConfirmedBodyAttempt = Math.Max(state.LastConfirmedBodyAttempt, requestAttempt.Sequence);
            }
            if (state.BodyUnavailableSources.Add(peer.Id))
            {
                (state.BodyUnavailableSourceOrder ??= []).Enqueue(peer.Id);
                if (state.BodyUnavailableSources.Count > MaxBodyUnavailableSources)
                    state.BodyUnavailableSources.Remove(state.BodyUnavailableSourceOrder.Dequeue());
            }
            canceled = null;
            if (!retainCellAnnouncement)
            {
                canceled = CancelAnnouncementRestoration(state, peer.Id);
                if (state.Announcements.Remove(peer.Id)) ReleaseAnnouncement(peer.Id, state);
            }
            if (state.Transaction is null && state.CurrentBodyAttempt is null)
                retryPeer = PlanBodyRetry(state, out failure);
        }
        SendBodyRetry(hash, state, retryPeer);
        failure?.Throw();
        return canceled;
    }

    private AnnouncementRestoration? CancelAnnouncementRestoration(TrackedSparseBlobTx state, PublicKey peerId)
    {
        if (!state.RestorationTickets.Remove(peerId, out AnnouncementRestoration? restoration)) return null;
        ReleaseAnnouncementRestoration(state, restoration);
        return restoration;
    }

    private void ReleaseAnnouncementRestoration(TrackedSparseBlobTx state, AnnouncementRestoration restoration)
    {
        if (!restoration.Invalidate()) return;
        lock (_accountingLock)
        {
            PublicKey peerId = restoration.Peer.Id;
            if (_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
            {
                usage.RestorationTickets--;
                ReleaseTrackedHashReference(usage, state.Hash);
                RemovePeerUsageIfEmpty(peerId, usage);
            }
        }
    }

    private void ExpireAnnouncementRestorations(TrackedSparseBlobTx state, DateTimeOffset now)
    {
        List<PublicKey>? expired = null;
        foreach ((PublicKey peerId, AnnouncementRestoration restoration) in state.RestorationTickets)
            if (now - restoration.CreatedAt >= RestorationTicketTtl) (expired ??= []).Add(peerId);
        if (expired is not null)
            foreach (PublicKey peerId in expired) CancelAnnouncementRestoration(state, peerId);
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

            ValueHash256 hash = entry.Key;
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

    private BlobCellMask AddProviderExtraCell(in ValueHash256 hash, TrackedSparseBlobTx state, BlobCellMask requestMask)
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
    public bool HasRecordedTransaction(in ValueHash256 hash)
    {
        if (!_transactions.TryGetValue(hash, out TrackedSparseBlobTx? state))
        {
            return false;
        }

        lock (state.Lock)
        {
            return state.Transaction is not null;
        }
    }

    /// <inheritdoc/>
    public int GetFullProviderAnnouncementCount(in ValueHash256 hash)
    {
        if (!_transactions.TryGetValue(hash, out TrackedSparseBlobTx? state))
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
            // A copy rejected earlier for its sidecar must not shadow this one: the hash does not cover the sidecar.
            _txPool.ForgetInvalidBlobProofs(hash);
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

        peer.RecordUsefulBody(hash.ValueHash256);
        BlobCellMask attachedCellMask = BlobCellMask.Empty;
        byte[][]? attachedCells = null;
        if (transaction.NetworkWrapper is ShardBlobNetworkWrapper sparseWrapper
            && sparseWrapper.Cells is { Length: > 0 } wrapperCells)
        {
            attachedCellMask = sparseWrapper.GetAvailableCellMask();
            if (!attachedCellMask.IsEmpty)
            {
                if (wrapperCells.Length != transaction.BlobVersionedHashes!.Length * attachedCellMask.Count)
                {
                    DisconnectInvalidCellSource(peer);
                    return null;
                }
                attachedCells = wrapperCells;
            }
        }

        TrackedSparseBlobTx state = GetOrAdd(hash, out bool added);
        bool accepted = false;
        List<ISparseBlobPoolPeer>? invalidCellSources = null;
        while (true)
        {
            long transactionRoomNeeded = 0;
            long cellsRoomNeeded = 0;
            lock (state.Lock)
            {
                if (IsCurrentState(hash, state)
                    && IsActivePeer(peer)
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
                            state.TransactionRetryPeers = null;
                            accepted = true;
                        }
                        else
                        {
                            transactionRoomNeeded = transactionBytes;
                        }
                    }

                    if (accepted && !state.Submitting && !state.ApplyingRecordedCells)
                        BindCellCount(state, state.Transaction!.BlobVersionedHashes!.Length, ref invalidCellSources);

                    if (accepted
                        && attachedCells is not null
                        && !state.Submitting
                        && !state.ApplyingRecordedCells)
                    {
                        PendingCellsBuffer addedCells = new(attachedCellMask, attachedCells, peer.Id, peer.CellSourceToken);
                        if (TryStoreRecordedCells(state, peer, addedCells, true, ref invalidCellSources, out cellsRoomNeeded))
                        {
                            state.CellsExpiresAt = _timestamper.UtcNowOffset + EarlyCellsTtl;
                        }
                    }

                    if (accepted)
                    {
                        Touch(state, _timestamper.UtcNowOffset);
                    }
                }
            }

            if (!(MakeRoom(peer.Id, transactionRoomNeeded, earlyCells: false, state)
                  | MakeRoom(peer.Id, cellsRoomNeeded, earlyCells: true, state)))
            {
                break;
            }
        }

        accepted = CompleteRecord(hash, state, added, accepted);
        DisconnectInvalidCellSources(invalidCellSources);
        if (!accepted)
        {
            return null;
        }

        return TrySubmit(hash, state);
    }

    /// <inheritdoc/>
    public bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells, IDisposable? requestOwnership = null)
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

        bool matchesKnownCellCount = MatchesKnownCellCount(hash, cellMask, cells.Length, out bool knownCellCount);

        PendingCellsBuffer addedCells = new(cellMask, cells, peer.Id, peer.CellSourceToken);
        if (addedCells.ByteLength > MaxEarlyCellsPerTransactionBytes)
        {
            return false;
        }

        TrackedSparseBlobTx state;
        bool added;
        if (requestOwnership is not null)
        {
            if (!_transactions.TryGetValue(hash.ValueHash256, out state!)) return false;
            added = false;
        }
        else state = GetOrAdd(hash, out added);
        bool accepted = false;
        List<ISparseBlobPoolPeer>? invalidCellSources = null;
        while (true)
        {
            long cellsRoomNeeded = 0;
            lock (state.Lock)
            {
                if (IsCurrentState(hash, state)
                    && IsActivePeer(peer)
                    && MatchesCellRequestOwnership(requestOwnership, peer, state)
                    && !state.Submitting
                    && !state.ApplyingRecordedCells
                    && !state.AmbiguousFailureSources.Contains(peer.Id))
                {
                    int? recordedCount = state.Transaction?.BlobVersionedHashes is { Length: > 0 } recordedHashes ? recordedHashes.Length : null;
                    if (!matchesKnownCellCount)
                    {
                        invalidCellSources = [peer];
                    }
                    else if (recordedCount is not null && recordedCount != addedCells.BlobCount)
                    {
                        RecordAmbiguousFailure(state, peer.Id);
                        invalidCellSources = [peer];
                    }
                    else
                    {
                        knownCellCount |= recordedCount is not null;
                        if (knownCellCount) BindCellCount(state, addedCells.BlobCount, ref invalidCellSources);
                        if (ContainsCells(state.Cells) || ContainsCells(state.AlternativeCells))
                        {
                            accepted = true;
                        }
                        else if (TryStoreRecordedCells(state, peer, addedCells, knownCellCount, ref invalidCellSources, out cellsRoomNeeded))
                        {
                            DateTimeOffset now = _timestamper.UtcNowOffset;
                            state.CellsExpiresAt = now + EarlyCellsTtl;
                            Touch(state, now);
                            accepted = true;
                        }
                    }
                }
            }

            if (!MakeRoom(peer.Id, cellsRoomNeeded, earlyCells: true, state)) break;
        }

        accepted = CompleteRecord(hash, state, added, accepted);
        DisconnectInvalidCellSources(invalidCellSources);
        if (!accepted)
        {
            return false;
        }

        TrySubmit(hash, state);
        return true;

        bool ContainsCells(PendingCellsBuffer? buffer) => buffer is { } existing
            && existing.BlobCount == addedCells.BlobCount && (existing.CellMask & cellMask) == cellMask;
    }

    private void DisconnectInvalidCellSource(ISparseBlobPoolPeer peer)
    {
        peer.DisconnectSparseBlobPeer(DisconnectReason.BreachOfProtocol, "Sparse blob cell count does not match the transaction.");
        RemovePeer(peer);
    }

    private void DisconnectInvalidCellSources(List<ISparseBlobPoolPeer>? peers)
    {
        if (peers is null) return;
        foreach (ISparseBlobPoolPeer peer in peers) DisconnectInvalidCellSource(peer);
    }

    private bool MatchesKnownCellCount(Hash256 hash, BlobCellMask cellMask, int cellCount, out bool knownCellCount)
    {
        knownCellCount = true;
        if (_txPool.TryGetPendingBlobTransaction(hash, out Transaction? transaction))
        {
            return cellCount == transaction.BlobVersionedHashes!.Length * cellMask.Count;
        }
        if (_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            lock (state.Lock)
            {
                if (state.Transaction?.BlobVersionedHashes is { Length: > 0 } recordedHashes)
                {
                    return cellCount == recordedHashes.Length * cellMask.Count;
                }
            }
        }
        knownCellCount = false;
        return true;
    }

    private bool TryStoreRecordedCells(TrackedSparseBlobTx state, ISparseBlobPoolPeer peer, in PendingCellsBuffer addedCells, bool knownCellCount, ref List<ISparseBlobPoolPeer>? invalidCellSources, out long roomNeeded)
    {
        roomNeeded = 0;
        if (knownCellCount) BindCellCount(state, addedCells.BlobCount, ref invalidCellSources);
        bool alternative = state.Cells is { } current && current.BlobCount != addedCells.BlobCount;
        if (alternative && knownCellCount) return false;
        PendingCellsBuffer? existing = alternative ? state.AlternativeCells : state.Cells;
        PendingCellsBuffer replacement = addedCells;
        if (existing is { } retained && !retained.TryMerge(addedCells, out replacement)) return false;
        int otherBytes = (alternative ? state.Cells : state.AlternativeCells)?.ByteLength ?? 0;
        if ((long)replacement.ByteLength + otherBytes > MaxEarlyCellsPerTransactionBytes) return false;
        if (!TryReplaceCellsAccounting(state.Hash, existing, replacement, peer))
        {
            roomNeeded = replacement.ByteLength - (existing?.ByteLength ?? 0);
            return false;
        }
        if (alternative) state.AlternativeCells = replacement;
        else state.Cells = replacement;
        return true;
    }

    private void BindCellCount(TrackedSparseBlobTx state, int blobCount, ref List<ISparseBlobPoolPeer>? invalidCellSources)
    {
        if (blobCount <= 0) return;
        List<ISparseBlobPoolPeer>? rejectedSources = invalidCellSources;
        if (state.Cells is { } current && current.BlobCount != blobCount)
        {
            Exclude(current);
            state.Cells = null;
        }
        if (state.AlternativeCells is { } alternative)
        {
            if (alternative.BlobCount == blobCount) state.Cells = alternative;
            else Exclude(alternative);
        }
        state.AlternativeCells = null;
        invalidCellSources = rejectedSources;

        void Exclude(in PendingCellsBuffer cells)
        {
            foreach (PendingCellsSource source in cells.Sources)
            {
                RecordAmbiguousFailure(state, source.PeerId);
                if (_peers.TryGetValue(source.PeerId, out ISparseBlobPoolPeer? peer)
                    && ReferenceEquals(source.SourceToken, peer.CellSourceToken))
                {
                    rejectedSources ??= [];
                    if (!rejectedSources.Contains(peer)) rejectedSources.Add(peer);
                }
            }
            ReleaseCells(state.Hash, cells);
        }
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
            _txPool.TryGetPendingBlobTransaction(hash, out Transaction? pooledTransaction);
            PendingCellsBuffer pending = default;
            List<ISparseBlobPoolPeer>? invalidCellSources = null;
            bool canApply;
            lock (state.Lock)
            {
                if (IsCurrentState(hash, state) && pooledTransaction is not null && !state.Submitting && !state.ApplyingRecordedCells)
                    BindCellCount(state, pooledTransaction.BlobVersionedHashes!.Length, ref invalidCellSources);
                canApply = IsCurrentState(hash, state)
                    && !state.Submitting
                    && !state.ApplyingRecordedCells
                    && state.Cells is not null;
                if (canApply)
                {
                    state.ApplyingRecordedCells = true;
                    pending = state.Cells!.Value;
                }
            }
            if (!canApply)
            {
                DisconnectInvalidCellSources(invalidCellSources);
                return false;
            }

            BlobCellMergeResult mergeResult;
            PublicKey? invalidCellSource;
            try
            {
                DisconnectInvalidCellSources(invalidCellSources);
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
            lock (state.Lock)
            {
                state.ApplyingRecordedCells = false;
                if (!IsCurrentState(hash, state))
                {
                    return false;
                }

                if (invalidProofTuple)
                {
                    RecordAmbiguousFailure(state, invalidCellSource);
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

    bool ISparseBlobPoolPeerRegistry.CoversRequest(ISparseBlobPoolPeer peer, ValueHash256 hash, BlobCellMask mask) =>
        CoversRequest(peer, hash, mask);

    internal bool CoversRequest(ISparseBlobPoolPeer peer, ValueHash256 hash, BlobCellMask mask)
    {
        if (!_transactions.TryGetValue(hash, out TrackedSparseBlobTx? state)) return false;
        lock (state.Lock)
            return state.Announcements.TryGetValue(peer.Id, out BlobCellMask announced) && (announced & mask) == mask;
    }

    private void CreditCellSource(PendingCellsSource source, Hash256 hash)
    {
        if (_peers.TryGetValue(source.PeerId, out ISparseBlobPoolPeer? peer)
            && ReferenceEquals(source.SourceToken, peer.CellSourceToken))
            peer.RecordUsefulCells(hash.ValueHash256);
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
            BlobCellMergeResult result = _txPool.MergeBlobCells(hash, source.CellMask, sourceCells, out BlobCellMask addedMask);
            if (result == BlobCellMergeResult.Accepted && !addedMask.IsEmpty) CreditCellSource(source, hash);
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
    public void Clear(in ValueHash256 hash)
    {
        if (_transactions.TryGetValue(hash, out TrackedSparseBlobTx? state))
        {
            TryRemoveState(hash, state);
        }
    }

    private TrackedSparseBlobTx GetOrAdd(in ValueHash256 key, out bool added)
    {
        while (true)
        {
            if (_transactions.TryGetValue(key, out TrackedSparseBlobTx? existing))
            {
                added = false;
                return existing;
            }

            DateTimeOffset now = _timestamper.UtcNowOffset;
            long revision = Interlocked.Increment(ref _trackedStateRevision);
            TrackedSparseBlobTx state = new(key, now, now + GetAdmissionDelay(key), revision);
            if (_transactions.TryAdd(key, state))
            {
                Interlocked.Increment(ref _trackedTransactionCount);
                added = true;
                return state;
            }
        }
    }

    private bool CompleteRecord(in ValueHash256 hash, TrackedSparseBlobTx state, bool added, bool accepted)
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
                    _transactionOrder.Enqueue(new TrackedStateKey(hash, state.Revision));
                    _transactionOrderCount++;
                }

                TrimTrackedTransactions(state);
            }

            return IsCurrentState(hash, state);
        }

        if (!added)
        {
            return false;
        }

        lock (state.Lock)
        {
            if (state.Announcements.Count == 0
                && state.RestorationTickets.Count == 0
                && state.CurrentBodyAttempt is null
                && state.Transaction is null
                && state.Cells is null
                && state.InFlightByPeer.Count == 0
                && !state.Submitted
                && !state.Submitting)
            {
                TryRemoveState(hash, state);
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
                || !peer.CanRequestCells)
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
            if (result == AcceptTxResult.Accepted)
                foreach (PendingCellsSource source in cells.Value.Sources) CreditCellSource(source, hash);
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
                    state.BodyRetryDueAt = null;
                    state.AmbiguousBodySource = null;
                    RetainSubmittedAnnouncements(state);
                    ReleaseTransaction(state);
                    ReleaseCells(state);
                    state.Transaction = null;
                    state.TransactionPeer = null;
                    state.Cells = null;
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
        bool removalRequested;
        ExceptionDispatchInfo? retryFailure = null;
        lock (state.Lock)
        {
            removalRequested = state.RemovalRequested;
            if (removalRequested)
            {
                state.Submitting = false;
            }
            else if (failureSource == SparseBlobAttachFailureSource.Ambiguous)
            {
                RecordAmbiguousFailure(state, transactionPeer?.Id);
                state.AmbiguousBodySource = transactionPeer?.Id;
                foreach (PendingCellsSource source in cells.Sources)
                {
                    RecordAmbiguousFailure(state, source.PeerId);
                }
                ReleaseTransaction(state);
                ReleaseCells(state);
                state.Transaction = null;
                state.TransactionPeer = null;
                state.Cells = null;
                transactionRetryPeer = PlanBodyRetry(state, out retryFailure);
            }
            else if (failureSource == SparseBlobAttachFailureSource.Transaction)
            {
                if (transactionPeer is not null)
                {
                    peerToDisconnect = transactionPeer;
                    peerToRemove = transactionPeer.Id;
                    disconnectDetails = error ?? "invalid sparse blob transaction";
                }

                ReleaseTransaction(state);
                state.Transaction = null;
                state.TransactionPeer = null;
                transactionRetryPeer = PlanBodyRetry(state, out retryFailure, cells, transactionPeer);
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

        if (failureSource == SparseBlobAttachFailureSource.Ambiguous)
        {
            _txPool.ForgetRejectedBlobTransaction(hash);
        }

        SendBodyRetry(hash, state, transactionRetryPeer);
        TryRequestCells(hash, cells.CellMask, retryFallbackPeerId);
        retryFailure?.Throw();
        return null;
    }

    private AcceptTxResult SubmitTransaction(ISparseBlobPoolPeer? peer, Transaction transaction)
    {
        transaction.Timestamp = Timestamper.Default.UnixTime.Seconds;
        AcceptTxResult result = _txPool.SubmitTx(transaction, TxHandlingOptions.None);
        if (_logger.IsTrace)
        {
            _logger.Trace($"{peer?.Id:hide} sent sparse blob tx {transaction.Hash} and it was {result}");
        }

        return result;
    }

    private void RecordAmbiguousFailure(TrackedSparseBlobTx state, PublicKey? sourcePeerId)
    {
        if (sourcePeerId is null) return;
        if (state.AmbiguousFailureSources.Add(sourcePeerId))
        {
            Queue<PublicKey> sourceOrder = state.AmbiguousFailureSourceOrder ??= new();
            sourceOrder.Enqueue(sourcePeerId);
            if (sourceOrder.Count > MaxAmbiguousFailureSources)
            {
                state.AmbiguousFailureSources.Remove(sourceOrder.Dequeue());
            }
        }

        if (state.Announcements.Remove(sourcePeerId))
        {
            ReleaseAnnouncement(sourcePeerId, state);
        }
    }

    private void OnMaintenanceElapsed(object? sender, EventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _maintenanceScheduled, 1, 0) != 0)
        {
            return;
        }

        if (!_backgroundTaskScheduler.TryScheduleTask(
            new MaintenanceSweepRequest(this),
            static (request, cancellationToken) => request.Registry.RunMaintenance(cancellationToken),
            timeout: ScheduledActionTimeout))
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

                MaintainTrackedState(entry.Key, entry.Value, now);
            }

            TrySchedulePendingCustodyUpdate();
        }
        finally
        {
            Interlocked.Exchange(ref _maintenanceScheduled, 0);
        }

        return Task.CompletedTask;
    }

    private void MaintainTrackedState(in ValueHash256 hash, TrackedSparseBlobTx state, DateTimeOffset now)
    {
        BlobCellMask retryMask = BlobCellMask.Empty;
        ISparseBlobPoolPeer? bodyRetryPeer = null;
        ExceptionDispatchInfo? bodyRetryFailure = null;
        bool submit = false;
        bool remove = false;
        lock (state.Lock)
        {
            if (!IsCurrentState(hash, state))
            {
                return;
            }

            ExpireAnnouncementRestorations(state, now);

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
                if (state.Transaction is null && state.CurrentBodyAttempt is null && state.BodyRetryDueAt is { } bodyRetryDueAt && now >= bodyRetryDueAt)
                {
                    bodyRetryPeer = PlanBodyRetry(state, out bodyRetryFailure);
                    if (bodyRetryPeer is null && bodyRetryFailure is null && !state.BodyRetrySending
                        && state.TransactionRetryPeers is null && state.AmbiguousBodySource is { } bodySource
                        && _peers.TryGetValue(bodySource, out ISparseBlobPoolPeer? fallback) && CanRequestBody(state, fallback))
                    {
                        state.AmbiguousFailureSources.Remove(bodySource);
                        bodyRetryPeer = fallback;
                        state.BodyRetryDueAt = now + Timeouts.Eth;
                    }
                }
                if (state.Transaction is null
                    && state.Cells is not null
                    && now >= state.CellsExpiresAt)
                {
                    ReleaseCells(state);
                    state.Cells = null;
                }

                retryMask = state.PendingCellMask | ExpireInFlight(state, now);
                state.PendingCellMask = BlobCellMask.Empty;

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
            TryRequestCells(hash, retryMask, NoLastResortPeer);
        }

        SendBodyRetry(hash.ToHash256(), state, bodyRetryPeer);
        bodyRetryFailure?.Throw();

        if (submit)
        {
            TrySubmit(hash.ToHash256(), state);
        }
    }

    private bool HasFullLocalBlobTransaction(in ValueHash256 hash)
        => _txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask availableMask)
            && availableMask.IsFull;

    private bool IsActivePeer(ISparseBlobPoolPeer peer)
        => _peers.TryGetValue(peer.Id, out ISparseBlobPoolPeer? registeredPeer)
            && ReferenceEquals(registeredPeer, peer)
            && !peer.IsClosing;

    private bool IsActivePeer(PublicKey peerId)
        => _peers.TryGetValue(peerId, out ISparseBlobPoolPeer? peer)
            && !peer.IsClosing;

    private BlobCellMask GetMissingAnnouncedMask(in ValueHash256 hash, TrackedSparseBlobTx state)
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

    private BlobCellMask GetMissingLocalMask(in ValueHash256 hash, BlobCellMask requestedMask)
        => _txPool.TryGetPendingBlobCellMask(hash, out BlobCellMask availableMask)
            ? requestedMask.Except(availableMask)
            : requestedMask;

    private bool TryRemoveState(in ValueHash256 hash, TrackedSparseBlobTx state)
    {
        lock (state.Lock)
        {
            if (state.Submitting)
            {
                state.RemovalRequested = true;
                return false;
            }

            if (!TryRemoveTrackedState(hash, state))
            {
                return false;
            }

            foreach (PublicKey peerId in state.Announcements.Keys)
            {
                ReleaseAnnouncement(peerId, state);
            }

            foreach (KeyValuePair<PublicKey, BlobCellMask> inFlight in state.InFlightByPeer)
            {
                ReleaseInFlight(state, inFlight.Key, inFlight.Value.Count, releaseHashReference: true);
            }

            foreach (AnnouncementRestoration restoration in state.RestorationTickets.Values)
                ReleaseAnnouncementRestoration(state, restoration);
            state.RestorationTickets.Clear();
            ReleaseBodyRequestAttempt(state);
            state.Announcements.Clear();
            state.InFlightByPeer.Clear();
            state.InFlightRevisionByPeer.Clear();
            state.ClaimedCellReservations?.Clear();
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

    private void TrimTrackedTransactions(TrackedSparseBlobTx incoming)
    {
        lock (_transactionOrderLock)
        {
            LastTrimStateCount = 0;
            int remainingAttempts = Math.Min(_transactionOrderCount, MaxTrimCandidates);
            while ((Volatile.Read(ref _trackedTransactionCount) > MaxTrackedTransactions || _transactionOrderCount > MaxTrackedTransactions * TrackedStateQueueSlack)
                && remainingAttempts-- > 0
                && _transactionOrder.TryDequeue(out TrackedStateKey key))
            {
                _transactionOrderCount--;
                if (_transactions.TryGetValue(key.Hash, out TrackedSparseBlobTx? state)
                    && state.Revision == key.Revision)
                {
                    LastTrimStateCount++;
                    lock (state.Lock)
                    {
                        if (Volatile.Read(ref _trackedTransactionCount) <= MaxTrackedTransactions
                            || HasProgress(state) || !TryRemoveState(key.Hash.ToHash256(), state))
                        {
                            _transactionOrder.Enqueue(key);
                            _transactionOrderCount++;
                        }
                    }
                }
            }

            if (Volatile.Read(ref _trackedTransactionCount) <= MaxTrackedTransactions)
            {
                return;
            }

            Span<TrackedStateKey> candidates = stackalloc TrackedStateKey[MaxTrimCandidates];
            int count = CopyOverrepresentedAnnouncements(candidates, out PublicKey? overrepresentedPeerId);
            TrackedSparseBlobTx? oldest = null;
            bool oldestIsSoleAnnouncement = false;
            for (int i = 0; i < count; i++)
            {
                if (_transactions.TryGetValue(candidates[i].Hash, out TrackedSparseBlobTx? state)
                    && state.Revision == candidates[i].Revision)
                {
                    LastTrimStateCount++;
                    lock (state.Lock)
                    {
                        if (HasProgress(state) || !state.Announcements.ContainsKey(overrepresentedPeerId!)) continue;
                        bool soleAnnouncement = state.Announcements.Count == 1;
                        if (oldest is null
                            || soleAnnouncement && !oldestIsSoleAnnouncement
                            || soleAnnouncement == oldestIsSoleAnnouncement && state.Revision < oldest.Revision)
                        {
                            oldest = state;
                            oldestIsSoleAnnouncement = soleAnnouncement;
                        }
                    }
                }
            }

            if (oldest is not null)
            {
                LastTrimStateCount++;
                lock (oldest.Lock)
                {
                    if (!HasProgress(oldest)
                        && oldest.Announcements.ContainsKey(overrepresentedPeerId!)
                        && (!oldestIsSoleAnnouncement || oldest.Announcements.Count == 1)
                        && TryRemoveState(oldest.Hash.ToHash256(), oldest)) return;
                }
            }

            TryRemoveState(incoming.Hash.ToHash256(), incoming);
        }
    }

    private static bool HasProgress(TrackedSparseBlobTx state) =>
        state.Submitting || state.Submitted || state.ApplyingRecordedCells || state.Transaction is not null
        || state.Cells is not null || state.AlternativeCells is not null || state.CurrentBodyAttempt is not null || !state.InFlightMask.IsEmpty
        || state.ClaimedCellReservations is { Count: > 0 };

    private int CopyOverrepresentedAnnouncements(Span<TrackedStateKey> candidates, out PublicKey? peerId)
    {
        lock (_accountingLock)
        {
            PeerUsage? largest = null;
            peerId = null;
            foreach ((PublicKey candidatePeerId, PeerUsage usage) in _peerUsage)
            {
                if (largest is null || usage.AnnouncedHashes.Count > largest.AnnouncedHashes.Count)
                {
                    largest = usage;
                    peerId = candidatePeerId;
                }
            }

            if (largest is null || largest.AnnouncedHashes.Count == 0) return 0;
            int count = 0;
            foreach ((long revision, ValueHash256 hash) in largest.AnnouncedStates)
            {
                candidates[count++] = new(hash, revision);
                if (count == candidates.Length) break;
            }
            return count;
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

    private bool IsCurrentState(Hash256 hash, TrackedSparseBlobTx state) => IsCurrentState(hash.ValueHash256, state);

    private bool IsCurrentState(in ValueHash256 hash, TrackedSparseBlobTx state)
        => _transactions.TryGetValue(hash, out TrackedSparseBlobTx? current)
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

    private bool TryReserveAnnouncement(ISparseBlobPoolPeer peer, TrackedSparseBlobTx state)
    {
        lock (_accountingLock)
        {
            if (!IsActivePeer(peer))
            {
                return false;
            }

            PublicKey peerId = peer.Id;
            PeerUsage usage = GetPeerUsage(peerId);
            int count = state.Submitted ? usage.RetainedAnnouncements : usage.Announcements;
            int limit = state.Submitted ? MaxRetainedAnnouncementsPerPeer : MaxAnnouncementsPerPeer;
            if (count >= limit)
            {
                RemovePeerUsageIfEmpty(peerId, usage);
                return false;
            }

            if (state.Submitted)
            {
                usage.RetainedAnnouncements++;
            }
            else
            {
                usage.Announcements++;
            }

            usage.AnnouncedHashes[state.Hash] = usage.AnnouncedHashes.GetValueOrDefault(state.Hash) + 1;
            usage.AnnouncedStates.Add(state.Revision, state.Hash);
            AddTrackedHashReference(usage, state.Hash);
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
                    ReleaseAnnouncementReference(usage, state);
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

    private void ReleaseAnnouncement(PublicKey peerId, TrackedSparseBlobTx state)
    {
        lock (_accountingLock)
        {
            if (!_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
            {
                return;
            }

            if (state.Submitted)
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

            ReleaseAnnouncementReference(usage, state);
            RemovePeerUsageIfEmpty(peerId, usage);
        }
    }

    private static void ReleaseAnnouncementReference(PeerUsage usage, TrackedSparseBlobTx state)
    {
        ValueHash256 hash = state.Hash;
        usage.AnnouncedStates.Remove(state.Revision);
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

    private bool MakeRoom(PublicKey peerId, long byteLength, bool earlyCells, TrackedSparseBlobTx incoming)
    {
        bool evicted = false;
        while (byteLength > 0
            && IsBudgetFull(byteLength, earlyCells) && TryEvictOldest(peerId, byteLength, earlyCells, incoming))
        {
            evicted = true;
        }

        return evicted;
    }

    private bool IsBudgetFull(long byteLength, bool earlyCells) => earlyCells
        ? Volatile.Read(ref _earlyCellsBytes) + byteLength > MaxEarlyCellsBytes
        : Volatile.Read(ref _trackedTransactionBytes) + byteLength > MaxTrackedTransactionBytes;

    private static long GetBudgetBytes(PeerUsage usage, bool earlyCells)
        => earlyCells ? usage.EarlyCellBytes : usage.TransactionBytes;

    private bool TryEvictOldest(PublicKey peerId, long byteLength, bool earlyCells, TrackedSparseBlobTx incoming)
    {
        List<(PublicKey PeerId, long Bytes, long Sequence)> owners = [];
        lock (_accountingLock)
        {
            long requiredOwnerBytes = byteLength
                + (_peerUsage.TryGetValue(peerId, out PeerUsage? requesterUsage) ? GetBudgetBytes(requesterUsage, earlyCells) : 0);
            foreach (KeyValuePair<PublicKey, PeerUsage> entry in _peerUsage)
            {
                long entryBytes = GetBudgetBytes(entry.Value, earlyCells);
                if (entryBytes >= requiredOwnerBytes && entry.Key != peerId)
                {
                    owners.Add((entry.Key, entryBytes, _peerSessionSequences.GetValueOrDefault(entry.Key)));
                }
            }
        }

        owners.Sort(static (x, y) => x.Bytes != y.Bytes ? y.Bytes.CompareTo(x.Bytes) : y.Sequence.CompareTo(x.Sequence));
        List<TrackedSparseBlobTx> candidates = [];
        for (int ownerIndex = 0; ownerIndex < owners.Count; ownerIndex++)
        {
            PublicKey ownerId = owners[ownerIndex].PeerId;
            ValueHash256[] ownerHashes;
            lock (_accountingLock)
            {
                if (!_peerUsage.TryGetValue(ownerId, out PeerUsage? ownerUsage))
                {
                    continue;
                }

                ownerHashes = [.. ownerUsage.TrackedHashes.Keys];
            }

            candidates.Clear();
            for (int i = 0; i < ownerHashes.Length; i++)
            {
                if (_transactions.TryGetValue(ownerHashes[i], out TrackedSparseBlobTx? state) && !ReferenceEquals(state, incoming))
                {
                    lock (state.Lock)
                    {
                        if (IsEvictable(state, ownerId, earlyCells))
                        {
                            candidates.Add(state);
                        }
                    }
                }
            }

            candidates.Sort(static (x, y) => x.Revision.CompareTo(y.Revision));
            bool evicted = false;
            for (int i = 0; i < candidates.Count && IsBudgetFull(byteLength, earlyCells); i++)
            {
                evicted |= earlyCells
                    ? TryEvictEarlyCells(candidates[i], ownerId)
                    : TryEvictTransaction(candidates[i], ownerId);
            }

            if (evicted)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEvictable(TrackedSparseBlobTx state, PublicKey ownerId, bool earlyCells)
    {
        if (state.Submitting || state.Submitted)
        {
            return false;
        }

        return earlyCells
            ? state.Transaction is null
                && !state.ApplyingRecordedCells
                && ((state.Cells?.GetByteLength(ownerId) ?? 0) > 0
                    || (state.AlternativeCells?.GetByteLength(ownerId) ?? 0) > 0)
            : state.TransactionBytes > 0
                && state.Cells is null
                && state.AlternativeCells is null
                && state.CurrentBodyAttempt is null
                && state.TransactionPeer?.Id == ownerId;
    }

    private bool TryEvictTransaction(TrackedSparseBlobTx state, PublicKey ownerId)
    {
        ISparseBlobPoolPeer? retryPeer;
        ExceptionDispatchInfo? retryFailure;
        lock (state.Lock)
        {
            if (!IsCurrentState(state.Hash, state) || !IsEvictable(state, ownerId, earlyCells: false))
            {
                return false;
            }

            ReleaseTransaction(state);
            state.Transaction = null;
            state.TransactionPeer = null;
            state.TransactionRetryPeers = [ownerId];
            state.TransactionRetryAttempts = 0;
            state.TransactionRetryResumeAfter = null;
            if (IsUnused(state))
            {
                TryRemoveState(state.Hash, state);
                return true;
            }
            retryPeer = PlanBodyRetry(state, out retryFailure);
        }

        if (retryPeer is not null) SendBodyRetry(state.Hash.ToHash256(), state, retryPeer);
        retryFailure?.Throw();
        return true;
    }

    private ISparseBlobPoolPeer? SelectUntriedTransactionRetryPeer(TrackedSparseBlobTx state, HashSet<PublicKey> triedPeers, HashSet<PublicKey>? failedPeers = null)
    {
        PublicKey? resumeAfter = state.TransactionRetryResumeAfter;
        if (resumeAfter is null && state.Cells is { } cells)
        {
            for (int i = 0; i < cells.Sources.Length; i++)
            {
                PublicKey sourcePeerId = cells.Sources[i].PeerId;
                if (!triedPeers.Contains(sourcePeerId)
                    && failedPeers?.Contains(sourcePeerId) != true
                    && state.Announcements.ContainsKey(sourcePeerId)
                    && _peers.TryGetValue(sourcePeerId, out ISparseBlobPoolPeer? sourcePeer)
                    && CanRequestBody(state, sourcePeer))
                {
                    return sourcePeer;
                }
            }
        }

        ISparseBlobPoolPeer? first = null;
        bool afterCursor = resumeAfter is null;
        foreach (PublicKey peerId in state.Announcements.Keys)
        {
            if (!triedPeers.Contains(peerId)
                && failedPeers?.Contains(peerId) != true
                && _peers.TryGetValue(peerId, out ISparseBlobPoolPeer? peer)
                && CanRequestBody(state, peer))
            {
                if (afterCursor) return peer;
                first ??= peer;
            }
            afterCursor |= peerId == resumeAfter;
        }

        return first;
    }

    private bool TryEvictEarlyCells(TrackedSparseBlobTx state, PublicKey ownerId)
    {
        lock (state.Lock)
        {
            if (!IsCurrentState(state.Hash, state) || !IsEvictable(state, ownerId, earlyCells: true))
            {
                return false;
            }

            state.Cells = RemoveSource(state.Cells, state.Hash, ownerId);
            state.AlternativeCells = RemoveSource(state.AlternativeCells, state.Hash, ownerId);
            if (state.Cells is null)
            {
                state.Cells = state.AlternativeCells;
                state.AlternativeCells = null;
            }

            if (IsUnused(state))
            {
                TryRemoveState(state.Hash, state);
            }
        }

        return true;
    }

    private PendingCellsBuffer? RemoveSource(PendingCellsBuffer? buffer, in ValueHash256 hash, PublicKey ownerId)
    {
        if (buffer is not { } current || !current.TryRemoveSource(ownerId, out PendingCellsBuffer? remaining)) return buffer;
        if (remaining is { } retained && TryReplaceCellsAccounting(hash, current, retained)) return retained;
        ReleaseCells(hash, current);
        return null;
    }

    private static bool IsUnused(TrackedSparseBlobTx state)
        => state.Announcements.Count == 0
            && state.RestorationTickets.Count == 0
            && state.InFlightByPeer.Count == 0
            && !HasProgress(state);

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
        if (state.Cells is { } cells) ReleaseCells(state.Hash, cells);
        if (state.AlternativeCells is { } alternative) ReleaseCells(state.Hash, alternative);
        state.AlternativeCells = null;
    }

    private void ReleaseCells(ValueHash256 hash, in PendingCellsBuffer cells)
    {
        lock (_accountingLock)
        {
            _earlyCellsBytes -= cells.ByteLength;
            for (int i = 0; i < cells.Sources.Length; i++)
            {
                PublicKey peerId = cells.Sources[i].PeerId;
                if (_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
                {
                    usage.EarlyCellBytes -= cells.GetByteLength(peerId);
                    ReleaseTrackedHashReference(usage, hash);
                    RemovePeerUsageIfEmpty(peerId, usage);
                }
            }
        }
    }

    private InFlightReservationResult TryReserveInFlight(ISparseBlobPoolPeer peer, ValueHash256 hash, int work, bool singleProvider, bool addHashReference)
    {
        lock (_accountingLock)
        {
            if (!IsActivePeer(peer))
            {
                return InFlightReservationResult.InactivePeer;
            }

            PublicKey peerId = peer.Id;
            PeerUsage usage = GetPeerUsage(peerId);
            if (_inFlightCellWork + work > MaxInFlightCellWork
                || (singleProvider && _singleProviderCellWork + work > MaxSingleProviderCellWork))
            {
                RemovePeerUsageIfEmpty(peerId, usage);
                return InFlightReservationResult.GlobalCapacityExceeded;
            }

            int sharedBudget = singleProvider ? MaxSingleProviderCellWork : MaxInFlightCellWork;
            int peerLimit = Math.Max(BlobCellMask.CellCount,
                Math.Min(MaxInFlightCellWorkPerPeer, sharedBudget / Math.Max(MinimumCellWorkShares, _peers.Count)));
            if (usage.InFlightCellWork + work > peerLimit)
            {
                RemovePeerUsageIfEmpty(peerId, usage);
                return InFlightReservationResult.PeerCapacityExceeded;
            }

            _inFlightCellWork += work;
            if (singleProvider) _singleProviderCellWork += work;
            usage.InFlightCellWork += work;
            if (addHashReference)
            {
                AddTrackedHashReference(usage, hash);
            }

            return InFlightReservationResult.Reserved;
        }
    }

    private void ReleaseInFlight(TrackedSparseBlobTx state, PublicKey peerId, int work, bool releaseHashReference)
    {
        if (work == 0 && !releaseHashReference)
        {
            return;
        }

        lock (_accountingLock)
        {
            _inFlightCellWork -= work;
            if (state.SingleProviderInFlightPeers.Contains(peerId)) _singleProviderCellWork -= work;
            if (releaseHashReference)
            {
                state.SingleProviderInFlightPeers.Remove(peerId);
                state.InFlightUntilByPeer.Remove(peerId);
                state.InFlightReceiptGuards.Remove(peerId);
            }
            if (_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
            {
                usage.InFlightCellWork -= work;
                if (releaseHashReference)
                {
                    ReleaseTrackedHashReference(usage, state.Hash);
                }

                RemovePeerUsageIfEmpty(peerId, usage);
            }
        }
    }

    private BlobCellMask ExpireInFlight(TrackedSparseBlobTx state, DateTimeOffset now)
    {
        List<PublicKey>? expiredPeers = null;
        foreach ((PublicKey peerId, DateTimeOffset deadline) in state.InFlightUntilByPeer)
        {
            if (now >= deadline
                && state.ClaimedCellReservations?.ContainsKey((peerId, state.InFlightRevisionByPeer[peerId])) != true
                && (!state.InFlightReceiptGuards.TryGetValue(peerId, out PacketReceiptTracker.ExpiryGuard? guard)
                    || guard.CanExpire()))
                (expiredPeers ??= []).Add(peerId);
        }

        BlobCellMask expiredMask = BlobCellMask.Empty;
        if (expiredPeers is null) return expiredMask;
        foreach (PublicKey peerId in expiredPeers)
        {
            CancelAnnouncementRestoration(state, peerId);
            BlobCellMask mask = state.InFlightByPeer[peerId];
            expiredMask |= mask;
            state.InFlightByPeer.Remove(peerId);
            state.InFlightRevisionByPeer.Remove(peerId);
            state.InFlightMask = state.InFlightMask.Except(mask);
            ReleaseInFlight(state, peerId, mask.Count, releaseHashReference: true);
            if (state.Announcements.Remove(peerId))
            {
                ReleaseAnnouncement(peerId, state);
            }
        }

        return expiredMask;
    }

    private ISparseBlobPoolPeer? SelectTransactionRetryPeer(TrackedSparseBlobTx state, PendingCellsBuffer cells, ISparseBlobPoolPeer? transactionPeer)
    {
        for (int i = 0; i < cells.Sources.Length; i++)
        {
            PublicKey sourcePeerId = cells.Sources[i].PeerId;
            if (!state.AmbiguousFailureSources.Contains(sourcePeerId)
                && (transactionPeer is null || sourcePeerId != transactionPeer.Id)
                && _peers.TryGetValue(sourcePeerId, out ISparseBlobPoolPeer? peer)
                && CanRequestBody(state, peer))
            {
                return peer;
            }
        }

        return null;
    }

    private ISparseBlobPoolPeer? SelectTransactionRetryPeer(TrackedSparseBlobTx state)
    {
        ISparseBlobPoolPeer? first = null;
        bool afterLastAsked = state.LastBodyRequestPeer is null;
        foreach (PublicKey peerId in state.Announcements.Keys)
        {
            if (peerId == state.LastBodyRequestPeer)
            {
                afterLastAsked = true;
                continue;
            }
            if (!state.AmbiguousFailureSources.Contains(peerId)
                && _peers.TryGetValue(peerId, out ISparseBlobPoolPeer? peer) && CanRequestBody(state, peer))
            {
                if (afterLastAsked) return peer;
                first ??= peer;
            }
        }

        if (first is not null) return first;
        return state.LastBodyRequestPeer is { } lastPeerId
            && !state.AmbiguousFailureSources.Contains(lastPeerId)
            && _peers.TryGetValue(lastPeerId, out ISparseBlobPoolPeer? lastPeer) && CanRequestBody(state, lastPeer)
                ? lastPeer
                : null;
    }

    private ISparseBlobPoolPeer? PlanBodyRetry(TrackedSparseBlobTx state, out ExceptionDispatchInfo? failure,
        PendingCellsBuffer? cells = null, ISparseBlobPoolPeer? transactionPeer = null)
    {
        failure = null;
        if (state.TransactionRetryPeers is not null)
        {
            if (state.TransactionRetryAttempts >= MaxTransactionRetryAttempts)
            {
                state.BodyRetryDueAt = null;
                return null;
            }
            if (state.CurrentBodyAttempt is not null || state.BodyRetrySending)
            {
                state.BodyRetryDueAt ??= DateTimeOffset.MinValue;
                return null;
            }
        }

        state.BodyRetryDueAt = DateTimeOffset.MinValue;
        try
        {
            ISparseBlobPoolPeer? peer;
            if (state.TransactionRetryPeers is { } triedPeers)
            {
                peer = SelectUntriedTransactionRetryPeer(state, triedPeers);
            }
            else
            {
                peer = cells is { } buffered ? SelectTransactionRetryPeer(state, buffered, transactionPeer) : null;
                if (transactionPeer is null) peer ??= SelectTransactionRetryPeer(state);
            }

            state.BodyRetryDueAt = _timestamper.UtcNowOffset + (peer is null ? AmbiguousBodyRetryDelay : Timeouts.Eth);
            return peer;
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
            return null;
        }
    }

    private static bool CanRequestBody(TrackedSparseBlobTx state, ISparseBlobPoolPeer peer)
        => !state.BodyUnavailableSources.Contains(peer.Id) && peer.CanRequestTransactions && !peer.IsClosing;

    private void SendBodyRetry(Hash256 hash, TrackedSparseBlobTx state, ISparseBlobPoolPeer? peer)
    {
        HashSet<PublicKey>? failedPeers = null;
        while (peer is not null)
        {
            HashSet<PublicKey>? triedPeers;
            long attempt;
            lock (state.Lock)
            {
                if (!IsCurrentState(hash, state) || state.Transaction is not null || state.Submitted) return;

                triedPeers = state.TransactionRetryPeers;
                if (triedPeers is not null)
                {
                    if (state.CurrentBodyAttempt is not null || state.BodyRetrySending
                        || state.TransactionRetryAttempts >= MaxTransactionRetryAttempts || triedPeers.Contains(peer.Id)
                        || !IsActivePeer(peer) || !CanRequestBody(state, peer)) return;
                    state.BodyRetrySending = true;
                }

                attempt = ++state.BodyRequestAttempt;
            }

            bool sent = false;
            try
            {
                sent = peer.TrySendPooledTransactionRequest(hash);
            }
            finally
            {
                lock (state.Lock)
                {
                    if (triedPeers is not null) state.BodyRetrySending = false;
                    if (IsCurrentState(hash, state))
                    {
                        if (sent && attempt > state.LastSentBodyRequestAttempt
                            && (state.CurrentBodyAttemptPeer is null || state.CurrentBodyAttemptPeer == peer.Id))
                        {
                            state.LastSentBodyRequestAttempt = attempt;
                            state.LastBodyRequestPeer = peer.Id;
                        }
                        if (triedPeers is not null && ReferenceEquals(state.TransactionRetryPeers, triedPeers))
                        {
                            if (sent)
                            {
                                triedPeers.Add(peer.Id);
                                state.TransactionRetryAttempts++;
                                state.TransactionRetryResumeAfter = null;
                                state.BodyRetryDueAt = state.TransactionRetryAttempts >= MaxTransactionRetryAttempts
                                    ? null
                                    : _timestamper.UtcNowOffset + _cellRequestTimeout;
                            }
                            else
                            {
                                state.TransactionRetryResumeAfter = peer.Id;
                            }
                        }
                    }
                }
            }

            if (sent || triedPeers is null) return;
            (failedPeers ??= []).Add(peer.Id);
            if (failedPeers.Count >= MaxTransactionRetryAttempts) return;
            lock (state.Lock)
            {
                if (!ReferenceEquals(state.TransactionRetryPeers, triedPeers)) return;
                peer = SelectUntriedTransactionRetryPeer(state, triedPeers, failedPeers);
            }
        }
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
            && usage.RestorationTickets == 0
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

    private TimeSpan GetAdmissionDelay(in ValueHash256 hash)
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

    private readonly record struct CustodyUpdateRequest(SparseBlobPoolPeerRegistry Registry)
        : IBackgroundTaskRequest<CustodyUpdateRequest>;

    private readonly record struct MaintenanceSweepRequest(SparseBlobPoolPeerRegistry Registry)
        : IBackgroundTaskRequest<MaintenanceSweepRequest>;

    private readonly record struct PeerCleanupAction(
        Hash256 Hash,
        TrackedSparseBlobTx State,
        ISparseBlobPoolPeer? TransactionRetryPeer,
        BlobCellMask RetryMask,
        bool Submit);

    private sealed class PeerUsage
    {
        public Dictionary<ValueHash256, int> AnnouncedHashes { get; } = [];
        public SortedDictionary<long, ValueHash256> AnnouncedStates { get; } = [];
        public Dictionary<ValueHash256, int> TrackedHashes { get; } = [];
        public int Announcements { get; set; }
        public int RetainedAnnouncements { get; set; }
        public int RestorationTickets { get; set; }
        public long EarlyCellBytes { get; set; }
        public long TransactionBytes { get; set; }
        public int InFlightCellWork { get; set; }
    }

    private sealed class ClaimedCellReservation
    {
        public int HoldCount;
        public BlobCellMask Mask;
        public BlobCellMask CompletedMask;
        public bool MutationValid = true;
    }

    private bool MatchesCellRequestOwnership(IDisposable? ownership, ISparseBlobPoolPeer peer, TrackedSparseBlobTx state)
        => ownership is null || ownership is ClaimedCellLease lease && lease.Matches(this, state, peer);

    private sealed class MissingCellLease : ICellRequestOwnership
    {
        public bool IsCurrent => false;
        public void Dispose() { }
    }

    private sealed class ClaimedCellLease(SparseBlobPoolPeerRegistry owner, TrackedSparseBlobTx state, PublicKey peerId, long revision, ISparseBlobPoolPeer originalPeer) : ICellRequestOwnership
    {
        private SparseBlobPoolPeerRegistry? _owner = owner;

        public bool IsCurrent
        {
            get
            {
                SparseBlobPoolPeerRegistry? currentOwner = Volatile.Read(ref _owner);
                if (currentOwner is null) return false;
                lock (state.Lock) return Matches(currentOwner, state, originalPeer);
            }
        }

        public bool Matches(SparseBlobPoolPeerRegistry expectedOwner, TrackedSparseBlobTx expectedState, ISparseBlobPoolPeer expectedPeer)
            => ReferenceEquals(Volatile.Read(ref _owner), expectedOwner)
                && ReferenceEquals(state, expectedState) && ReferenceEquals(originalPeer, expectedPeer)
                && expectedOwner._peers.TryGetValue(peerId, out ISparseBlobPoolPeer? registeredPeer) && ReferenceEquals(registeredPeer, originalPeer)
                && expectedOwner.IsCurrentState(state.Hash, state)
                && state.ClaimedCellReservations?.TryGetValue((peerId, revision), out ClaimedCellReservation? claimed) == true
                && claimed.MutationValid
                && (!state.InFlightRevisionByPeer.TryGetValue(peerId, out long currentRevision) || currentRevision == revision);

        public void Complete(BlobCellMask mask) => Volatile.Read(ref _owner)?.CompleteClaimedCells(state, peerId, revision, mask);

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseClaimedCells(state, peerId, revision);
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
        public Dictionary<PublicKey, BlobCellMask> Announcements { get; } = [];
        public Dictionary<PublicKey, AnnouncementRestoration> RestorationTickets { get; } = [];
        public HashSet<PublicKey> AmbiguousFailureSources { get; } = [];
        public HashSet<PublicKey> BodyUnavailableSources { get; } = [];
        public Queue<PublicKey>? BodyUnavailableSourceOrder { get; set; }
        public Queue<PublicKey>? AmbiguousFailureSourceOrder { get; set; }
        public PublicKey? AmbiguousBodySource { get; set; }
        public DateTimeOffset? BodyRetryDueAt { get; set; }
        public PublicKey? LastBodyRequestPeer { get; set; }
        public long NextBodyAttempt { get; set; }
        public long LastConfirmedBodyAttempt { get; set; }
        public BodyRequestAttempt? CurrentBodyAttempt { get; set; }
        public PublicKey? CurrentBodyAttemptPeer { get; set; }
        public long BodyRequestAttempt { get; set; }
        public long LastSentBodyRequestAttempt { get; set; }
        public bool BodyRetrySending { get; set; }
        public Dictionary<PublicKey, BlobCellMask> InFlightByPeer { get; } = [];
        public Dictionary<PublicKey, long> InFlightRevisionByPeer { get; } = [];
        public Transaction? Transaction { get; set; }
        public ISparseBlobPoolPeer? TransactionPeer { get; set; }
        public int TransactionBytes { get; set; }
        public PendingCellsBuffer? Cells { get; set; }
        public PendingCellsBuffer? AlternativeCells { get; set; }
        public DateTimeOffset? AdmissionDueAt { get; set; }
        public bool IsQueued { get; set; }
        public HashSet<PublicKey>? TransactionRetryPeers { get; set; }
        public int TransactionRetryAttempts { get; set; }
        public PublicKey? TransactionRetryResumeAfter { get; set; }
        public bool Submitted { get; set; }
        public bool Submitting { get; set; }
        public bool RemovalRequested { get; set; }
        public bool ApplyingRecordedCells { get; set; }
        public BlobCellMask InFlightMask { get; set; }
        public BlobCellMask PendingCellMask { get; set; }
        public Dictionary<PublicKey, DateTimeOffset> InFlightUntilByPeer { get; } = [];
        public Dictionary<PublicKey, PacketReceiptTracker.ExpiryGuard> InFlightReceiptGuards { get; } = [];
        public Dictionary<(PublicKey PeerId, long Revision), ClaimedCellReservation>? ClaimedCellReservations { get; set; }
        public HashSet<PublicKey> SingleProviderInFlightPeers { get; } = [];
        public long NextInFlightRevision { get; set; }
    }
}
