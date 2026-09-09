// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncFeed(ISnapProvider snapProvider, ILogManager logManager, TimeSpan? stallWarningThreshold = null) : ISimpleSyncFeed<SnapSyncBatch>
    {
        private readonly Lock _syncLock = new();

        internal const int AllowedInvalidResponses = 5;
        private readonly LinkedList<(PeerInfo peer, AddRangeResult result)> _resultLog = new();
        // Guards the single-peer stale-pivot heuristic below: a pivot update wipes the result log, so a peer
        // that keeps failing while staying the allocator's favorite would re-trip the same path forever without
        // ever being punished. Holds the node the heuristic last fired for, cleared by the first useful range
        // response from anyone; only that same node failing through a second streak is treated as the offender.
        // Keyed on the node id rather than the PeerInfo instance, which the pool replaces on every reconnect -
        // a peer that drops and comes back between two streaks would otherwise start over as a first offender.
        private PublicKey? _stalePivotUpdateTrigger;

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

        private readonly ILogger _logger = logManager.GetClassLogger<SnapSyncFeed>();

        public async Task<SnapSyncBatch?> PrepareRequest(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    WarnIfStalled();

                    bool finished = _snapProvider.IsFinished(out SnapSyncBatch request);

                    if (request is not null)
                    {
                        return request;
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
                    OnRunEnded();
                    return EmptyBatch;
                }
                catch (Exception e)
                {
                    _logger.Error("Error when preparing a batch", e);
                }

                try
                {
                    await Task.Delay(50, token);
                }
                catch (OperationCanceledException)
                {
                    // Awaited outside the try above, so this is the one run-ending exit that leaves by throwing.
                    OnRunEnded();
                    throw;
                }
            }

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
            // A code response carries no AddRangeResult of its own, so it would otherwise read as range success.
            bool isRangeResult = true;
            bool responseHandled = false;

            try
            {
                if (batch.AccountRangeResponse is not null)
                {
                    result = _snapProvider.AddAccountRange(batch.AccountRangeRequest, batch.AccountRangeResponse);
                }
                else if (batch.StorageRangeResponse is not null)
                {
                    result = _snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse);
                }
                else if (batch.CodesResponse is not null)
                {
                    isRangeResult = false;
                    _snapProvider.AddCodes(batch.CodesRequest, batch.CodesResponse);
                }
                else if (batch.AccountsToRefreshResponse is not null)
                {
                    result = _snapProvider.RefreshAccounts(batch.AccountsToRefreshRequest, batch.AccountsToRefreshResponse);
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

                    _logger.Trace($"SNAP - timeout {peer}");
                    Interlocked.Increment(ref Metrics.SnapRequestTimeouts);
                    OnUnproductiveResponse("no response");
                    return SyncResponseHandlingResult.LesserQuality;
                }

                responseHandled = true;
            }
            finally
            {
                // The one release of the request the scheduler handed out. It must run after the handler,
                // or IsSnapGetRangesFinished could see empty queues and a zero count mid-scheduling.
                _snapProvider.ReleaseRequest(batch, responseHandled);
                batch.Dispose();
            }

            return AnalyzeResponsePerPeer(result, peer, isRangeResult);
        }

        public SyncResponseHandlingResult AnalyzeResponsePerPeer(AddRangeResult result, PeerInfo? peer) =>
            AnalyzeResponsePerPeer(result, peer, isRangeResult: true);

        /// <param name="isRangeResult">Whether <paramref name="result"/> reflects range work. A code response
        /// carries no <see cref="AddRangeResult"/> of its own and reads as OK even when it matched nothing, so it
        /// must not count as the useful progress that clears the repeat-offender guard.</param>
        public SyncResponseHandlingResult AnalyzeResponsePerPeer(AddRangeResult result, PeerInfo? peer, bool isRangeResult)
        {
            if (peer is null)
            {
                return SyncResponseHandlingResult.OK;
            }

            int maxSize = 10 * AllowedInvalidResponses;
            while (_resultLog.Count > maxSize)
            {
                lock (_syncLock)
                {
                    if (_resultLog.Count > 0)
                    {
                        _resultLog.RemoveLast();
                    }
                }
            }

            lock (_syncLock)
            {
                _resultLog.AddFirst((peer, result));
            }

            if (result == AddRangeResult.OK)
            {
                if (isRangeResult)
                {
                    lock (_syncLock)
                    {
                        _stalePivotUpdateTrigger = null;
                        _consecutiveUnproductiveResponses = 0;
                        _lastUnproductiveReason = NoUnproductiveReason;
                        Metrics.SnapConsecutiveUnproductiveResponses = 0;

                        // Zero means no run is under way. ReleaseRequest, in the finally above, is what lets
                        // IsFinished report the run over, so the dispatcher can end the run between there and
                        // here - and a timestamp written after that would be charged to the next run.
                        // Within a run this is always non-zero: WarnIfStalled starts the clock at the top of
                        // PrepareRequest, before any batch can be handed out.
                        if (_lastProductiveTimestamp != 0) _lastProductiveTimestamp = Stopwatch.GetTimestamp();
                    }
                }

                return SyncResponseHandlingResult.OK;
            }
            else
            {
                OnUnproductiveResponse(result.ToString());

                int allLastSuccess = 0;
                int allLastFailures = 0;
                int peerLastFailures = 0;
                bool seenOtherPeer = false;

                lock (_syncLock)
                {
                    // Scan the whole window first so the single-peer guard cannot fire
                    // prematurely when a healthy peer's entries sit further back in the log
                    // than the analyzed peer's recent failures.
                    foreach ((PeerInfo peer, AddRangeResult _) probe in _resultLog)
                    {
                        if (probe.peer != peer)
                        {
                            seenOtherPeer = true;
                            break;
                        }
                    }

                    foreach ((PeerInfo peer, AddRangeResult result) item in _resultLog)
                    {
                        if (item.result == AddRangeResult.OK)
                        {
                            allLastSuccess++;

                            if (item.peer == peer)
                            {
                                break;
                            }
                        }
                        else
                        {
                            allLastFailures++;

                            if (item.peer == peer)
                            {
                                peerLastFailures++;

                                if (peerLastFailures > AllowedInvalidResponses)
                                {
                                    // With a single peer in the entire window and no successes, the
                                    // failure stream is more likely a stale pivot than a misbehaving
                                    // peer — punishing the only available peer would stall sync. But when
                                    // the pivot was already updated for this exact reason and nothing has
                                    // succeeded since, the peer itself is the problem: without a punishment
                                    // the allocator keeps picking the same fastest-but-useless peer and the
                                    // heuristic loops forever on a wiped log.
                                    if (!seenOtherPeer && allLastSuccess == 0)
                                    {
                                        PublicKey? peerNodeId = peer.SyncPeer?.Node?.Id;
                                        bool repeatOffender = peerNodeId is not null && peerNodeId.Equals(_stalePivotUpdateTrigger);
                                        _stalePivotUpdateTrigger = peerNodeId;
                                        _snapProvider.UpdatePivot();

                                        _resultLog.Clear();

                                        if (repeatOffender)
                                        {
                                            if (_logger.IsDebug) _logger.Debug($"SNAP - peer kept failing across a pivot update, punishing:{peer}");
                                            return SyncResponseHandlingResult.LesserQuality;
                                        }

                                        break;
                                    }

                                    if (allLastFailures == peerLastFailures)
                                    {
                                        _logger.Trace($"SNAP - peer to be punished:{peer}");
                                        return SyncResponseHandlingResult.LesserQuality;
                                    }

                                    if (allLastSuccess == 0 && allLastFailures > peerLastFailures)
                                    {
                                        _snapProvider.UpdatePivot();

                                        _resultLog.Clear();

                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                if (result == AddRangeResult.ExpiredRootHash)
                {
                    return SyncResponseHandlingResult.NoProgress;
                }

                return SyncResponseHandlingResult.OK;
            }
        }

        /// <summary>Records a snap request that yielded no usable range.</summary>
        /// <remarks>
        /// A code response is not counted either way: it carries no <see cref="AddRangeResult"/>, so it neither
        /// restarts the clock nor advances the streak. The codes-only tail of a sync is bounded, so this cannot
        /// hold a warning open indefinitely.
        /// </remarks>
        /// <param name="reason">Why the response was unusable - an <see cref="AddRangeResult"/> name, or that none arrived.</param>
        private void OnUnproductiveResponse(string reason)
        {
            lock (_syncLock)
            {
                Metrics.SnapConsecutiveUnproductiveResponses = ++_consecutiveUnproductiveResponses;
                _lastUnproductiveReason = reason;
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
