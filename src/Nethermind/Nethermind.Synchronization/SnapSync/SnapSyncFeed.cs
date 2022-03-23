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
        private const int STORAGE_BATCH_SIZE = 2;

        private int _accountResponsesCount;
        private int _storageResponsesCount;

        private BlockHeader _bestHeader;
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
        
        public override async Task<SnapSyncBatch?> PrepareRequest()
        {
            try
            {
                SnapSyncBatch request = new SnapSyncBatch();

                if(_snapProvider.NextSlot.HasValue)
                {
                    request.StorageRangeRequest = new()
                    {
                        RootHash = _bestHeader.StateRoot,
                        Accounts = new PathWithAccount[] { _snapProvider.NextSlot.Value.accountPath},
                        StartingHash = _snapProvider.NextSlot.Value.nextSlotPath,
                        BlockNumber = _bestHeader.Number
                    };

                    // TODO: make it thread safe and handle retry
                    _snapProvider.NextSlot = null;
                }
                else if(_snapProvider.StoragesToRetrieve.Count > 0)
                {
                    // TODO: optimize this
                    List<PathWithAccount> storagesToQuery = storagesToQuery = new(STORAGE_BATCH_SIZE);

                    for (int i = 0; i < STORAGE_BATCH_SIZE && _snapProvider.StoragesToRetrieve.TryDequeue(out PathWithAccount storage); i++)
                    {
                        storagesToQuery.Add(storage);
                    }

                    request.StorageRangeRequest = new()
                    {
                        RootHash = _bestHeader.StateRoot,
                        Accounts = storagesToQuery.ToArray(),
                        StartingHash = Keccak.Zero,
                        BlockNumber = _bestHeader.Number
                    };

                    if (_storageResponsesCount == 1 || _storageResponsesCount > 0 && _storageResponsesCount % 10 == 0)
                    {
                        _logger.Info($"SNAP - Responses:{_storageResponsesCount}, Slots:{Metrics.SyncedStorageSlots}");
                    }
                }
                else
                {
                    // some contract hardcoded
                    //var path = Keccak.Compute(new Address("0x4c9A3f79801A189D98D3a5A18dD5594220e4d907").Bytes);
                    // = new(_bestHeader.StateRoot, path, path, _bestHeader.Number);

                    request.AccountRangeRequest = new(_bestHeader.StateRoot, _snapProvider.NextAccountPath, Keccak.MaxValue, _bestHeader.Number);

                    if (_accountResponsesCount == 1 || _accountResponsesCount > 0 && _accountResponsesCount % 10 == 0)
                    {
                        _logger.Info($"SNAP - Responses:{_accountResponsesCount}, Accounts:{Metrics.SyncedAccounts}, next request:{request.AccountRangeRequest.RootHash}:{request.AccountRangeRequest.StartingHash}:{request.AccountRangeRequest.LimitHash}");
                    }
                }


                return await Task.FromResult(request);
            }
            catch (Exception e)
            {
                _logger.Error("Error when preparing a batch", e);
                return await Task.FromResult<SnapSyncBatch>(null);
            }
        }

        public override SyncResponseHandlingResult HandleResponse(SnapSyncBatch? batch)
        {
            if (batch == null)
            {
                if (_logger.IsError) _logger.Error("Received empty batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            if(batch.AccountRangeResponse is not null)
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

                _accountResponsesCount++;
            }
            else if(batch.StorageRangeResponse is not null)
            {
                if (batch.StorageRangeResponse.PathsAndSlots.Length == 0 && batch.StorageRangeResponse.Proofs.Length == 0)
                {
                    _logger.Warn($"GetStorageRange: Requested expired RootHash:{batch.StorageRangeRequest.RootHash}");
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
                            _snapProvider.StoragesToRetrieve.Enqueue(batch.StorageRangeRequest.Accounts[i]);
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
                return SyncResponseHandlingResult.NoProgress;
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
