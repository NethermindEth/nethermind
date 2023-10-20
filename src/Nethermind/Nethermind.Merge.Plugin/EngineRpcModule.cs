// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{

    private readonly IHandler<IEnumerable<string>, IEnumerable<string>> _capabilitiesHandler;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public EngineRpcModule(
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
    {
        _capabilitiesHandler = capabilitiesHandler ?? throw new ArgumentNullException(nameof(capabilitiesHandler));
        _getPayloadHandlerV1 = getPayloadHandlerV1;
        _getPayloadHandlerV2 = getPayloadHandlerV2;
        _getPayloadHandlerV3 = getPayloadHandlerV3;
        _newPayloadV1Handler = newPayloadV1Handler;
        _forkchoiceUpdatedV1Handler = forkchoiceUpdatedV1Handler;
        _executionGetPayloadBodiesByHashV1Handler = executionGetPayloadBodiesByHashV1Handler;
        _executionGetPayloadBodiesByRangeV1Handler = executionGetPayloadBodiesByRangeV1Handler;
        _transitionConfigurationHandler = transitionConfigurationHandler;
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _gcKeeper = gcKeeper;
        _logger = logManager.GetClassLogger();
    }

    public ResultWrapper<IEnumerable<string>> engine_exchangeCapabilities(IEnumerable<string> methods)
        => _capabilitiesHandler.Handle(methods);
}
