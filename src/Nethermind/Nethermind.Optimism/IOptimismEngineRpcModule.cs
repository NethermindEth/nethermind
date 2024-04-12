// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism;


[RpcModule(ModuleType.Engine)]
public interface IOptimismEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, OptimismPayloadAttributes? payloadAttributes = null);

    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayload executionPayload);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, OptimismPayloadAttributes? payloadAttributes = null);

    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayload executionPayload);

    [JsonRpcMethod(
        Description =
            "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(
        ForkchoiceStateV1 forkchoiceState, OptimismPayloadAttributes? payloadAttributes = null);

    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(ExecutionPayloadV3 executionPayload,
        byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot);
}
