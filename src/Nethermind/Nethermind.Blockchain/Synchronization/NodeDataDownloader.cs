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
        private int _maxPendingRequestsCount = 1; // initial value - later adjusted based on the number of peers
        private const int MaxRequestSize = 3;
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
            HashSet<Task> tasks = new HashSet<Task>();
            StateSyncBatch[] dataBatches;
            do
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _maxPendingRequestsCount = _executor.HintMaxConcurrentRequests();
                dataBatches = PrepareRequests();
                for (int i = 0; i < dataBatches.Length; i++)
                {
                    StateSyncBatch currentBatch = dataBatches[i];
                    if (_logger.IsTrace) _logger.Trace($"Creating new task with - {dataBatches[i].StateSyncs.Length}");
                    Task task = _executor.ExecuteRequest(token, currentBatch).ContinueWith(t =>
                    {
                        int afterDecrement = Interlocked.Decrement(ref _pendingRequests);
                        if (_logger.IsTrace) _logger.Trace($"Decrementing pending requests - now at {afterDecrement}");
                        if (t.IsCompletedSuccessfully)
                        {
                            int consumed = _nodeDataFeed.HandleResponse(t.Result);
                            Interlocked.Add(ref _consumedNodesCount, consumed);
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

                        if (_logger.IsDebug) _logger.Debug("Something else happened with node data request");
                    });

                    tasks.Add(task);
                }

                if (tasks.Count != 0)
                {
                    Task firstComplete = await Task.WhenAny(tasks);
                    if (_logger.IsTrace) _logger.Trace($"Removing task from the list of {tasks.Count} node sync tasks");
                    if (!tasks.Remove(firstComplete))
                    {
                        if (_logger.IsError) _logger.Error($"Could not remove node sync task - task count {tasks.Count}");
                    }

                    if (firstComplete.IsFaulted)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Node sync task throwing {firstComplete.Exception?.Message}");
                        throw ((AggregateException) firstComplete.Exception).InnerExceptions[0];
                    }
                }
            } while (dataBatches.Length + _pendingRequests + tasks.Count != 0 || _nodeDataFeed.TotalNodesPending != 0);

            _logger.Debug($"Finished with {dataBatches.Length} {_pendingRequests} {tasks.Count}");
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
            } while (_pendingRequests + requests.Count < _maxPendingRequestsCount);

            var requestsArray = requests.ToArray();
            Interlocked.Add(ref _pendingRequests, requestsArray.Length);
            if (_logger.IsTrace) _logger.Trace($"Pending requests {_pendingRequests}");
            return requestsArray;
        }

        public async Task<long> SyncNodeData(CancellationToken token, Keccak rootNode)
        {
            _consumedNodesCount = 0;
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