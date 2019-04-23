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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public class NodeDataDownloader : INodeDataDownloader
    {
        private readonly INodeDataFeed _nodeDataFeed;
        private INodeDataRequestExecutor _executor;
        private const int MaxPendingRequestsCount = 1;
        private const int MaxRequestSize = 384;
        private int _pendingRequests;
        private int _consumedNodesCount;
        private ILogger _logger;

        public NodeDataDownloader(INodeDataFeed nodeDataFeed, ILogManager logManager)
        {
            _nodeDataFeed = nodeDataFeed ?? throw new ArgumentNullException(nameof(nodeDataFeed));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public NodeDataDownloader(INodeDataFeed nodeDataFeed, INodeDataRequestExecutor executor, ILogManager logManager)
            : this(nodeDataFeed, logManager)
        {
            SetExecutor(executor);
        }

        private async Task KeepSyncing(CancellationToken token)
        {
            StateSyncBatch[] dataBatches;
            do
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                dataBatches = PrepareRequests();
                for (int i = 0; i < dataBatches.Length; i++)
                {
                    StateSyncBatch currentBatch = dataBatches[i];
                    if (_logger.IsTrace) _logger.Trace($"Sending requests for {currentBatch.StateSyncs.Length} nodes");
                    await _executor.ExecuteRequest(token, currentBatch).ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            int consumed = _nodeDataFeed.HandleResponse(t.Result);
                            _consumedNodesCount += consumed;
                            return;
                        }

                        if (t.IsCanceled)
                        {
                            throw new EthSynchronizationException("Canceled");
                        }

                        if (t.IsFaulted)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Node data request faulted {t.Exception}");
                            throw t.Exception;
                        }

                        if (_logger.IsDebug) _logger.Debug($"Something else happened with node data request");
                    });
                }
            } while (dataBatches.Length != 0);
        }

        private StateSyncBatch[] PrepareRequests()
        {
            List<StateSyncBatch> requests = new List<StateSyncBatch>();
            do
            {
                StateSyncBatch currentBatch = _nodeDataFeed.PrepareRequest(MaxRequestSize);
                if (currentBatch.StateSyncs.Length == 0)
                {
                    break;
                }
                
                requests.Add(currentBatch);
            } while (_pendingRequests + requests.Count < MaxPendingRequestsCount);

            var requestsArray = requests.ToArray();
            Interlocked.Add(ref _pendingRequests, requestsArray.Length);
            return requestsArray;
        }

        public async Task<long> SyncNodeData(CancellationToken token, Keccak rootNode)
        {
            _nodeDataFeed.SetNewStateRoot(rootNode);
            await KeepSyncing(token);
            return _consumedNodesCount;
        }

        public void SetExecutor(INodeDataRequestExecutor executor)
        {
            if (_logger.IsTrace) _logger.Trace($"Setting request executor to {executor.GetType().Name}");
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public bool IsFullySynced(Keccak stateRoot)
        {
            return _nodeDataFeed.IsFullySynced(stateRoot);
        }
    }
}