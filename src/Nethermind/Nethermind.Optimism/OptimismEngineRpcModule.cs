// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism;

public class OptimismEngineRpcModule : IOptimismEngineRpcModule
{
    private readonly IEngineRpcModule _engineRpcModule;

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, OptimismPayloadAttributes? payloadAttributes = null)
    {
        return await _engineRpcModule.engine_forkchoiceUpdatedV1(forkchoiceState, payloadAttributes);
    }

    public Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId)
    {
        return _engineRpcModule.engine_getPayloadV1(payloadId);
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayload executionPayload)
    {
        return _engineRpcModule.engine_newPayloadV1(executionPayload);
    }

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, OptimismPayloadAttributes? payloadAttributes = null)
    {
        return await _engineRpcModule.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes);
    }

    public Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId)
    {
        return _engineRpcModule.engine_getPayloadV2(payloadId);
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayload executionPayload)
    {
        return _engineRpcModule.engine_newPayloadV2(executionPayload);
    }

    public OptimismEngineRpcModule(IEngineRpcModule engineRpcModule)
    {
        _engineRpcModule = engineRpcModule;
    }
}
