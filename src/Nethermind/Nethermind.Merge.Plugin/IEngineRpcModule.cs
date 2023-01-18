// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
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

    [JsonRpcMethod(
        Description = "Returns an array of execution payload bodies for the list of provided block hashes.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByHashV1(Keccak[] blockHashes);

    [JsonRpcMethod(
        Description = "Returns an array of execution payload bodies for the provided number range",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByRangeV1(long start, long count);
}
