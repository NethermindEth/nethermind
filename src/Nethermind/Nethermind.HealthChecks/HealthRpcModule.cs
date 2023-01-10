// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.JsonRpc;

namespace Nethermind.HealthChecks
{
    public class NodeStatusResult
    {
        public bool Healthy { get; set; }

        public string[] Messages { get; set; }
    }

    public class HealthRpcModule : IHealthRpcModule
    {
        private readonly INodeHealthService _nodeHealthService;

        public HealthRpcModule(INodeHealthService nodeHealthService)
        {
            _nodeHealthService = nodeHealthService;
        }

        public ResultWrapper<NodeStatusResult> health_nodeStatus()
        {
            CheckHealthResult checkHealthResult = _nodeHealthService.CheckHealth();
            string[] messages = checkHealthResult.Messages.Select(x => x.Message).ToArray();
            NodeStatusResult result = new() { Healthy = checkHealthResult.Healthy, Messages = messages };
            return ResultWrapper<NodeStatusResult>.Success(result);
        }
    }
}
