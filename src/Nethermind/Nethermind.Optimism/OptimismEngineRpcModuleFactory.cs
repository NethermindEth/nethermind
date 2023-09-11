using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Optimism;

public class OptimismEngineRpcModuleFactory(ISpecProvider specProvider, ILogManager logManager) : IEngineRpcModuleFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public IEngineRpcModule Create(
        IAsyncHandler<byte[],
        ExecutionPayload?> getPayloadHandlerV1,
        IAsyncHandler<byte[],
        GetPayloadV2Result?> getPayloadHandlerV2,
        IAsyncHandler<byte[],
        GetPayloadV3Result?> getPayloadHandlerV3,
        IAsyncHandler<ExecutionPayload,
        PayloadStatusV1> newPayloadV1Handler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
        IAsyncHandler<IList<Keccak>,
        IEnumerable<ExecutionPayloadBodyV1Result?>> executionGetPayloadBodiesByHashV1Handler,
        IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
        IHandler<TransitionConfigurationV1,
        TransitionConfigurationV1> transitionConfigurationHandler,
        IHandler<IEnumerable<string>,
        IEnumerable<string>> capabilitiesHandler,
        GCKeeper gcKeeper)
    {
        _logger.Info("Optimism Engine enabled.");

        return new OptimismEngineRpcModule(
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
            logManager
        );
    }
}
