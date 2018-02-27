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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Model;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;

namespace Nethermind.Discovery
{
    public class NodesLocator : INodesLocator
    {
        private readonly ILogger _logger;
        private readonly INodeTable _nodeTable;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly Node _masterNode;

        public NodesLocator(INodeTable nodeTable, IDiscoveryManager discoveryManager, IDiscoveryConfigurationProvider configurationProvider, ILogger logger)
        {
            _nodeTable = nodeTable;
            _discoveryManager = discoveryManager;
            _configurationProvider = configurationProvider;
            _logger = logger;
            _masterNode = nodeTable.MasterNode;
        }

        public async Task LocateNodes()
        {
            await LocateNodes(null);
        }

        public async Task LocateNodes(byte[] searchedNodeId)
        {
            var alreadyTriedNodes = new List<string>();
            
            _logger.Log($"Starting location process for node: {(searchedNodeId != null ? new Hex(searchedNodeId).ToString() : "masterNode: " + _masterNode.Id)}");

            for (var i = 0; i < _configurationProvider.MaxDiscoveryRounds; i++)
            {
                //if searched node is not specified master node is used
                var closestNodes = searchedNodeId != null ? _nodeTable.GetClosestNodes(searchedNodeId) : _nodeTable.GetClosestNodes();
                var tryCandidates = closestNodes.Where(node => !alreadyTriedNodes.Contains(node.IdHashText)).ToArray();
                if (!tryCandidates.Any())
                {
                    _logger.Log("No more closer candidates");
                    break;
                }

                var successRequestsCount = 0;
                var failRequestCount = 0;
                var nodesTriedCount = 0;
                while (true)
                {
                    var count = failRequestCount > 0 ? failRequestCount : _configurationProvider.Concurrency;
                    var nodesToSend = tryCandidates.Skip(nodesTriedCount).Take(count).ToArray();
                    if (!nodesToSend.Any())
                    {
                        _logger.Log($"No more nodes to send, sent {successRequestsCount} successfull requests, failedRequestCounter: {failRequestCount}, nodesTriedCounter: {nodesTriedCount}");
                        break;
                    }

                    nodesTriedCount += nodesToSend.Length;
                    alreadyTriedNodes.AddRange(nodesToSend.Select(x => x.IdHashText));

                    var results = await SendFindNode(nodesToSend, searchedNodeId);
                    
                    foreach (var result in results)
                    {
                        if (result.ResultType == ResultType.Failure)
                        {
                            failRequestCount++;
                        }
                        else
                        {
                            successRequestsCount++;
                        }
                    }

                    if (successRequestsCount >= _configurationProvider.Concurrency)
                    {
                        _logger.Log($"Sent {successRequestsCount} successfull requests, failedRequestCounter: {failRequestCount}, nodesTriedCounter: {nodesTriedCount}");
                        break;
                    }
                }
            }
            _logger.Log($"Finished locating nodes, triedNodesCount: {alreadyTriedNodes.Count}");
        }

        private async Task<Result[]> SendFindNode(Node[] nodesToSend, byte[] searchedNodeId)
        {
            var sendFindNodeTasks = new List<Task<Result>>();
            foreach (var node in nodesToSend)
            {
                var task = SendFindNode(node, searchedNodeId);
                sendFindNodeTasks.Add(task);
            }

            return await Task.WhenAll(sendFindNodeTasks);
        }

        private async Task<Result> SendFindNode(Node destinationNode, byte[] searchedNodeId)
        {
            return await Task.Run(() => SendFindNodeSync(destinationNode, searchedNodeId));
        }

        private Result SendFindNodeSync(Node destinationNode, byte[] searchedNodeId)
        {
            var nodeManager = _discoveryManager.GetNodeLifecycleManager(destinationNode);
            nodeManager.SendFindNode(searchedNodeId ?? _masterNode.Id.Bytes);

            if (_discoveryManager.WasMessageReceived(destinationNode.IdHashText, MessageType.Neighbors, _configurationProvider.SendNodeTimeout))
            {
                return Result.Success();
            }
            return Result.Fail($"Did not receive Neighbors reponse in time from: {destinationNode.Host}");
        }
    }
}