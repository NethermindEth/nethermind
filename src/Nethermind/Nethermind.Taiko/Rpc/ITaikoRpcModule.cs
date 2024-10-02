// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.Taiko.Rpc;

[RpcModule(ModuleType.Eth)]
public interface ITaikoRpcModule: IEthRpcModule
{
    [JsonRpcMethod(
        Description = "Returns the latest L2 block's corresponding L1 origin.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<L1Origin?>> taiko_headL1Origin();

    [JsonRpcMethod(
        Description = "Returns the L2 block's corresponding L1 origin.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<L1Origin?>> taiko_l1OriginByID(UInt256 blockId);

    [JsonRpcMethod(
        Description = "Returns the node sync mode.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<string>> taiko_getSyncMode();
#pragma warning restore IDE1006 // Naming Styles
}
