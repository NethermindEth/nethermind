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

    private readonly ISyncModeSelector _syncModeSelector;
    private readonly IVerkleSyncProvider _syncProvider;
    private readonly ILogger _logger;

    private const VerkleSyncBatch EmptyBatch = null;

    public VerkleSyncFeed(ISyncModeSelector syncModeSelector, IVerkleSyncProvider syncProvider, ILogManager logManager)
    {
        _syncModeSelector = syncModeSelector;
        _syncProvider = syncProvider;

        _logger = logManager.GetClassLogger();

        _syncModeSelector.Changed += SyncModeSelectorOnChanged;
    }

    public override Task<VerkleSyncBatch?> PrepareRequest(CancellationToken token = default)
    {
        try
        {
            (VerkleSyncBatch request, bool finished) = _syncProvider.GetNextRequest();
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


    private const int AllowedInvalidResponses = 5;
    private readonly LinkedList<(PeerInfo peer, AddRangeResult result)> _resultLog = new();
    public SyncResponseHandlingResult AnalyzeResponsePerPeer(AddRangeResult result, PeerInfo? peer = null)
    {
        if (peer is null) return SyncResponseHandlingResult.OK;

        const int maxSize = 10 * AllowedInvalidResponses;

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

        if (result == AddRangeResult.OK) return SyncResponseHandlingResult.OK;

        int allLastSuccess = 0;
        int allLastFailures = 0;
        int peerLastFailures = 0;

        lock (_syncLock)
        {
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
                            if (allLastFailures == peerLastFailures)
                            {
                                _logger.Trace($"VERKLE SYNC - peer to be punished:{peer}");
                                return SyncResponseHandlingResult.LesserQuality;
                            }

                            if (allLastSuccess == 0 && allLastFailures > peerLastFailures)
                            {
                                // TODO: here try to get proofs and witnesses from block and update the state when
                                //   pivot changes - this will allow to get rid of healing
                                _syncProvider.UpdatePivot();
                                _resultLog.Clear();
                                break;
                            }
                        }
                    }
                }
            }
        }

        return result == AddRangeResult.ExpiredRootHash
            ? SyncResponseHandlingResult.NoProgress
            : SyncResponseHandlingResult.OK;
    }

    public override bool IsMultiFeed => true;
    public override AllocationContexts Contexts  => AllocationContexts.Verkle;

    private void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
    {
        if (CurrentState == SyncFeedState.Dormant)
        {
            if ((e.Current & SyncMode.VerkleSync) == SyncMode.VerkleSync)
            {
                if (_syncProvider.CanSync())
                {
                    Activate();
                }
            }
        }
    }

    public void Dispose()
    {
        _syncModeSelector.Changed -= SyncModeSelectorOnChanged;
    }
}
