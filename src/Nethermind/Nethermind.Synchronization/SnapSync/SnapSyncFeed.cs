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
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync.State;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncFeed : SyncFeed<AccountsSyncBatch?>, IDisposable
    {
        private readonly ISyncModeSelector _syncModeSelector;
        //private readonly ISnapStateProvider _stateProvider;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        public override bool IsMultiFeed => true;
        public override AllocationContexts Contexts => AllocationContexts.State;
        
        public SnapSyncFeed(ISyncModeSelector syncModeSelector, ISnapStateProvider stateProvider, IBlockTree blockTree, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            //_stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }
        
        public override async Task<AccountsSyncBatch?> PrepareRequest()
        {
            AccountsSyncBatch request = new AccountsSyncBatch(); // { Request = _stateProvider.GetNextAccountRange() };
            return await Task.FromResult(request);
        }

        public override SyncResponseHandlingResult HandleResponse(AccountsSyncBatch? batch)
        {
            if (batch.Response == null)
            {
                if(_logger.IsError) _logger.Error("Received empty response");
                return SyncResponseHandlingResult.InternalError;
            }
            else
            {
                //_stateProvider.AddAccounts(batch.Response);
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
                    BlockHeader bestSuggested = _blockTree.BestSuggestedHeader;
                    if (bestSuggested == null || bestSuggested.Number == 0)
                    {
                        return;
                    }

                    if (_logger.IsInfo) _logger.Info($"Starting the node data sync from the {bestSuggested.ToString(BlockHeader.Format.Short)} {bestSuggested.StateRoot} root");
                    // ResetStateRoot(bestSuggested.Number, bestSuggested.StateRoot!);
                    Activate();
                }
            }
        }
    }
}
