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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncFeed : SyncFeed<SnapSyncBatch?>, IDisposable
    {
        private const SnapSyncBatch EmptyBatch = null;
        private int _accountResponsesCount;
        private int _storageResponsesCount;
        private int _emptyRequestCount;
        private int _retriesCount;

        private readonly Pivot _pivot;
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

            _pivot = new(blockTree, logManager);

            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }
        
        public override Task<SnapSyncBatch?> PrepareRequest()
        {
            try
            {
                SnapSyncBatch request = new SnapSyncBatch();

                BlockHeader pivotHeader = _pivot.GetPivotHeader();

                (AccountRange accountRange, StorageRange storageRange) = _snapProvider.ProgressTracker.GetNextRequest(pivotHeader.Number, pivotHeader.StateRoot);

                if (storageRange != null)
                {
                    request.StorageRangeRequest = storageRange;

                    if (_storageResponsesCount == 1 || _storageResponsesCount > 0 && _storageResponsesCount % 100 == 0)
                    {
                        _logger.Info($"SNAP - ({_pivot.GetPivotHeader().StateRoot}) Responses:{_storageResponsesCount}, Slots:{Metrics.SyncedStorageSlots}");
                    }
                }
                else if (accountRange != null)
                {
                    request.AccountRangeRequest = accountRange;

                    if (_accountResponsesCount == 1 || _accountResponsesCount > 0 && _accountResponsesCount % 10 == 0)
                    {
                        _logger.Info($"SNAP - ({_pivot.GetPivotHeader().StateRoot}) Responses:{_accountResponsesCount}, Accounts:{Metrics.SyncedAccounts}, next request:{request.AccountRangeRequest.RootHash}:{request.AccountRangeRequest.StartingHash}:{request.AccountRangeRequest.LimitHash}");
                    }
                }
                else
                {
                    _emptyRequestCount++;
                    if(_emptyRequestCount % 100 == 0)
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
                if (batch.AccountRangeResponse.PathAndAccounts.Length == 0 && batch.AccountRangeResponse.Proofs.Length == 0)
                {
                    _logger.Warn($"GetAccountRange: Requested expired RootHash:{batch.AccountRangeRequest.RootHash}");
                }
                else
                {
                    _snapProvider.AddAccountRange(batch.AccountRangeRequest.BlockNumber.Value, batch.AccountRangeRequest.RootHash, batch.AccountRangeRequest.StartingHash, batch.AccountRangeResponse.PathAndAccounts, batch.AccountRangeResponse.Proofs);

                    if (batch.AccountRangeResponse.PathAndAccounts.Length > 0)
                    {
                        Interlocked.Add(ref Metrics.SyncedAccounts, batch.AccountRangeResponse.PathAndAccounts.Length);
                    }
                }

                _snapProvider.ProgressTracker.ReportRequestFinished(batch.AccountRangeRequest);

                _accountResponsesCount++;
            }
            else if(batch.StorageRangeResponse is not null)
            {
                if (batch.StorageRangeResponse.PathsAndSlots.Length == 0 && batch.StorageRangeResponse.Proofs.Length == 0)
                {
                    _logger.Warn($"GetStorageRange - expired BlockNumber:{batch.StorageRangeRequest.BlockNumber}, RootHash:{batch.StorageRangeRequest.RootHash}, (Accounts:{batch.StorageRangeRequest.Accounts.Count()}), {batch.StorageRangeRequest.StartingHash}");

                    _snapProvider.ProgressTracker.EnqueueAccountStorage(batch.StorageRangeRequest);
                }
                else
                {
                    int slotCount = 0;

                    int requestLength = batch.StorageRangeRequest.Accounts.Length;
                    int responseLength = batch.StorageRangeResponse.PathsAndSlots.Length;

                    for (int i = 0; i < requestLength; i++)
                    {
                        if (i < responseLength)
                        {
                            // only the last can have proofs
                            byte[][] proofs = null;
                            if (i == responseLength - 1)
                            {
                                proofs = batch.StorageRangeResponse.Proofs;
                            }

                            _snapProvider.AddStorageRange(batch.StorageRangeRequest.BlockNumber.Value, batch.StorageRangeRequest.Accounts[i], batch.StorageRangeRequest.Accounts[i].Account.StorageRoot, batch.StorageRangeRequest.StartingHash, batch.StorageRangeResponse.PathsAndSlots[i], proofs);

                            slotCount += batch.StorageRangeResponse.PathsAndSlots[i].Length;
                        }
                        else
                        {
                            _snapProvider.ProgressTracker.EnqueueAccountStorage(batch.StorageRangeRequest.Accounts[i]);
                        }
                    }

                    if (slotCount > 0)
                    {
                        Interlocked.Add(ref Metrics.SyncedStorageSlots, slotCount);
                    }
                }

                _storageResponsesCount++;
            }
            else
            {
                //if (_logger.IsInfo) _logger.Info("SNAP peer not assigned to handle request");

                // Retry
                if (batch.AccountRangeRequest != null)
                {
                    _snapProvider.ProgressTracker.ReportRequestFinished(batch.AccountRangeRequest);
                }
                else if (batch.StorageRangeRequest is not null)
                {
                    _snapProvider.ProgressTracker.EnqueueAccountStorage(batch.StorageRangeRequest);
                }

                _retriesCount++;
                if (_retriesCount % 10 == 0)
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
                    if (_pivot.GetPivotHeader() == null || _pivot.GetPivotHeader().Number == 0)
                    {
                        if (_logger.IsInfo) _logger.Info($"No Best Suggested Header available. Snap Sync not started.");

                        return;
                    }

                    if (_logger.IsInfo) _logger.Info($"Starting the SNAP data sync from the {_pivot.GetPivotHeader().ToString(BlockHeader.Format.Short)} {_pivot.GetPivotHeader().StateRoot} root");

                    Activate();
                }
            }
        }
    }
}
