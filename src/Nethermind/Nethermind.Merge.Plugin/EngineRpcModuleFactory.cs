using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public class EngineRpcModuleFactory : IEngineRpcModuleFactory
{
    private readonly ISpecProvider _specProvider;
    private readonly ILogManager _logManager;

    public EngineRpcModuleFactory(ISpecProvider specProvider, ILogManager logManager)
    {
        _specProvider = specProvider;
        _logManager = logManager;
    }

    public IEngineRpcModule Create(
        IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
        IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadHandlerV3,
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
        IAsyncHandler<IList<Keccak>, IEnumerable<ExecutionPayloadBodyV1Result?>> executionGetPayloadBodiesByHashV1Handler,
        IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
        IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
        IHandler<IEnumerable<string>, IEnumerable<string>> capabilitiesHandler,
        GCKeeper gcKeeper
    )
    {
        return new EngineRpcModule(
            getPayloadHandlerV1,
            getPayloadHandlerV2,
            getPayloadHandlerV3,
            newPayloadV1Handler,
            forkchoiceUpdatedV1Handler,
            executionGetPayloadBodiesByHashV1Handler,
            executionGetPayloadBodiesByRangeV1Handler,
            transitionConfigurationHandler,
            capabilitiesHandler,
            _specProvider,
            gcKeeper,
            _logManager
        );
    }
}
