// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism;

public class OptimismEngineRpcModule : IOptimismEngineRpcModule
{
    private readonly IEngineRpcModule _engineRpcModule;

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, OptimismPayloadAttributes? payloadAttributes = null)
    {
        if (payloadAttributes is { GasLimit: 0 })
        {
            return ForkchoiceUpdatedV1Result.Error("GasLimit is required", MergeErrorCodes.InvalidForkchoiceState);
        }

        try
        {
            payloadAttributes?.GetTransactions();
        }
        catch (RlpException e)
        {
            return ForkchoiceUpdatedV1Result.Error(e.Message, MergeErrorCodes.InvalidForkchoiceState);
        }

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

    public OptimismEngineRpcModule(IEngineRpcModule engineRpcModule)
    {
        _engineRpcModule = engineRpcModule;
    }
}
