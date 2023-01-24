// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin;

[RpcModule(ModuleType.Engine)]
public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description =
            "Responds with information on the state of the execution client to either engine_consensusStatus or any other call if consistency failure has occurred.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<ExecutionStatusResult> engine_executionStatus();
}
