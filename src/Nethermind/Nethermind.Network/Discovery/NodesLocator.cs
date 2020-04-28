﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
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
            List<Keccak> alreadyTriedNodes = new List<Keccak>();

            if(_logger.IsDebug) _logger.Debug($"Starting discovery process for node: {(searchedNodeId != null ? $"randomNode: {new PublicKey(searchedNodeId).ToShortString()}" : $"masterNode: {_masterNode.Id}")}");
            int nodesCountBeforeDiscovery = _nodeTable.Buckets.Sum(x => x.BondedItems.Count);

            for (int i = 0; i < _discoveryConfig.MaxDiscoveryRounds; i++)
            {
                Node[] tryCandidates;
                int candTryIndex = 0;
                while (true)
                {
                    //if searched node is not specified master node is used
                    Node[] closestNodes = searchedNodeId != null ? _nodeTable.GetClosestNodes(searchedNodeId) : _nodeTable.GetClosestNodes();
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

                    if(_logger.IsTrace) _logger.Trace($"Waiting {_discoveryConfig.DiscoveryNewCycleWaitTime} for new nodes");
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
                    if(_logger.IsTrace) _logger.Trace("No more closer candidates");
                    break;
                }

                int successRequestsCount = 0;
                int failRequestCount = 0;
                int nodesTriedCount = 0;
                while (true)
                {
                    int count = failRequestCount > 0 ? failRequestCount : _discoveryConfig.Concurrency;
                    Node[] nodesToSend = tryCandidates.Skip(nodesTriedCount).Take(count).ToArray();
                    if (!nodesToSend.Any())
                    {
                        if(_logger.IsDebug) _logger.Debug($"No more nodes to send, sent {successRequestsCount} successfull requests, failedRequestCounter: {failRequestCount}, nodesTriedCounter: {nodesTriedCount}");
                        break;
                    }

                    nodesTriedCount += nodesToSend.Length;
                    alreadyTriedNodes.AddRange(nodesToSend.Select(x => x.IdHash));

                    Result[] results = await SendFindNode(nodesToSend, searchedNodeId, cancellationToken);
                    
                    foreach (Result result in results)
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
            int nodesCountAfterDiscovery = _nodeTable.Buckets.Sum(x => x.BondedItems.Count);
            if(_logger.IsDebug) _logger.Debug($"Finished discovery cycle, tried contacting {alreadyTriedNodes.Count} nodes. All nodes count before the process: {nodesCountBeforeDiscovery}, after the process: {nodesCountAfterDiscovery}");

            if (_logger.IsTrace)
            {
                LogNodeTable();
            }
        }

        private void LogNodeTable()
        {
            NodeBucket[] nonEmptyBuckets = _nodeTable.Buckets.Where(x => x.BondedItems.Any()).ToArray();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("------------------------------------------------------");
            sb.AppendLine($"NodeTable, non-empty bucket count: {nonEmptyBuckets.Length}, total items count: {nonEmptyBuckets.Sum(x => x.BondedItems.Count)}");

            foreach (NodeBucket nodeBucket in nonEmptyBuckets)
            {
                sb.AppendLine($"Bucket: {nodeBucket.Distance}, count: {nodeBucket.BondedItems.Count}");
                foreach (NodeBucketItem bucketItem in nodeBucket.BondedItems)
                {
                    sb.AppendLine($"{bucketItem.Node}, LastContactTime: {bucketItem.LastContactTime:yyyy-MM-dd HH:mm:ss:000}");
                }
            }

            sb.AppendLine("------------------------------------------------------");
            _logger.Trace(sb.ToString());
        }

        private async Task<Result[]> SendFindNode(Node[] nodesToSend, byte[] searchedNodeId, CancellationToken cancellationToken)
        {
            List<Task<Result>> sendFindNodeTasks = new List<Task<Result>>();
            foreach (Node node in nodesToSend)
            {
                Task<Result> task = SendFindNode(node, searchedNodeId, cancellationToken);
                sendFindNodeTasks.Add(task);
            }

            return await Task.WhenAll(sendFindNodeTasks);
        }

        private async Task<Result> SendFindNode(Node destinationNode, byte[] searchedNodeId, CancellationToken cancellationToken)
        {
            try
            {
                INodeLifecycleManager nodeManager = _discoveryManager.GetNodeLifecycleManager(destinationNode);
                
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