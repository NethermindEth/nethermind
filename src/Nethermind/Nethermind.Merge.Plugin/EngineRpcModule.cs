// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Api;
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
    protected readonly ISpecProvider _specProvider;
    protected readonly ILogger _logger;

    public EngineRpcModule(
        IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
        IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadHandlerV3,
        IAsyncHandler<byte[], GetPayloadV4Result?> getPayloadHandlerV4,
        IAsyncHandler<byte[], GetPayloadV5Result?> getPayloadHandlerV5,
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
        IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>> executionGetPayloadBodiesByHashV1Handler,
        IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
        IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
        IHandler<IEnumerable<string>, IEnumerable<string>> capabilitiesHandler,
        IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> getBlobsHandler,
        IAsyncHandler<byte[][], IEnumerable<BlobAndProofV2>?> getBlobsHandlerV2,
        IEngineRequestsTracker engineRequestsTracker,
        ISpecProvider specProvider,
        GCKeeper gcKeeper,
        ILogManager logManager)
    {
        _capabilitiesHandler = capabilitiesHandler ?? throw new ArgumentNullException(nameof(capabilitiesHandler));
        _getPayloadHandlerV1 = getPayloadHandlerV1;
        _getPayloadHandlerV2 = getPayloadHandlerV2;
        _getPayloadHandlerV3 = getPayloadHandlerV3;
        _getPayloadHandlerV4 = getPayloadHandlerV4;
        _getPayloadHandlerV5 = getPayloadHandlerV5;
        _newPayloadV1Handler = newPayloadV1Handler;
        _forkchoiceUpdatedV1Handler = forkchoiceUpdatedV1Handler;
        _executionGetPayloadBodiesByHashV1Handler = executionGetPayloadBodiesByHashV1Handler;
        _executionGetPayloadBodiesByRangeV1Handler = executionGetPayloadBodiesByRangeV1Handler;
        _transitionConfigurationHandler = transitionConfigurationHandler;
        _getBlobsHandler = getBlobsHandler;
        _getBlobsHandlerV2 = getBlobsHandlerV2;
        _engineRequestsTracker = engineRequestsTracker;
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _gcKeeper = gcKeeper;
        _logger = logManager.GetClassLogger();
    }

    public ResultWrapper<IEnumerable<string>> engine_exchangeCapabilities(IEnumerable<string> methods)
        => _capabilitiesHandler.Handle(methods);

    public ResultWrapper<ClientVersionV1[]> engine_getClientVersionV1(ClientVersionV1 clientVersionV1)
    {
        return ResultWrapper<ClientVersionV1[]>.Success([new ClientVersionV1()]);
    }
}
