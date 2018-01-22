using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nevermind.Core;
using Nevermind.Discovery.RoutingTable;
using Nevermind.Utils.Model;

namespace Nevermind.Discovery
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
            var alreadyTriedNodes = new List<string>();
            
            for (var i = 0; i < _configurationProvider.MaxDiscoveryRounds; i++)
            {
                var closestNodes = _nodeTable.GetClosestNodes();
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

                    var results = await SendFineNode(nodesToSend);
                    
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

        private async Task<Result[]> SendFineNode(Node[] nodesToSend)
        {
            var sendFindNodeTasks = new List<Task<Result>>();
            foreach (var node in nodesToSend)
            {
                var task = SendFindNode(node);
                sendFindNodeTasks.Add(task);
            }

            return await Task.WhenAll(sendFindNodeTasks);
        }

        public async Task<Result> SendFindNode(Node destinationNode)
        {
            return await Task.Run(() => { return Result.Success(); });
        }
    }
}