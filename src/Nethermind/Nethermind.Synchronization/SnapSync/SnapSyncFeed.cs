// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncFeed(ISnapProvider snapProvider, ILogManager logManager) : ISimpleSyncFeed<SnapSyncBatch>
    {
        private readonly Lock _syncLock = new();

        internal const int AllowedInvalidResponses = 5;
        private readonly LinkedList<(PeerInfo peer, AddRangeResult result)> _resultLog = new();
        // Guards the single-peer stale-pivot heuristic below: a pivot update wipes the result log, so a peer
        // that keeps failing while staying the allocator's favorite would re-trip the same path forever without
        // ever being punished. Holds the peer the heuristic last fired for, cleared by the first useful
        // response from anyone; only the same peer failing through a second streak is treated as the offender.
        private PeerInfo? _stalePivotUpdateTrigger;

        private const SnapSyncBatch EmptyBatch = null;

        private readonly ISnapProvider _snapProvider = snapProvider;

        private readonly ILogger _logger = logManager.GetClassLogger<SnapSyncFeed>();

        public async Task<SnapSyncBatch?> PrepareRequest(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool finished = _snapProvider.IsFinished(out SnapSyncBatch request);

                    if (request is not null)
                    {
                        return request;
                    }

                    if (finished)
                    {
                        _snapProvider.Dispose();
                        return null;
                    }
                }
                catch (OperationCanceledException)
                {
                    return EmptyBatch;
                }
                catch (Exception e)
                {
                    _logger.Error("Error when preparing a batch", e);
                }

                await Task.Delay(50, token);
            }

            return EmptyBatch;
        }

        public SyncResponseHandlingResult HandleResponse(SnapSyncBatch batch, PeerInfo? peer = null)
        {
            if (batch is null)
            {
                if (_logger.IsError) _logger.Error("Received empty batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            AddRangeResult result = AddRangeResult.OK;

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
                    _snapProvider.AddCodes(batch.CodesRequest, batch.CodesResponse);
                }
                else if (batch.AccountsToRefreshResponse is not null)
                {
                    result = _snapProvider.RefreshAccounts(batch.AccountsToRefreshRequest, batch.AccountsToRefreshResponse);
                }
                else
                {
                    _snapProvider.RetryRequest(batch);

                    if (peer is null)
                    {
                        return SyncResponseHandlingResult.NotAssigned;
                    }
                    else
                    {
                        _logger.Trace($"SNAP - timeout {peer}");
                        return SyncResponseHandlingResult.LesserQuality;
                    }
                }
            }
            finally
            {
                batch.Dispose();
            }

            return AnalyzeResponsePerPeer(result, peer);
        }

        public SyncResponseHandlingResult AnalyzeResponsePerPeer(AddRangeResult result, PeerInfo? peer)
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
                lock (_syncLock)
                {
                    _stalePivotUpdateTrigger = null;
                }

                return SyncResponseHandlingResult.OK;
            }
            else
            {
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
                                        bool repeatOffender = ReferenceEquals(_stalePivotUpdateTrigger, peer);
                                        _stalePivotUpdateTrigger = peer;
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
    }
}
