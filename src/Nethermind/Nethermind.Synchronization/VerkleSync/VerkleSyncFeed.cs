// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.VerkleSync;

public class VerkleSyncFeed: SyncFeed<VerkleSyncBatch?>, IDisposable
{
    private readonly object _syncLock = new();

    private const int AllowedInvalidResponses = 5;
    private readonly LinkedList<(PeerInfo peer, AddRangeResult result)> _resultLog = new();

    private const VerkleSyncBatch EmptyBatch = null;

    private readonly IVerkleSyncProvider _syncProvider;

    private readonly ILogger _logger;
    private bool _disposed = false;
    public override bool IsMultiFeed => true;
    public override AllocationContexts Contexts => AllocationContexts.Verkle;

    public VerkleSyncFeed(IVerkleSyncProvider syncProvider, ILogManager logManager)
    {
        _syncProvider = syncProvider;
        _logger = logManager.GetClassLogger();
    }

    public override Task<VerkleSyncBatch?> PrepareRequest(CancellationToken token = default)
    {
        try
        {
            bool finished = _syncProvider.IsFinished(out VerkleSyncBatch? request);
            if (request is not null) return Task.FromResult(request);

            if (finished) Finish();
            return Task.FromResult(EmptyBatch);
        }
        catch (Exception e)
        {
            _logger.Error("Error when preparing a batch", e);
            return Task.FromResult(EmptyBatch);
        }
    }

    public override SyncResponseHandlingResult HandleResponse(VerkleSyncBatch? batch, PeerInfo? peer = null)
    {
        if (batch is null)
        {
            if (_logger.IsError) _logger.Error("Received empty batch as a response");
            return SyncResponseHandlingResult.InternalError;
        }

        AddRangeResult result = AddRangeResult.OK;

        if (batch.SubTreeRangeResponse is not null)
        {
            result = _syncProvider.AddSubTreeRange(batch.SubTreeRangeRequest, batch.SubTreeRangeResponse);
        }
        else if (batch.LeafToRefreshRequest is not null)
        {
            _syncProvider.RefreshLeafs(batch.LeafToRefreshRequest, batch.LeafToRefreshResponse);
        }
        else
        {
            _syncProvider.RetryRequest(batch);

            if (peer is null) return SyncResponseHandlingResult.NotAssigned;

            _logger.Trace($"VERKLE SYNC - timeout {peer}");
            return SyncResponseHandlingResult.LesserQuality;
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
                                    _syncProvider.UpdatePivot();

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

    public override void SyncModeSelectorOnChanged(SyncMode current)
    {
        if (_disposed) return;
        if (CurrentState == SyncFeedState.Dormant)
        {
            if ((current & SyncMode.VerkleSync) == SyncMode.VerkleSync)
            {
                if (_syncProvider.CanSync())
                {
                    Activate();
                }
            }
        }
    }

    public override bool IsFinished => _syncProvider.IsVerkleGetRangesFinished();

    public void Dispose()
    {
        _disposed = true;
    }
}
