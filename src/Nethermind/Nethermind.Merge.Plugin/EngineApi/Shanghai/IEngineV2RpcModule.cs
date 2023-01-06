// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.EngineApi.Paris.Data;
using Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai
{
    [RpcModule(ModuleType.Engine)]
    public interface IEngineV2RpcModule : IRpcModule
    {
        [JsonRpcMethod(
            Description = "Returns the most recent version of an execution payload and fees with respect to the transaction set contained by the mempool.",
            IsSharable = true,
            IsImplemented = true)]
        public Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId);

        [JsonRpcMethod(
            Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayloadV2 executionPayload);

        [JsonRpcMethod(
            Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, PayloadAttributesV2? payloadAttributes = null);
    }
}
