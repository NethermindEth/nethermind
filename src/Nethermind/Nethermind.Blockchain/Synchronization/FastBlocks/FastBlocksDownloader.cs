//  Copyright (c) 2018 Demerzel Solutions Limited
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
    public class FastBlocksDownloader
    {
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly IFastBlocksFeed _fastBlocksFeed;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private int _pendingRequests;
        private int _downloadedHeaders;
        private ILogger _logger;

        public FastBlocksDownloader(IEthSyncPeerPool syncPeerPool, IFastBlocksFeed fastBlocksFeed, IBlockValidator blockValidator, ISealValidator sealValidator, ILogManager logManager)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _fastBlocksFeed = fastBlocksFeed ?? throw new ArgumentNullException(nameof(fastBlocksFeed));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private async Task ExecuteRequest(CancellationToken token, FastBlocksBatch batch)
        {
            SyncPeerAllocation syncPeerAllocation = batch.Allocation;
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
                ISyncPeer peer = syncPeerAllocation?.Current?.SyncPeer;
                if (peer != null)
                {
                    batch.MarkSent();
                    switch (batch.BatchType)
                    {
                        case FastBlocksBatchType.Headers:
                        {
                            Task<BlockHeader[]> getHeadersTask = peer.GetBlockHeaders(batch.Headers.StartNumber, batch.Headers.RequestSize, 0, token);
                            await getHeadersTask.ContinueWith(
                                t =>
                                {
                                    if (t.IsCompletedSuccessfully)
                                    {
                                        if (batch.RequestTime > 1000)
                                        {
                                            if (_logger.IsDebug) _logger.Debug($"{batch} - peer is slow {batch.RequestTime:F2}");
                                        }
                                        
                                        batch.Headers.Response = getHeadersTask.Result;
                                        ValidateHeaders(token, batch);
                                    }
                                    else
                                    {
                                        _syncPeerPool.ReportInvalid(batch.Allocation);
                                    }
                                }
                            );

                            break;
                        }

                        case FastBlocksBatchType.Bodies:
                        {
                            Task<BlockBody[]> getBodiesTask = peer.GetBlockBodies(batch.Bodies.Request, token);
                            await getBodiesTask.ContinueWith(
                                t =>
                                {
                                    if (t.IsCompletedSuccessfully)
                                    {
                                        if (batch.RequestTime > 1000)
                                        {
                                            if (_logger.IsDebug) _logger.Debug($"{batch} - peer is slow {batch.RequestTime:F2}");
                                        }

                                        batch.Bodies.Response = getBodiesTask.Result;
                                    }
                                    else
                                    {
                                        _syncPeerPool.ReportInvalid(batch.Allocation);
                                    }
                                }
                            );

                            break;
                        }

                        case FastBlocksBatchType.Receipts:
                        {
                            Task<TxReceipt[][]> getReceiptsTask = peer.GetReceipts(batch.Receipts.Request, token);
                            await getReceiptsTask.ContinueWith(
                                t =>
                                {
                                    if (t.IsCompletedSuccessfully)
                                    {
                                        if (batch.RequestTime > 1000)
                                        {
                                            if (_logger.IsDebug) _logger.Debug($"{batch} - peer is slow {batch.RequestTime:F2}");
                                        }

                                        batch.Receipts.Response = getReceiptsTask.Result;
                                    }
                                    else
                                    {
                                        _syncPeerPool.ReportInvalid(batch.Allocation);
                                    }
                                }
                            );
                            
                            break;
                        }

                        default:
                        {
                            throw new InvalidOperationException($"{nameof(FastBlocksBatchType)} is {batch.BatchType}");
                        }
                    }
                }

                (BlocksDataHandlerResult Result, int ItemsSynced) result = (BlocksDataHandlerResult.InvalidFormat, 0);
                try
                {
                    if (batch.Bodies?.Response == null
                        && batch.Headers?.Response == null
                        && batch.Receipts?.Response == null)
                    {
                        // to avoid uncontrolled loop in case of a code error
                        await Task.Delay(10);
                    }

                    result = _fastBlocksFeed.HandleResponse(batch);
                }
                catch (Exception e)
                {
                    // possibly clear the response and handle empty response batch here (to avoid missing parts)
                    if (_logger.IsError) _logger.Error($"Error when handling response", e);
                }

                Interlocked.Add(ref _downloadedHeaders, result.ItemsSynced);
                if (result.ItemsSynced == 0 && peer != null)
                {
                    _syncPeerPool.ReportNoSyncProgress(syncPeerAllocation);
                }
            }
            finally
            {
                if (syncPeerAllocation != null)
                {
                    _syncPeerPool.Free(syncPeerAllocation);
                }
            }
        }

        private void ValidateHeaders(CancellationToken cancellation, FastBlocksBatch batch)
        {
            batch.MarkValidation();
            try
            {
                if (_logger.IsTrace) _logger.Trace("Starting block validation");

                BlockHeader[] headers = batch.Headers.Response;
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
                        continue;
                    }

                    bool isHashValid = _blockValidator.ValidateHash(header);
                    bool isSealValid = _sealValidator.ValidateSeal(header, false);
                    if (!(isHashValid && isSealValid))
                    {
                        if (_logger.IsTrace) _logger.Trace("One of the blocks is invalid");
                        _syncPeerPool.ReportInvalid(batch.Allocation?.Current);
                        batch.Headers.Response = null;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Error when validating headers of {batch}", ex);
                batch.Headers.Response = null;
            }
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

                FastBlocksBatch request = PrepareRequest();
                if (request != null)
                {
                    request.Allocation = await _syncPeerPool.BorrowAsync(BorrowOptions.DoNotReplace | (request.Prioritized ? BorrowOptions.None : BorrowOptions.LowPriority), "fast blocks", request.MinNumber);
                    
                    Interlocked.Increment(ref _pendingRequests);
                    Task task = ExecuteRequest(token, request);
#pragma warning disable 4014
                    task.ContinueWith(t =>
#pragma warning restore 4014
                    {
                        Interlocked.Decrement(ref _pendingRequests);
                    });
                }
                else
                {
                    finalizeSignalsCount++;
                    await Task.Delay(10);
                    if (_logger.IsInfo) _logger.Info($"DIAG: 0 batches created with {_pendingRequests} pending requests.");
                }
            } while (_pendingRequests != 0 || finalizeSignalsCount < 3 || !_fastBlocksFeed.IsFinished);

            if (_logger.IsInfo) _logger.Info($"Finished download with {_pendingRequests} pending requests.");
        }

        private FastBlocksBatch PrepareRequest()
        {
            FastBlocksBatch request = _fastBlocksFeed.PrepareRequest();
            if (_logger.IsTrace) _logger.Trace($"Pending requests {_pendingRequests}");
            return request;
        }

        public async Task<long> Sync(CancellationToken token)
        {
            if (_logger.IsDebug) _logger.Debug($"Sync headers - pending: {_pendingRequests}");
            _fastBlocksFeed.StartNewRound();
            _downloadedHeaders = 0;
            await KeepSyncing(token);
            return _downloadedHeaders;
        }
    }
}