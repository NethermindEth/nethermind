// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Analytics
{
    [RpcModule(ModuleType.Clique)]
    public interface IAnalyticsRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Retrieves ETH supply counted from state.", IsImplemented = true)]
        ResultWrapper<UInt256> analytics_verifySupply();

        [JsonRpcMethod(Description = "Retrieves ETH supply counted from rewards.", IsImplemented = true)]
        ResultWrapper<UInt256> analytics_verifyRewards();
    }
}
