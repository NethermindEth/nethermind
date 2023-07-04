// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.HealthChecks
{
    [RpcModule(ModuleType.Health)]
    public interface IHealthRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Check health status of the node", IsImplemented = true)]
        ResultWrapper<NodeStatusResult> health_nodeStatus();
    }
}
