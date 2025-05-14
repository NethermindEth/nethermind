// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Optimism.Cl.Rpc;

[RpcModule("Optimism")]
public interface IOptimismOptimismRpcModule : IRpcModule
{
    [JsonRpcMethod(
        IsImplemented = false,
        Description = "TODO",
        IsSharable = true,
        ExampleResponse = "TODO")]
    public ResultWrapper<int> optimism_outputAtBlock();

    [JsonRpcMethod(
        IsImplemented = false,
        Description = "TODO",
        IsSharable = true,
        ExampleResponse = "TODO")]
    public ResultWrapper<int> optimism_syncStatus();

    [JsonRpcMethod(
        IsImplemented = false,
        Description = "TODO",
        IsSharable = true,
        ExampleResponse = "TODO")]
    public ResultWrapper<int> optimism_rollupConfig();

    [JsonRpcMethod(
        IsImplemented = true,
        Description = "Get the software version.",
        IsSharable = true,
        ExampleResponse = "1.31.10")]
    public ResultWrapper<string> optimism_version();
}
