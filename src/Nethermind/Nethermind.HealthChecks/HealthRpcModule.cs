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
// 

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
            NodeStatusResult result = new NodeStatusResult() {Healthy = checkHealthResult.Healthy, Messages = messages};
            return ResultWrapper<NodeStatusResult>.Success(result);
        }
    }
}
