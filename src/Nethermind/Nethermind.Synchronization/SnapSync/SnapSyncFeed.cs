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

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncFeed : SyncFeed<SnapSyncBatch?>, IDisposable
    {
        private const SnapSyncBatch EmptyBatch = null;
        private int _emptyRequestCount;
        private int _retriesCount;

        private readonly ISyncModeSelector _syncModeSelector;
        private readonly ISnapProvider _snapProvider;

        private readonly ILogger _logger;
        public override bool IsMultiFeed => true;
        public override AllocationContexts Contexts => AllocationContexts.Snap;
        
        public SnapSyncFeed(ISyncModeSelector syncModeSelector, ISnapProvider snapProvider, IBlockTree blockTree, ILogManager logManager)
        {
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _snapProvider = snapProvider ?? throw new ArgumentNullException(nameof(snapProvider)); ;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

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

                    _emptyRequestCount++;
                    if (_emptyRequestCount % 100 == 0)
                    {
                        _logger.Info($"SNAP - emptyRequestCount:{_emptyRequestCount}");
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

        public override SyncResponseHandlingResult HandleResponse(SnapSyncBatch? batch)
        {
            if (batch == null)
            {
                if (_logger.IsError) _logger.Error("Received empty batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            if (batch.AccountRangeResponse is not null)
            {
                _snapProvider.AddAccountRange(batch.AccountRangeRequest, batch.AccountRangeResponse);
            }
            else if(batch.StorageRangeResponse is not null)
            {
                _snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse);
            }
            else if(batch.CodesResponse is not null)
            {
                 _snapProvider.AddCodes(batch.CodesRequest, batch.CodesResponse);
            }
            else
            {
                _snapProvider.RetryRequest(batch);

                _retriesCount++;
                if (_retriesCount % 100 == 0)
                {
                    _logger.Info($"SNAP - retriesCount:{_retriesCount}");
                }

                // Other option - What if the peer didn't respond? Timeout
                return SyncResponseHandlingResult.NotAssigned;
                //return SyncResponseHandlingResult.NoProgress;
            }

            return SyncResponseHandlingResult.OK;
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
