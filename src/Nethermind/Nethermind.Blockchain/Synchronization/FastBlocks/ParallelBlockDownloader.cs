/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Mining;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class ParallelBlocksDownloader
    {
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly IBlockRequestFeed _blockRequestFeed;
        private readonly ISealValidator _sealValidator;
        private int _pendingRequests;
        private int _downloadedHeaders;
        private ILogger _logger;

        public ParallelBlocksDownloader(IEthSyncPeerPool syncPeerPool, IBlockRequestFeed nodeDataFeed, ISealValidator sealValidator, ILogManager logManager)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _blockRequestFeed = nodeDataFeed ?? throw new ArgumentNullException(nameof(nodeDataFeed));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private SemaphoreSlim _semaphore = new SemaphoreSlim(0);

        private int _lastUsefulPeerCount;

        private async Task ExecuteRequest(CancellationToken token, BlockSyncBatch batch)
        {
            SyncPeerAllocation nodeSyncAllocation = _syncPeerPool.Borrow(BorrowOptions.DoNotReplace, "fast blocks");
            try
            {
                ISyncPeer peer = nodeSyncAllocation?.Current?.SyncPeer;
                batch.AssignedPeer = nodeSyncAllocation;
                if (peer != null)
                {
                    Task<BlockHeader[]> getHeadersTask = peer.GetBlockHeaders(batch.HeadersSyncBatch.StartNumber.Value, batch.HeadersSyncBatch.RequestSize, 0, token);
                    await getHeadersTask.ContinueWith(
                        t =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                batch.HeadersSyncBatch.Response = getHeadersTask.Result;
                            }
                            else
                            {
                                _syncPeerPool.ReportNoSyncProgress(batch.AssignedPeer);
                            }
                        }
                    );
                }
                else
                {
                    await Task.Delay(10);
                }

                (BlocksDataHandlerResult Result, int NodesConsumed) result = (BlocksDataHandlerResult.InvalidFormat, 0);
                try
                {
                    if (batch.HeadersSyncBatch?.Response != null)
                    {
                        ValidateSeals(token, batch.HeadersSyncBatch.Response);
                    }

                    result = _blockRequestFeed.HandleResponse(batch);
                    if (result.Result == BlocksDataHandlerResult.BadQuality)
                    {
                        if (batch.AssignedPeer?.Current != null)
                        {
                            _syncPeerPool.ReportBadPeer(batch.AssignedPeer);
                        }
                    }
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Error when handling response", e);
                }

                Interlocked.Add(ref _downloadedHeaders, result.NodesConsumed);
                if (result.NodesConsumed == 0 && peer != null)
                {
                    _syncPeerPool.ReportNoSyncProgress(nodeSyncAllocation);
                }
            }
            finally
            {
                if (nodeSyncAllocation != null)
                {
                    _syncPeerPool.Free(nodeSyncAllocation);
                }
            }
        }

        private void ValidateSeals(CancellationToken cancellation, BlockHeader[] headers)
        {
            if (_logger.IsTrace) _logger.Trace("Starting seal validation");

            for (int i = 0; i < headers.Length; i++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    if (_logger.IsTrace) _logger.Trace("Returning fom seal validation");
                    return;
                }

                BlockHeader header = headers[i];
                if (header == null)
                {
                    return;
                }

                if (!_sealValidator.ValidateSeal(headers[i]))
                {
                    if (_logger.IsTrace) _logger.Trace("One of the seals is invalid");
                    throw new EthSynchronizationException("Peer sent a block with an invalid seal");
                }
            }
        }

        private async Task UpdateParallelism()
        {
            int newUsefulPeerCount = _syncPeerPool.UsefulPeerCount;
            int difference = newUsefulPeerCount - _lastUsefulPeerCount;
            if (difference == 0)
            {
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Headers sync parallelism - {_syncPeerPool.UsefulPeerCount} useful peers out of {_syncPeerPool.PeerCount} in total (pending requests: {_pendingRequests} | remaining: {_semaphore.CurrentCount}).");
            if (difference > 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Releasing semaphore");
                _semaphore.Release(difference);
            }
            else
            {
                HashSet<Task<bool>> allSemaphoreTasks = new HashSet<Task<bool>>();
                for (int i = 0; i < -difference; i++)
                {
                    allSemaphoreTasks.Add(_semaphore.WaitAsync(5000));
                }

                foreach (Task<bool> semaphoreTask in allSemaphoreTasks)
                {
                    if (_logger.IsDebug) _logger.Debug($"Set semaphore");
                    if (!await semaphoreTask)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Faile to set semaphore");
                        newUsefulPeerCount++;
                    }
                }
            }

            _lastUsefulPeerCount = newUsefulPeerCount;
        }

        private async Task KeepSyncing(CancellationToken token)
        {
            int finalizeSignalsCount = 0;
            do
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await UpdateParallelism();
//                if (_logger.IsInfo) _logger.Info($"Waiting for semaphore");
                if (!await _semaphore.WaitAsync(1000, token))
                {
//                    if (_logger.IsInfo) _logger.Info($"Failed semaphore wait");
                    continue;
                }

//                if (_logger.IsInfo) _logger.Info($"Successful semaphore wait");

                BlockSyncBatch request = PrepareRequest();
                if (request?.HeadersSyncBatch != null)
                {
//                    if (_logger.IsInfo) _logger.Info($"Creating new headers request {request} with current semaphore count {_semaphore.CurrentCount} and pending requests {_pendingRequests}");
                    Interlocked.Increment(ref _pendingRequests);
                    Task task = ExecuteRequest(token, request);
#pragma warning disable 4014
                    task.ContinueWith(t =>
#pragma warning restore 4014
                    {
                        Interlocked.Decrement(ref _pendingRequests);
                        _semaphore.Release();
//                        if (_logger.IsInfo) _logger.Info($"Released semaphore - now at semaphore count {_semaphore.CurrentCount} and pending requests {_pendingRequests}");
                    });
                }
                else
                {
                    finalizeSignalsCount++;
                    await Task.Delay(10);
                    _semaphore.Release();
                    if (_logger.IsDebug) _logger.Debug($"DIAG: 0 batches created with {_pendingRequests} pending requests.");
                }
            } while (_pendingRequests != 0 || finalizeSignalsCount < 3);

            if (_logger.IsInfo) _logger.Info($"Finished with {_pendingRequests} pending requests and {_lastUsefulPeerCount} useful peers.");
        }

        private BlockSyncBatch PrepareRequest()
        {
            BlockSyncBatch request = _blockRequestFeed.PrepareRequest(_threshold);
            if (_logger.IsTrace) _logger.Trace($"Pending requests {_pendingRequests}");
            return request;
        }

        private int _threshold;

        public async Task<long> SyncHeaders(int threshold, CancellationToken token)
        {
            if (_logger.IsDebug) _logger.Debug($"Sync headers - pending: {_pendingRequests} - semaphore: {_semaphore.CurrentCount}");
            _blockRequestFeed.StartNewRound();
            _threshold = threshold;
            _downloadedHeaders = 0;
            await KeepSyncing(token);
            return _downloadedHeaders;
        }
    }
}