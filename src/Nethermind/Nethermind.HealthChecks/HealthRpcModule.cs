// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc;

namespace Nethermind.HealthChecks
{
    public class NodeStatusResult
    {
        public bool Healthy { get; set; }
        public IEnumerable<string> Messages { get; set; }
        public IEnumerable<string> Errors { get; set; }
        public bool IsSyncing { get; set; }
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
            IEnumerable<string> messages = checkHealthResult.Messages.Select(x => x.Message);
            NodeStatusResult result = new()
            {
                Healthy = checkHealthResult.Healthy,
                Errors = checkHealthResult.Errors,
                Messages = messages,
                IsSyncing = checkHealthResult.IsSyncing
            };
            return ResultWrapper<NodeStatusResult>.Success(result);
        }
    }
}
