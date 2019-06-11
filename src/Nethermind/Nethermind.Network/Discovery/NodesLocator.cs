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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery
{
    public class NodesLocator : INodesLocator
    {
        private readonly ILogger _logger;
        private readonly INodeTable _nodeTable;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly IDiscoveryConfig _discoveryConfig;
        private Node _masterNode;

        public NodesLocator(INodeTable nodeTable, IDiscoveryManager discoveryManager, IDiscoveryConfig discoveryConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
            _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
            _discoveryManager = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
        }

        public void Initialize(Node masterNode)
        {
            _masterNode = masterNode;
        }

        public async Task LocateNodesAsync(CancellationToken cancellationToken)
        {
            await LocateNodesAsync(null, cancellationToken);
        }

        public async Task LocateNodesAsync(byte[] searchedNodeId, CancellationToken cancellationToken)
        {
            var alreadyTriedNodes = new List<Keccak>();

            if(_logger.IsDebug) _logger.Debug($"Starting discovery process for node: {(searchedNodeId != null ? $"randomNode: {new PublicKey(searchedNodeId).ToShortString()}" : $"masterNode: {_masterNode.Id}")}");
            var nodesCountBeforeDiscovery = _nodeTable.Buckets.Sum(x => x.Items.Count);

            for (var i = 0; i < _discoveryConfig.MaxDiscoveryRounds; i++)
            {
                Node[] tryCandidates;
                var candTryIndex = 0;
                while (true)
                {
                    //if searched node is not specified master node is used
                    var closestNodes = searchedNodeId != null ? _nodeTable.GetClosestNodes(searchedNodeId) : _nodeTable.GetClosestNodes();
                    tryCandidates = closestNodes.Where(node => !alreadyTriedNodes.Contains(node.IdHash)).ToArray();
                    if (tryCandidates.Any())
                    {
                        break;
                    }
                    if (candTryIndex > 20)
                    {
                        break;
                    }
                    candTryIndex = candTryIndex + 1;

                    _logger.Trace($"Waiting {_discoveryConfig.DiscoveryNewCycleWaitTime} for new nodes");
                    //we need to wait some time for pong messages received from new nodes we reached out to
                    try
                    {
                        await Task.Delay(_discoveryConfig.DiscoveryNewCycleWaitTime, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                if (!tryCandidates.Any())
                {
                    _logger.Trace("No more closer candidates");
                    break;
                }

                var successRequestsCount = 0;
                var failRequestCount = 0;
                var nodesTriedCount = 0;
                while (true)
                {
                    var count = failRequestCount > 0 ? failRequestCount : _discoveryConfig.Concurrency;
                    var nodesToSend = tryCandidates.Skip(nodesTriedCount).Take(count).ToArray();
                    if (!nodesToSend.Any())
                    {
                        _logger.Trace($"No more nodes to send, sent {successRequestsCount} successfull requests, failedRequestCounter: {failRequestCount}, nodesTriedCounter: {nodesTriedCount}");
                        break;
                    }

                    nodesTriedCount += nodesToSend.Length;
                    alreadyTriedNodes.AddRange(nodesToSend.Select(x => x.IdHash));

                    var results = await SendFindNode(nodesToSend, searchedNodeId, cancellationToken);
                    
                    foreach (var result in results)
                    {
                        if ((result?.ResultType ?? ResultType.Failure) == ResultType.Failure)
                        {
                            failRequestCount++;
                        }
                        else
                        {
                            successRequestsCount++;
                        }
                    }

                    if (successRequestsCount >= _discoveryConfig.Concurrency)
                    {
                        if(_logger.IsTrace) _logger.Trace($"Sent {successRequestsCount} successfull requests, failedRequestCounter: {failRequestCount}, nodesTriedCounter: {nodesTriedCount}");
                        break;
                    }
                }
            }
            var nodesCountAfterDiscovery = _nodeTable.Buckets.Sum(x => x.Items.Count);
            if(_logger.IsDebug) _logger.Debug($"Finished discovery cycle, tried contacting {alreadyTriedNodes.Count} nodes. All nodes count before the process: {nodesCountBeforeDiscovery}, after the process: {nodesCountAfterDiscovery}");

            if (_logger.IsTrace)
            {
                LogNodeTable();
            }
        }

        private void LogNodeTable()
        {
            var nonEmptyBuckets = _nodeTable.Buckets.Where(x => x.Items.Any()).ToArray();
            var sb = new StringBuilder();
            sb.AppendLine("------------------------------------------------------");
            sb.AppendLine($"NodeTable, non-empty bucket count: {nonEmptyBuckets.Length}, total items count: {nonEmptyBuckets.Sum(x => x.Items.Count)}");

            foreach (var nodeBucket in nonEmptyBuckets)
            {
                sb.AppendLine($"Bucket: {nodeBucket.Distance}, count: {nodeBucket.Items.Count}");
                foreach (var bucketItem in nodeBucket.Items)
                {
                    sb.AppendLine($"{bucketItem.Node}, LastContactTime: {bucketItem.LastContactTime:yyyy-MM-dd HH:mm:ss:000}");
                }
            }

            sb.AppendLine("------------------------------------------------------");
            _logger.Trace(sb.ToString());
        }

        private async Task<Result[]> SendFindNode(Node[] nodesToSend, byte[] searchedNodeId, CancellationToken cancellationToken)
        {
            var sendFindNodeTasks = new List<Task<Result>>();
            foreach (var node in nodesToSend)
            {
                var task = SendFindNode(node, searchedNodeId, cancellationToken);
                sendFindNodeTasks.Add(task);
            }

            return await Task.WhenAll(sendFindNodeTasks);
        }

        private async Task<Result> SendFindNode(Node destinationNode, byte[] searchedNodeId, CancellationToken cancellationToken)
        {
            try
            {
                var nodeManager = _discoveryManager.GetNodeLifecycleManager(destinationNode);
                nodeManager?.SendFindNode(searchedNodeId ?? _masterNode.Id.Bytes);

                if (await _discoveryManager.WasMessageReceived(destinationNode.IdHash, MessageType.Neighbors, _discoveryConfig.SendNodeTimeout))
                {
                    return Result.Success;
                }

                return Result.Fail($"Did not receive Neighbors reponse in time from: {destinationNode.Host}");
            }
            catch (OperationCanceledException)
            {
                return Result.Fail("Cancelled");
            } 
        }
    }
}