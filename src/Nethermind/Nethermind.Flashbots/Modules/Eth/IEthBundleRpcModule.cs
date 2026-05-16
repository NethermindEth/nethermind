// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Flashbots.Data;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Flashbots.Modules.Eth;

[RpcModule(ModuleType.Eth)]
public interface IEthBundleRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Submits a Flashbots MEV bundle. Defined by the Flashbots Auction RPC spec.",
        IsSharable = true,
        IsImplemented = false)]
    Task<ResultWrapper<EthBundleHash>> eth_sendBundle(EthSendBundle bundle);
}
