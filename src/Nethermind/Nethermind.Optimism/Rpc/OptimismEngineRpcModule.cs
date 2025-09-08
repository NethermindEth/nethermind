// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.ProtocolVersion;

namespace Nethermind.Optimism.Rpc;

public class OptimismEngineRpcModule(
    IEngineRpcModule engineRpcModule,
    IOptimismSignalSuperchainV1Handler signalSuperchainHandler
) : IOptimismEngineRpcModule
{
    private readonly IEngineRpcModule _engineRpcModule = engineRpcModule;
    private readonly IOptimismSignalSuperchainV1Handler _signalSuperchainHandler = signalSuperchainHandler;

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

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(ForkchoiceStateV1 forkchoiceState, OptimismPayloadAttributes? payloadAttributes = null)
    {
        return await _engineRpcModule.engine_forkchoiceUpdatedV3(forkchoiceState, payloadAttributes);
    }

    public async Task<ResultWrapper<OptimismGetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId)
    {
        ResultWrapper<GetPayloadV3Result?> result = await _engineRpcModule.engine_getPayloadV3(payloadId);
        return ResultWrapper<OptimismGetPayloadV3Result?>.From(result, result.Data is null ? null : new OptimismGetPayloadV3Result(result.Data));
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot)
    {
        return _engineRpcModule.engine_newPayloadV3(executionPayload, blobVersionedHashes, parentBeaconBlockRoot);
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV4(OptimismExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
    {
        return _engineRpcModule.engine_newPayloadV4(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);
    }

    public Task<ResultWrapper<GetPayloadV4Result?>> engine_getPayloadV4(byte[] payloadId)
    {
        return _engineRpcModule.engine_getPayloadV4(payloadId);
    }

    public ResultWrapper<OptimismSignalSuperchainV1Result> engine_signalSuperchainV1(OptimismSuperchainSignal signal)
    {
        OptimismProtocolVersion currentVersion = _signalSuperchainHandler.CurrentVersion;

        if (currentVersion < signal.Recommended)
        {
            _signalSuperchainHandler.OnBehindRecommended(signal.Recommended);
        }
        if (currentVersion < signal.Required)
        {
            _signalSuperchainHandler.OnBehindRequired(signal.Required);
        }

        return ResultWrapper<OptimismSignalSuperchainV1Result>.Success(new OptimismSignalSuperchainV1Result(currentVersion));
    }
}
