//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Core.Crypto;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncFeed : SyncFeed<SnapSyncBatch?>, IDisposable
    {
        private readonly object _syncLock = new ();

        private const int AllowedInvalidResponses = 5;
        private readonly LinkedList<(PeerInfo peer, AddRangeResult result)> _resultLog = new();

        private const SnapSyncBatch EmptyBatch = null;

        private readonly ISyncModeSelector _syncModeSelector;
        private readonly ISnapProvider _snapProvider;

        private readonly ILogger _logger;
        public override bool IsMultiFeed => true;
        public override AllocationContexts Contexts => AllocationContexts.Snap;
        
        public SnapSyncFeed(ISyncModeSelector syncModeSelector, ISnapProvider snapProvider, IBlockTree blockTree, ILogManager logManager)
        {
            _syncModeSelector = syncModeSelector;
            _snapProvider = snapProvider;
            _logger = logManager.GetClassLogger();

            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }
        
        public override Task<SnapSyncBatch?> PrepareRequest()
        {
            try
            {
                (SnapSyncBatch request, bool finished) = _snapProvider.GetNextRequest();

                if (request == null)
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
            if (batch == null)
            {
                if (_logger.IsError) _logger.Error("Received empty batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            AddRangeResult result = AddRangeResult.OK;

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

                if (peer == null)
                {
                    return SyncResponseHandlingResult.NotAssigned;
                }
                else
                {
                    _logger.Trace($"SNAP - timeout {peer}");
                    return SyncResponseHandlingResult.LesserQuality;
                }
            }

            return AnalyzeResponsePerPeer(result, peer);
        }

        public SyncResponseHandlingResult AnalyzeResponsePerPeer(AddRangeResult result, PeerInfo peer)
        {
            if(peer == null)
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

                lock(_syncLock)
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

                return SyncResponseHandlingResult.OK;
            }
        }

        public void Dispose()
        {
            _syncModeSelector.Changed -= SyncModeSelectorOnChanged;
        }

        private void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (CurrentState == SyncFeedState.Dormant)
            {
                if ((e.Current & SyncMode.SnapSync) == SyncMode.SnapSync)
                {
                    if (_snapProvider.CanSync())
                    {
                        Activate();
                    }
                }
            }
        }
    }
}
