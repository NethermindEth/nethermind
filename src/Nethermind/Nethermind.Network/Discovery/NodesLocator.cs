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
            ISet<Keccak> alreadyTriedNodes = new HashSet<Keccak>();

            if(_logger.IsDebug) _logger.Debug($"Starting discovery process for node: {(searchedNodeId != null ? $"randomNode: {new PublicKey(searchedNodeId).ToShortString()}" : $"masterNode: {_masterNode.Id}")}");
            int nodesCountBeforeDiscovery = NodesCountBeforeDiscovery;

            Node[] tryCandidates = new Node[_discoveryConfig.BucketSize]; // max bucket size here
            for (int i = 0; i < _discoveryConfig.MaxDiscoveryRounds; i++)
            {
                Array.Clear(tryCandidates, 0, tryCandidates.Length);
                int candidatesCount;
                
                int attemptsCount = 0;
                while (true)
                {
                    //if searched node is not specified master node is used
                    IEnumerable<Node> closestNodes = searchedNodeId != null ? _nodeTable.GetClosestNodes(searchedNodeId) : _nodeTable.GetClosestNodes();

                    candidatesCount = 0;
                    foreach (Node closestNode in closestNodes.Where(node => !alreadyTriedNodes.Contains(node.IdHash)))
                    {
                        candidatesCount++;
                        tryCandidates[candidatesCount++] = closestNode;
                        if (candidatesCount > tryCandidates.Length - 1)
                        {
                            break;
                        }
                    }
                    
                    if (attemptsCount++ > 20 || candidatesCount > 0)
                    {
                        break;
                    }

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

                if (candidatesCount == 0)
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
                    IEnumerable<Node> nodesToSend = tryCandidates.Skip(nodesTriedCount).Take(count);
                    
                    var sendFindNodeTasks = SendFileNodes(searchedNodeId, cancellationToken, nodesToSend, alreadyTriedNodes);
                    Result[] results = await Task.WhenAll(sendFindNodeTasks);

                    if (results.Length == 0)
                    {
                        if(_logger.IsDebug) _logger.Debug($"No more nodes to send, sent {successRequestsCount} successfull requests, failedRequestCounter: {failRequestCount}, nodesTriedCounter: {nodesTriedCount}");
                        break;
                    }

                    nodesTriedCount += results.Length;
                    
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
            int nodesCountAfterDiscovery = _nodeTable.Buckets.Sum(x => x.BondedItemsCount);
            if(_logger.IsDebug) _logger.Debug($"Finished discovery cycle, tried contacting {alreadyTriedNodes.Count} nodes. All nodes count before the process: {nodesCountBeforeDiscovery}, after the process: {nodesCountAfterDiscovery}");

            if (_logger.IsTrace)
            {
                LogNodeTable();
            }
        }

        private IEnumerable<Task<Result>> SendFileNodes(byte[] searchedNodeId, CancellationToken cancellationToken, IEnumerable<Node> nodesToSend, ISet<Keccak> alreadyTriedNodes)
        {
            foreach (Node node in nodesToSend)
            {
                alreadyTriedNodes.Add(node.IdHash);
                yield return SendFindNode(node, searchedNodeId, cancellationToken);
            }
        }

        private int NodesCountBeforeDiscovery
        {
            get
            {
                int nodesCountBeforeDiscovery = 0;
                for (var index = 0; index < _nodeTable.Buckets.Length; index++)
                {
                    NodeBucket x = _nodeTable.Buckets[index];
                    nodesCountBeforeDiscovery += x.BondedItemsCount;
                }

                return nodesCountBeforeDiscovery;
            }
        }

        private void LogNodeTable()
        {
            IEnumerable<NodeBucket> nonEmptyBuckets = _nodeTable.Buckets.Where(x => x.BondedItems.Any());
            StringBuilder sb = new StringBuilder();

            int length = 0;
            int bondedItemsCount = 0;

            foreach (NodeBucket nodeBucket in nonEmptyBuckets)
            {
                length++;
                int itemsCount = nodeBucket.BondedItemsCount;
                bondedItemsCount += itemsCount;
                sb.AppendLine($"Bucket: {nodeBucket.Distance}, count: {itemsCount}");
                foreach (NodeBucketItem bucketItem in nodeBucket.BondedItems)
                {
                    sb.AppendLine($"{bucketItem.Node}, LastContactTime: {bucketItem.LastContactTime:yyyy-MM-dd HH:mm:ss:000}");
                }
            }
            
            sb.Insert(0, $"------------------------------------------------------{Environment.NewLine}NodeTable, non-empty bucket count: {length}, total items count: {bondedItemsCount}");
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

                return await _discoveryManager.WasMessageReceived(destinationNode.IdHash, MessageType.Neighbors, _discoveryConfig.SendNodeTimeout)
                    ? Result.Success
                    : Result.Fail($"Did not receive Neighbors response in time from: {destinationNode.Host}");
            }
            catch (OperationCanceledException)
            {
                return Result.Fail("Cancelled");
            } 
        }
    }
}
