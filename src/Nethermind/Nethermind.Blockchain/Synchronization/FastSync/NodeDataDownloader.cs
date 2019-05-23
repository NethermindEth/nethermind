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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization.FastSync
{
    public class NodeDataDownloader : INodeDataDownloader
    {
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly INodeDataFeed _nodeDataFeed;
        private const int MaxRequestSize = 384;
        private int _pendingRequests;
        private int _consumedNodesCount;
        private ILogger _logger;

        public NodeDataDownloader(IEthSyncPeerPool syncPeerPool, INodeDataFeed nodeDataFeed, ILogManager logManager)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _nodeDataFeed = nodeDataFeed ?? throw new ArgumentNullException(nameof(nodeDataFeed));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private SemaphoreSlim _semaphore = new SemaphoreSlim(0);
        
        private int _lastUsefulPeerCount;

        private async Task ExecuteRequest(CancellationToken token, StateSyncBatch batch)
        {
            SyncPeerAllocation nodeSyncAllocation = _syncPeerPool.Borrow(BorrowOptions.DoNotReplace, "node sync");
            try
            {
                ISyncPeer peer = nodeSyncAllocation?.Current?.SyncPeer;
                batch.AssignedPeer = nodeSyncAllocation;
                if (peer != null)
                {
                    var hashes = batch.RequestedNodes.Select(r => r.Hash).ToArray();
                    Task<byte[][]> getNodeDataTask = peer.GetNodeData(hashes, token);
                    await getNodeDataTask.ContinueWith(
                        t =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                batch.Responses = getNodeDataTask.Result;
                            }
                        }
                    );
                }

                (NodeDataHandlerResult Result, int NodesConsumed) result = (NodeDataHandlerResult.InvalidFormat, 0);
                try
                {
                    result = _nodeDataFeed.HandleResponse(batch);
                    if (result.Result == NodeDataHandlerResult.BadQuality)
                    {
                        _syncPeerPool.ReportBadPeer(batch.AssignedPeer);
                    }
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Error when handling response", e);
                }

                Interlocked.Add(ref _consumedNodesCount, result.NodesConsumed);
                if (result.NodesConsumed == 0 && peer != null)
                {
                    _syncPeerPool.ReportNoSyncProgress(nodeSyncAllocation);
                }
            }
            finally
            {
                if (nodeSyncAllocation != null)
                {
//                    _logger.Warn($"Free {nodeSyncAllocation?.Current}");
                    _syncPeerPool.Free(nodeSyncAllocation);
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

            if (_logger.IsInfo) _logger.Info($"Node sync parallelism - {_syncPeerPool.UsefulPeerCount} useful peers out of {_syncPeerPool.PeerCount} in total (pending requests: {_pendingRequests} | remaining: {_semaphore.CurrentCount}).");

            if (difference > 0)
            {
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
                    if (! await semaphoreTask)
                    {
                        newUsefulPeerCount++;
                    }
                }
            }

            _lastUsefulPeerCount = newUsefulPeerCount;
        }

        private async Task KeepSyncing(CancellationToken token)
        {
            do
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await UpdateParallelism();
                if (!await _semaphore.WaitAsync(1000, token))
                {
                    continue;
                }
                
                StateSyncBatch request = PrepareRequest();
                if (request.RequestedNodes.Length != 0)
                {   
                    Interlocked.Increment(ref _pendingRequests);
                    if (_logger.IsTrace) _logger.Trace($"Creating new request with {request.RequestedNodes.Length} nodes");
                    Task task = ExecuteRequest(token, request);
#pragma warning disable 4014
                    task.ContinueWith(t =>
#pragma warning restore 4014
                    {
                        Interlocked.Decrement(ref _pendingRequests);
                        _semaphore.Release();
                    });   
                }
                else
                {
                    await Task.Delay(50);
                    _semaphore.Release();
                    if (_logger.IsDebug) _logger.Debug($"DIAG: 0 batches created with {_pendingRequests} pending requests, {_nodeDataFeed.TotalNodesPending} pending nodes");
                }
            } while (_pendingRequests != 0);

            if (_logger.IsInfo) _logger.Info($"Finished with {_pendingRequests} pending requests and {_lastUsefulPeerCount} useful peers.");
        }

        private StateSyncBatch PrepareRequest()
        {
            StateSyncBatch request = _nodeDataFeed.PrepareRequest(MaxRequestSize);
            if (_logger.IsTrace) _logger.Trace($"Pending requests {_pendingRequests}");
            return request;
        }

        public async Task<long> SyncNodeData(CancellationToken token, long number, Keccak rootNode)
        {
            _consumedNodesCount = 0;
            _nodeDataFeed.SetNewStateRoot(number, rootNode);
            await KeepSyncing(token);
            return _consumedNodesCount;
        }

        public bool IsFullySynced(BlockHeader header)
        {
            return _nodeDataFeed.IsFullySynced(header.StateRoot);
        }
    }
}