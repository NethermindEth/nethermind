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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Mining;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class ParallelBlocksDownloader
    {
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly IBlockRequestFeed _blockRequestFeed;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private int _pendingRequests;
        private int _downloadedHeaders;
        private ILogger _logger;

        public ParallelBlocksDownloader(IEthSyncPeerPool syncPeerPool, IBlockRequestFeed blockRequestFeed, IBlockValidator blockValidator, ISealValidator sealValidator, ILogManager logManager)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _blockRequestFeed = blockRequestFeed ?? throw new ArgumentNullException(nameof(blockRequestFeed));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private SemaphoreSlim _semaphore = new SemaphoreSlim(0);

        private int _lastUsefulPeerCount;

        private async Task ExecuteRequest(CancellationToken token, BlockSyncBatch batch)
        {
            SyncPeerAllocation nodeSyncAllocation = _syncPeerPool.Borrow(BorrowOptions.DoNotReplace | (batch.Prioritized ? BorrowOptions.None : BorrowOptions.LowPriority), "fast blocks", batch.MinNumber);
            foreach (PeerInfo peerInfo in _syncPeerPool.UsefulPeers)
            {
                if (peerInfo.HeadNumber < Math.Max(0, (batch.MinNumber ?? 0) - 1024))
                {
                    if (_logger.IsDebug) _logger.Debug($"Made {peerInfo} sleep for a while - no min number satisfied");
                    _syncPeerPool.ReportNoSyncProgress(peerInfo);
                }
            }

            try
            {
                ISyncPeer peer = nodeSyncAllocation?.Current?.SyncPeer;
                batch.Allocation = nodeSyncAllocation;
                if (peer != null)
                {
                    batch.MarkSent();
                    Task<BlockHeader[]> getHeadersTask = peer.GetBlockHeaders(batch.HeadersSyncBatch.StartNumber.Value, batch.HeadersSyncBatch.RequestSize, 0, token);
                    await getHeadersTask.ContinueWith(
                        t =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                if (batch.RequestTime > 1000)
                                {
                                    _logger.Error($"{batch} - reporting peer too slow {batch.RequestTime:F2}");
                                }

                                batch.HeadersSyncBatch.Response = getHeadersTask.Result;
                            }
                            else
                            {
                                _syncPeerPool.ReportNoSyncProgress(batch.Allocation);
                            }
                        }
                    );
                }

                (BlocksDataHandlerResult Result, int NodesConsumed) result = (BlocksDataHandlerResult.InvalidFormat, 0);
                try
                {
                    batch.MarkValidation();
                    if (batch.HeadersSyncBatch?.Response != null)
                    {
                        ValidateBlocks(token, batch.HeadersSyncBatch.Response);
                    }
                    else
                    {
                        await Task.Delay(50);
                    }

                    result = _blockRequestFeed.HandleResponse(batch);
                    if (result.Result == BlocksDataHandlerResult.BadQuality)
                    {
                        if (batch.Allocation?.Current != null)
                        {
                            _syncPeerPool.ReportBadPeer(batch.Allocation);
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

        private void ValidateBlocks(CancellationToken cancellation, BlockHeader[] headers)
        {
            if (_logger.IsTrace) _logger.Trace("Starting block validation");

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
                
                bool isHashValid = _blockValidator.ValidateHash(header);
                bool isSealValid = _sealValidator.ValidateSeal(header);
                if (!(isHashValid && isSealValid))
                {
                    if (_logger.IsTrace) _logger.Trace("One of the blocks is invalid");
                    throw new EthSynchronizationException($"Peer sent a block with seal valid {isSealValid}, hash valid {isHashValid}");
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
                if (_logger.IsTrace) _logger.Trace($"Releasing semaphore - {_pendingRequests} pending");
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
                    if (_logger.IsTrace) _logger.Trace($"Set semaphore - {_pendingRequests} pending");
                    if (!await semaphoreTask)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Failed to set semaphore");
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
            BlockSyncBatch request = _blockRequestFeed.PrepareRequest();
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