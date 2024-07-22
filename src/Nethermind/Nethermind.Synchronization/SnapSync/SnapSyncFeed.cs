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
    public class SnapSyncFeed : SyncFeed<SnapSyncBatch?>, IDisposable
    {
        private readonly object _syncLock = new();

        private const int AllowedInvalidResponses = 5;
        private readonly LinkedList<(PeerInfo peer, AddRangeResult result)> _resultLog = new();

        private const SnapSyncBatch EmptyBatch = null;

        private readonly ISnapProvider _snapProvider;

        private readonly ILogger _logger;
        private bool _disposed = false;
        public override bool IsMultiFeed => true;
        public override AllocationContexts Contexts => AllocationContexts.Snap;

        public SnapSyncFeed(ISnapProvider snapProvider, ILogManager logManager)
        {
            _snapProvider = snapProvider;
            _logger = logManager.GetClassLogger();
        }

        public override Task<SnapSyncBatch?> PrepareRequest(CancellationToken token = default)
        {
            try
            {
                bool finished = _snapProvider.IsFinished(out SnapSyncBatch request);

                if (request is null)
                {
                    if (finished)
                    {
                        Finish();
                    }

                    return Task.FromResult(EmptyBatch);
                }

                return Task.FromResult(request);
            }
            catch (Exception e)
            {
                _logger.Error("Error when preparing a batch", e);
                return Task.FromResult(EmptyBatch);
            }
        }

        public override SyncResponseHandlingResult HandleResponse(SnapSyncBatch? batch, PeerInfo peer)
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
                    _snapProvider.RefreshAccounts(batch.AccountsToRefreshRequest, batch.AccountsToRefreshResponse);
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

        public SyncResponseHandlingResult AnalyzeResponsePerPeer(AddRangeResult result, PeerInfo peer)
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
                return SyncResponseHandlingResult.OK;
            }
            else
            {
                int allLastSuccess = 0;
                int allLastFailures = 0;
                int peerLastFailures = 0;

                lock (_syncLock)
                {
                    foreach (var item in _resultLog)
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

        public void Dispose()
        {
            _disposed = true;
        }

        public override void SyncModeSelectorOnChanged(SyncMode current)
        {
            if (_disposed) return;
            if (CurrentState == SyncFeedState.Dormant)
            {
                if ((current & SyncMode.SnapSync) == SyncMode.SnapSync)
                {
                    if (_snapProvider.CanSync())
                    {
                        Activate();
                    }
                }
            }
        }

        public override void Finish()
        {
            _snapProvider.Dispose();
            base.Finish();
        }

        public override bool IsFinished => _snapProvider.IsSnapGetRangesFinished();
    }
}
