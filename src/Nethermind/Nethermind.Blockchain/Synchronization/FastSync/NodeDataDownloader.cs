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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization.FastSync
{
    public class NodeDataDownloader : INodeDataDownloader
    {
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly INodeDataFeed _feed;
        private readonly INodeDataConsumer _additionalConsumer;
        private int _pendingRequests;
        private int _consumedNodesCount;
        private ILogger _logger;

        public NodeDataDownloader(IEthSyncPeerPool syncPeerPool, INodeDataFeed nodeDataFeed, INodeDataConsumer additionalConsumer, ILogManager logManager)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _feed = nodeDataFeed ?? throw new ArgumentNullException(nameof(nodeDataFeed));
            _additionalConsumer = additionalConsumer ?? throw new ArgumentNullException(nameof(additionalConsumer));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _additionalConsumer.NeedMoreData += AdditionalConsumerOnNeedMoreData;
        }

        private void AdditionalConsumerOnNeedMoreData(object sender, EventArgs e)
        {
            Task keepSyncing = SyncOnce(CancellationToken.None, true);
            keepSyncing.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"Requesting node data failed: {t.Exception}");
                }
            });
        }

        private async Task ExecuteRequest(CancellationToken token, StateSyncBatch batch)
        {
            var peer = batch.AssignedPeer?.Current?.SyncPeer;
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
                if (batch.IsAdditionalDataConsumer)
                {
                    result = (NodeDataHandlerResult.OK, _additionalConsumer.HandleResponse(batch.RequestedNodes.Select(r => r.Hash).ToArray(), batch.Responses));
                }
                else
                {
                    result = _feed.HandleResponse(batch);
                }

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
                _syncPeerPool.ReportNoSyncProgress(batch.AssignedPeer, !batch.IsAdditionalDataConsumer);
            }

            if (batch.AssignedPeer != null)
            {
                _syncPeerPool.Free(batch.AssignedPeer);
            }
        }

        private async Task KeepSyncing(CancellationToken token)
        {
            int lastRequestSize; 
            do
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                
                lastRequestSize = await SyncOnce(token, false);
            } while (_pendingRequests != 0 || lastRequestSize > 0);

            if (_logger.IsInfo) _logger.Info($"Finished with {_pendingRequests} pending requests and {_syncPeerPool.UsefulPeerCount} useful peers.");
        }

        private async Task<int> SyncOnce(CancellationToken token, bool forAdditionalConsumers)
        {
            int requestSize = 0;
            StateSyncBatch request = PrepareRequest(forAdditionalConsumers);
            if (request.RequestedNodes.Length != 0)
            {
                request.AssignedPeer = await _syncPeerPool.BorrowAsync(PeerSelectionOptions.DoNotReplace, "node sync", null, 1000);

                Interlocked.Increment(ref _pendingRequests);
                // if (_logger.IsWarn) _logger.Warn($"Creating new request with {request.RequestedNodes.Length} nodes");
                Task task = ExecuteRequest(token, request);
#pragma warning disable 4014
                task.ContinueWith(t =>
#pragma warning restore 4014
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Failure when executing node data request {t.Exception}");
                    }

                    Interlocked.Decrement(ref _pendingRequests);
                    requestSize = request.RequestedNodes.Length;
                });
            }
            else
            {
                await Task.Delay(50);
                if (_logger.IsDebug) _logger.Debug($"DIAG: 0 batches created with {_pendingRequests} pending requests, {_feed.TotalNodesPending} pending nodes");
            }

            return requestSize;
        }

        private StateSyncBatch PrepareRequest(bool forAdditionalConsumers)
        {
            if (forAdditionalConsumers)
            {
                if (!_additionalConsumer.NeedsData)
                {
                    return StateSyncBatch.Empty;
                }

                Keccak[] hashes = _additionalConsumer.PrepareRequest();
                if (hashes.Length == 0)
                {
                    return StateSyncBatch.Empty;
                }

                StateSyncBatch priorityBatch = new StateSyncBatch();
                priorityBatch.RequestedNodes = hashes.Select(h => new StateSyncItem(h, NodeDataType.Code, 0, 0)).ToArray();
                // if (_logger.IsWarn) _logger.Warn($"!!! Priority batch {priorityBatch.RequestedNodes.Length}");
                priorityBatch.IsAdditionalDataConsumer = true;
                return priorityBatch;
            }

            if (_logger.IsTrace) _logger.Trace($"Pending requests {_pendingRequests}");
            StateSyncBatch standardBatch = _feed.PrepareRequest();
            // if (_logger.IsWarn) _logger.Warn($"!!! Standard batch {standardBatch.RequestedNodes.Length}");
            return standardBatch;
        }

        public async Task<long> SyncNodeData(CancellationToken token, long number, Keccak rootNode)
        {
            _consumedNodesCount = 0;
            _feed.SetNewStateRoot(number, rootNode);
            await KeepSyncing(token);
            return _consumedNodesCount;
        }

        public bool IsFullySynced(BlockHeader header) => _feed.IsFullySynced(header.StateRoot);
    }
}