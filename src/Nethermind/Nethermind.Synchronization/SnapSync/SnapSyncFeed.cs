// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncFeed(INodeStatsManager? nodeStatsManager, ISnapProvider snapProvider, ILogManager logManager,
        TimeSpan? stallWarningThreshold = null) : ISimpleSyncFeed<SnapSyncBatch>
    {
        public SnapSyncFeed(ISnapProvider snapProvider, ILogManager logManager, TimeSpan? stallWarningThreshold = null)
            : this(null, snapProvider, logManager, stallWarningThreshold)
        {
        }

        private readonly Lock _syncLock = new();

        internal const int AllowedInvalidResponses = 5;
        private readonly LinkedList<(PeerInfo peer, AddRangeResult result)> _resultLog = new();
        private ValueHash256? _unavailableRoot;
        private int _eligibleUnavailableResponses;

        // A snap request that produced nothing usable is otherwise recorded only at Trace (a timeout) or in a
        // detailed-only metric (a bad range), while ProgressTracker keeps reporting the same percentage - so a
        // node whose every request fails looks exactly like one that is working slowly. These three make the
        // stall itself sayable: how many requests it covers, and how long it has run. Written under _syncLock;
        // the only unlocked access is WarnIfStalled's fast-path read of the timestamp.
        private int _consecutiveUnproductiveResponses;
        private string _lastUnproductiveReason = NoUnproductiveReason;
        private long _lastProductiveTimestamp;
        private long _lastStallWarningTimestamp;

        private const string NoUnproductiveReason = "none recorded";
        private const string NoPeerReason = "no peer available";

        /// <summary>How long snap sync may go without storing a usable range before it is called a stall.</summary>
        /// <remarks>
        /// Also the interval between repeats. A healthy snap sync stores ranges continuously, so this only has to
        /// clear the pauses the recovery mechanisms themselves cause - punishing a peer, or a pivot update
        /// invalidating the in-flight requests. Overridable so tests do not have to wait it out.
        /// </remarks>
        private static readonly TimeSpan DefaultStallWarningThreshold = TimeSpan.FromMinutes(5);

        private readonly TimeSpan _stallWarningThreshold = stallWarningThreshold ?? DefaultStallWarningThreshold;

        private const SnapSyncBatch EmptyBatch = null;

        private readonly ISnapProvider _snapProvider = snapProvider;
        private readonly SnapRecoveryCoordinator _recovery = snapProvider is SnapProvider provider ? provider.Recovery : new(TimeProvider.System);

        internal SnapSyncFeed(INodeStatsManager nodeStatsManager, ISnapProvider snapProvider, ILogManager logManager,
            SnapRecoveryCoordinator recovery) : this(nodeStatsManager, snapProvider, logManager)
            => _recovery = recovery;

        private readonly ILogger _logger = logManager.GetClassLogger<SnapSyncFeed>();

        public async Task<SnapSyncBatch?> PrepareRequest(CancellationToken token)
        {
            SnapSyncBatch? pending = null;
            while (!token.IsCancellationRequested)
            {
                Task readiness = _recovery.ReadinessSignal;
                try
                {
                    WarnIfStalled();

                    bool finished = false;
                    if (pending is null)
                    {
                        finished = _snapProvider.IsFinished(out pending);
                        if (pending is not null)
                        {
                            SnapSyncBatch owned = pending;
                            owned.RecoveryContext = _recovery;
                            owned.ProviderContext = _snapProvider;
                            owned.SetTerminal(() =>
                            {
                                try { _snapProvider.ReleaseRequest(owned, false); }
                                finally { owned.Dispose(); }
                            });
                        }
                    }

                    if (pending is not null)
                    {
                        bool rooted = pending.AccountRangeRequest is not null || pending.StorageRangeRequest is not null || pending.AccountsToRefreshRequest is not null;
                        bool unknown = pending.StorageRangeRequest is { } storage && (_snapProvider is not SnapProvider provider || provider.IsUnknown(storage));
                        if (_recovery.CanPrepare(rooted, unknown, pending.RecoveryCohort)) return pending;
                        ((IDispatchTerminal)pending).CompleteDispatch();
                        pending = null;
                    }

                    if (finished)
                    {
                        _snapProvider.Dispose();
                        OnRunEnded();
                        return null;
                    }
                }
                catch (OperationCanceledException)
                {
                    (pending as IDispatchTerminal)?.CompleteDispatch();
                    OnRunEnded();
                    return EmptyBatch;
                }
                catch (Exception e)
                {
                    _logger.Error("Error when preparing a batch", e);
                }

                try
                {
                    await _recovery.WaitForReadiness(readiness,
                        pending?.CodesRequest is null && (pending is not null || _snapProvider is SnapProvider provider && provider.HasPendingRootWork), token);
                }
                catch (OperationCanceledException)
                {
                    (pending as IDispatchTerminal)?.CompleteDispatch();
                    // Awaited outside the try above, so this is the one run-ending exit that leaves by throwing.
                    OnRunEnded();
                    throw;
                }
            }

            (pending as IDispatchTerminal)?.CompleteDispatch();
            OnRunEnded();
            return EmptyBatch;
        }

        /// <summary>Ends the stall measurement at the end of a snap-sync run.</summary>
        /// <remarks>
        /// This feed outlives a single run: a reorg under the pivot during BAL healing discards the synced state
        /// and starts snap over on the same instance (<c>StateSyncRunner.RunSnapSyncWithBalHealing</c>). Between the
        /// two runs nothing stores a range, and healing can take far longer than the stall threshold, so a clock
        /// left running would make the next run's first request report a stall that never happened, and a streak
        /// left standing would make it name the previous run's failures. A null return from
        /// <see cref="PrepareRequest"/> is exactly where the dispatcher ends a run.
        /// </remarks>
        private void OnRunEnded()
        {
            lock (_syncLock)
            {
                _lastProductiveTimestamp = 0;
                _lastStallWarningTimestamp = 0;
                _consecutiveUnproductiveResponses = 0;
                _lastUnproductiveReason = NoUnproductiveReason;
                Metrics.SnapConsecutiveUnproductiveResponses = 0;
            }
        }

        public SyncResponseHandlingResult HandleResponse(SnapSyncBatch batch, PeerInfo? peer = null)
        {
            if (batch is null)
            {
                if (_logger.IsError) _logger.Error("Received empty batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            AddRangeResult result = AddRangeResult.OK;
            bool isRangeResult = true;
            bool madeStorageProgress = false;
            bool responseHandled = false;
            long usefulBytes = 0;
            bool neutral = false;
            bool rootedBytes = false;
            bool timedOut = false;
            Hash256? requestedRoot = batch.AccountRangeRequest?.RootHash ?? batch.StorageRangeRequest?.RootHash ?? batch.AccountsToRefreshRequest?.RootHash;

            // Held until the request is released below, so that a checkpoint never sees the response half handled.
            using IDisposable? responseScope = _snapProvider.BeginResponse();
            try
            {
                if (batch.LocalDeferred) return SyncResponseHandlingResult.NotAssigned;
                if (batch.AccountRangeResponse is not null)
                {
                    result = _snapProvider.AddAccountRange(batch.AccountRangeRequest, batch.AccountRangeResponse);
                    if (result == AddRangeResult.OK) usefulBytes = CountAccountBytes(batch.AccountRangeResponse);
                }
                else if (batch.StorageRangeResponse is not null)
                {
                    if (_snapProvider is SnapProvider provider)
                    {
                        result = provider.AddStorageRangeWithResult(batch.StorageRangeRequest, batch.StorageRangeResponse, out usefulBytes, out neutral, out rootedBytes,
                            batch.DispatchEvidence, batch.RecoveryLease);
                    }
                    else
                    {
                        result = _snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse, out madeStorageProgress);
                        if (result == AddRangeResult.OK)
                        {
                            foreach (IOwnedReadOnlyList<PathWithStorageSlot> slots in batch.StorageRangeResponse.PathsAndSlots)
                            {
                                foreach (PathWithStorageSlot slot in slots) usefulBytes += 32 + slot.SlotRlpValue.Length;
                            }
                        }
                    }
                }
                else if (batch.CodesResponse is not null)
                {
                    isRangeResult = false;
                    (int matched, long bytes) = AddCodes(batch.CodesRequest, batch.CodesResponse, out bool invalidResponse);
                    usefulBytes = bytes;
                    result = invalidResponse || matched == 0 ? AddRangeResult.InvalidProof : AddRangeResult.OK;
                }
                else if (batch.AccountsToRefreshResponse is not null)
                {
                    result = _snapProvider.RefreshAccounts(batch.AccountsToRefreshRequest, batch.AccountsToRefreshResponse);
                    if (result == AddRangeResult.OK) usefulBytes = CountAccountBytes(batch.AccountsToRefreshResponse);
                }
                else
                {
                    if (peer is null)
                    {
                        // SimpleDispatcher hands the batch straight back when the pool could not allocate one.
                        // No peer means no range either, so it counts: otherwise a sync that cannot get a peer at
                        // all leaves the streak and the reason frozen at whatever the last answered request left,
                        // and the gauge reading zero for the whole stall.
                        OnUnproductiveResponse(NoPeerReason);
                        return SyncResponseHandlingResult.NotAssigned;
                    }

                    _logger.Trace($"SNAP - timeout {peer:hide}");
                    Interlocked.Increment(ref Metrics.SnapRequestTimeouts);
                    OnUnproductiveResponse("no response");
                    timedOut = true;
                    return SyncResponseHandlingResult.LesserQuality;
                }

                responseHandled = true;
                bool rootEligible = requestedRoot is not null && requestedRoot == _recovery.CurrentRoot;
                bool retired = result == AddRangeResult.ExpiredRootHash && _recovery.TracksPivot && !rootEligible;
                batch.RecoveryLease?.Classify(neutral, retired);
                if (retired) neutral = true;
                if (isRangeResult && _recovery.TracksPivot)
                {
                    bool verifiedRecovery = (result == AddRangeResult.OK || neutral) && usefulBytes > 0 &&
                        (batch.StorageRangeRequest is null || rootedBytes);
                    ObserveRootAvailability(requestedRoot, rootEligible && !neutral && result == AddRangeResult.ExpiredRootHash,
                        rootEligible && verifiedRecovery);
                }
                if (neutral)
                {
                    if (usefulBytes > 0) OnProductiveResponse();
                    return SyncResponseHandlingResult.Emptish;
                }
            }
            finally
            {
                if (timedOut) batch.RecoveryLease?.Classify(false, false);
                if (peer is not null && (timedOut || responseHandled && (usefulBytes > 0 || !neutral && result != AddRangeResult.OK)))
                {
                    nodeStatsManager?.ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges,
                        usefulBytes / Math.Max(1, batch.ResponseMilliseconds));
                }
                // The one release of the request the scheduler handed out. It must run after the handler,
                // or IsSnapGetRangesFinished could see empty queues and a zero count mid-scheduling.
                batch.Handled();
                try { _snapProvider.ReleaseRequest(batch, responseHandled, responseHandled && isRangeResult && (result == AddRangeResult.OK || usefulBytes > 0 || madeStorageProgress)); }
                finally
                {
                    batch.Dispose();
                    if (!batch.DispatchOwned) ((IDispatchTerminal)batch).CompleteDispatch();
                }
            }

            return AnalyzeResponsePerPeer(result, peer, isRangeResult,
                unavailable: result == AddRangeResult.ExpiredRootHash && _recovery.TracksPivot,
                productiveRange: usefulBytes > 0 || madeStorageProgress);
        }

        private void ObserveRootAvailability(Hash256? requestedRoot, bool unavailable, bool recovered)
        {
            lock (_syncLock)
            {
                ValueHash256? current = _recovery.CurrentRoot?.ValueHash256;
                if (_unavailableRoot != current)
                {
                    _unavailableRoot = current;
                    _eligibleUnavailableResponses = 0;
                }
                if (requestedRoot?.ValueHash256 != current) return;
                if (recovered) _eligibleUnavailableResponses = 0;
                else if (unavailable)
                {
                    _eligibleUnavailableResponses++;
                    if (_eligibleUnavailableResponses % (AllowedInvalidResponses + 1) == 0)
                    {
                        _snapProvider.UpdatePivot();
                        ValueHash256? updated = _recovery.CurrentRoot?.ValueHash256;
                        if (updated != _unavailableRoot)
                        {
                            _unavailableRoot = updated;
                            _eligibleUnavailableResponses = 0;
                        }
                    }
                }
            }
        }

        private static long CountAccountBytes(AccountsAndProofs response)
        {
            long bytes = 0;
            foreach (PathWithAccount account in response.PathAndAccounts)
            {
                bytes += 32 + AccountDecoder.Instance.GetLength(account.Account);
            }
            return bytes;
        }

        private (int Matched, long Bytes) AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IByteArrayList codes, out bool invalidResponse)
        {
            if (_snapProvider is SnapProvider provider) return provider.AddCodesWithResult(requestedHashes, codes, out invalidResponse);

            invalidResponse = false;
            HashSet<ValueHash256> requested = [.. requestedHashes];
            HashSet<ValueHash256> remaining = [.. requestedHashes];
            int matched = 0;
            long bytes = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                ValueHash256 hash = ValueKeccak.Compute(codes[i]);
                if (!requested.Contains(hash)) invalidResponse = true;
                else if (remaining.Remove(hash))
                {
                    matched++;
                    bytes += codes[i].Length;
                }
            }
            _snapProvider.AddCodes(requestedHashes, codes);
            return (matched, bytes);
        }

        public SyncResponseHandlingResult AnalyzeResponsePerPeer(AddRangeResult result, PeerInfo? peer) =>
            AnalyzeResponsePerPeer(result, peer, isRangeResult: true);

        /// <param name="isRangeResult">Whether <paramref name="result"/> reflects range work. Code replies are
        /// scored separately and cannot reset range failure history.</param>
        public SyncResponseHandlingResult AnalyzeResponsePerPeer(AddRangeResult result, PeerInfo? peer, bool isRangeResult) =>
            AnalyzeResponsePerPeer(result, peer, isRangeResult, unavailable: false, productiveRange: result == AddRangeResult.OK);

        internal SyncResponseHandlingResult AnalyzeResponsePerPeer(AddRangeResult result, PeerInfo? peer,
            bool isRangeResult, bool unavailable, bool productiveRange)
        {
            if (!isRangeResult)
            {
                if (result == AddRangeResult.OK) return SyncResponseHandlingResult.OK;
                OnUnproductiveResponse("unmatched codes");
                return SyncResponseHandlingResult.LesserQuality;
            }
            if (productiveRange)
            {
                OnProductiveResponse();
                if (peer is not null && result == AddRangeResult.OK) RecordRangeResult(peer, AddRangeResult.OK);
            }
            if (result == AddRangeResult.OK) return SyncResponseHandlingResult.OK;
            if (!productiveRange) OnUnproductiveResponse(result.ToString());
            if (peer is null) return SyncResponseHandlingResult.NotAssigned;
            // Only the request handler can establish unavailable-root evidence.
            AddRangeResult attributed = unavailable ? AddRangeResult.ExpiredRootHash : AddRangeResult.InvalidProof;
            lock (_syncLock)
            {
                RecordRangeResult(peer, attributed);
                int failures = 0;
                foreach ((PeerInfo owner, AddRangeResult entry) in _resultLog)
                {
                    if (!SamePeer(owner, peer)) continue;
                    if (entry == AddRangeResult.OK) break;
                    if ((entry == AddRangeResult.ExpiredRootHash) == unavailable) failures++;
                }
                int threshold = unavailable ? 2 * (AllowedInvalidResponses + 1) : AllowedInvalidResponses + 1;
                return failures >= threshold ? SyncResponseHandlingResult.LesserQuality : SyncResponseHandlingResult.NoProgress;
            }
        }

        private void RecordRangeResult(PeerInfo peer, AddRangeResult result)
        {
            lock (_syncLock)
            {
                _resultLog.AddFirst((peer, result));
                if (_resultLog.Count > 10 * AllowedInvalidResponses) _resultLog.RemoveLast();
            }
        }

        private static bool SamePeer(PeerInfo first, PeerInfo second)
        {
            PublicKey? firstId = first.SyncPeer.Node?.Id;
            PublicKey? secondId = second.SyncPeer.Node?.Id;
            return firstId is not null && secondId is not null ? firstId.Equals(secondId) : ReferenceEquals(first, second);
        }

        private void OnProductiveResponse()
        {
            lock (_syncLock)
            {
                _consecutiveUnproductiveResponses = 0;
                _lastUnproductiveReason = NoUnproductiveReason;
                Metrics.SnapConsecutiveUnproductiveResponses = 0;
                if (_lastProductiveTimestamp != 0) _lastProductiveTimestamp = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>Records a snap request that yielded no usable range.</summary>
        /// <param name="reason">Why the response was unusable - an <see cref="AddRangeResult"/> name, or that none arrived.</param>
        private int OnUnproductiveResponse(string reason)
        {
            lock (_syncLock)
            {
                Metrics.SnapConsecutiveUnproductiveResponses = ++_consecutiveUnproductiveResponses;
                _lastUnproductiveReason = reason;
                return _consecutiveUnproductiveResponses;
            }
        }

        /// <summary>
        /// Warns, rate-limited, once snap sync has gone <see cref="DefaultStallWarningThreshold"/> without storing a
        /// usable range.
        /// </summary>
        /// <remarks>
        /// Driven from the request side rather than the response side on purpose. A stall does not necessarily
        /// produce responses to count: a request that stays in flight and is neither answered nor timed out leaves
        /// every response-driven counter frozen, so no streak of unproductive responses can reach any threshold.
        /// Elapsed time since the last stored range covers that as well as the every-request-fails shape.
        /// <para>
        /// Only time within one snap-sync run counts. The clock starts at the first request of a run rather than at
        /// construction, and <see cref="OnRunEnded"/> stops it when the run finishes.
        /// </para>
        /// </remarks>
        private void WarnIfStalled()
        {
            // Runs once per snap request, and is a no-op on a healthy node, so the common case stays off the lock
            // that AnalyzeResponsePerPeer holds while walking its result log.
            long lastProductive = Volatile.Read(ref _lastProductiveTimestamp);
            if (lastProductive != 0 && Stopwatch.GetElapsedTime(lastProductive) < _stallWarningThreshold) return;

            int streak;
            string reason;
            TimeSpan stalledFor;
            lock (_syncLock)
            {
                long now = Stopwatch.GetTimestamp();
                // Zero means no run is under way yet, or the last one ended: start the clock here rather than
                // charging this run for the gap since the previous one stored a range.
                if (_lastProductiveTimestamp == 0)
                {
                    _lastProductiveTimestamp = now;
                    return;
                }

                stalledFor = Stopwatch.GetElapsedTime(_lastProductiveTimestamp, now);
                if (stalledFor < _stallWarningThreshold) return;
                if (_lastStallWarningTimestamp != 0
                    && Stopwatch.GetElapsedTime(_lastStallWarningTimestamp, now) < _stallWarningThreshold)
                {
                    return;
                }

                _lastStallWarningTimestamp = now;
                streak = _consecutiveUnproductiveResponses;
                reason = _lastUnproductiveReason;
            }

            if (_logger.IsWarn)
            {
                _logger.Warn($"Snap sync has not stored a usable range for {stalledFor.TotalMinutes:N1} min " +
                             $"({streak} unproductive responses, most recent: {reason}). The state percentage " +
                             "will not move until a peer answers with a range at the current pivot.");
            }
        }
    }
}
