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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncFeed : SyncFeed<AccountsSyncBatch?>, IDisposable
    {
        private BlockHeader _bestHeader;
        private int _responsesCount;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly SnapProvider _snapProvider;

        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        public override bool IsMultiFeed => true;
        public override AllocationContexts Contexts => AllocationContexts.Snap;
        
        public SnapSyncFeed(ISyncModeSelector syncModeSelector, SnapProvider snapProvider, IBlockTree blockTree, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _snapProvider = snapProvider ?? throw new ArgumentNullException(nameof(snapProvider)); ;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }
        
        public override async Task<AccountsSyncBatch?> PrepareRequest()
        {
            try
            {
                AccountsSyncBatch request = new AccountsSyncBatch();
                request.Request = new(_bestHeader.StateRoot, _snapProvider.NextStartingHash, Keccak.MaxValue, _bestHeader.Number);

                _logger.Info($"{request.Request.RootHash}:{request.Request.StartingHash}:{request.Request.LimitHash}");

                return await Task.FromResult(request);
            }
            catch (Exception e)
            {
                _logger.Error("Error when preparing a batch", e);
                return await Task.FromResult<AccountsSyncBatch>(null);
            }
        }

        public override SyncResponseHandlingResult HandleResponse(AccountsSyncBatch? batch)
        {
            _logger.Info($"HANDLE RESPONSE:{batch.Response is not null}");

            if (batch == null)
            {
                if (_logger.IsError) _logger.Error("Received empty batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            if(batch.Response is null)
            {
                if (_logger.IsInfo) _logger.Info("SNAP peer not assigned to handle request");
                return SyncResponseHandlingResult.NoProgress;
            }

            _responsesCount++;
            _snapProvider.AddAccountRange(batch.Request.BlockNumber.Value, batch.Request.RootHash, batch.Request.StartingHash, batch.Response.PathAndAccounts, batch.Response.Proofs);


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
                   _bestHeader = _blockTree.BestSuggestedHeader;
                    if (_bestHeader == null || _bestHeader.Number == 0)
                    {
                        if (_logger.IsInfo) _logger.Info($"No Best Suggested Header available. Snap Sync not started.");

                        return;
                    }

                    if (_logger.IsInfo) _logger.Info($"Starting the snap data sync from the {_bestHeader.ToString(BlockHeader.Format.Short)} {_bestHeader.StateRoot} root");

                    Activate();
                }
            }
        }
    }
}
