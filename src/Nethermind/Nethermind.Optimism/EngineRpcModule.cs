// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism;

public interface IOptimismEngineRpcModule
{
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, OptimismPayloadAttributes? payloadAttributes = null);
}

public class OptimismEngineRpcModule : EngineRpcModule, IOptimismEngineRpcModule
{
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

        return await ForkchoiceUpdated(forkchoiceState, payloadAttributes, 1);
    }

    public OptimismEngineRpcModule(
        IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
        IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadHandlerV3,
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
        IAsyncHandler<IList<Keccak>, IEnumerable<ExecutionPayloadBodyV1Result?>> executionGetPayloadBodiesByHashV1Handler,
        IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
        IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
        IHandler<IEnumerable<string>, IEnumerable<string>> capabilitiesHandler,
        ISpecProvider specProvider,
        GCKeeper gcKeeper,
        ILogManager logManager)
        : base(
            getPayloadHandlerV1,
            getPayloadHandlerV2,
            getPayloadHandlerV3,
            newPayloadV1Handler,
            forkchoiceUpdatedV1Handler,
            executionGetPayloadBodiesByHashV1Handler,
            executionGetPayloadBodiesByRangeV1Handler,
            transitionConfigurationHandler,
            capabilitiesHandler,
            specProvider,
            gcKeeper,
            logManager)
    {
    }
}
