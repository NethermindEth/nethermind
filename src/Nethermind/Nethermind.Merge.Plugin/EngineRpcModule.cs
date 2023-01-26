// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{

    private readonly IAsyncHandler<IEnumerable<string>, IEnumerable<string>> _capabilitiesHandler;
    private readonly IHandler<ExecutionStatusResult> _executionStatusHandler;
    private readonly IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result?[]> _executionGetPayloadBodiesByHashV1Handler;
    private readonly IGetPayloadBodiesByRangeV1Handler _executionGetPayloadBodiesByRangeV1Handler;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public EngineRpcModule(
        IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
        IHandler<ExecutionStatusResult> executionStatusHandler,
        IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result?[]> executionGetPayloadBodiesByHashV1Handler,
        IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
        IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
        IAsyncHandler<IEnumerable<string>, IEnumerable<string>> capabilitiesHandler,
        ISpecProvider specProvider,
        ILogManager logManager)
    {
        _capabilitiesHandler = capabilitiesHandler ?? throw new ArgumentNullException(nameof(capabilitiesHandler));
        _getPayloadHandlerV1 = getPayloadHandlerV1;
        _getPayloadHandlerV2 = getPayloadHandlerV2;
        _newPayloadV1Handler = newPayloadV1Handler;
        _forkchoiceUpdatedV1Handler = forkchoiceUpdatedV1Handler;
        _executionStatusHandler = executionStatusHandler;
        _executionGetPayloadBodiesByHashV1Handler = executionGetPayloadBodiesByHashV1Handler;
        _executionGetPayloadBodiesByRangeV1Handler = executionGetPayloadBodiesByRangeV1Handler;
        _transitionConfigurationHandler = transitionConfigurationHandler;
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logger = logManager.GetClassLogger();
    }

    public Task<ResultWrapper<IEnumerable<string>>> engine_exchangeCapabilities(IEnumerable<string> methods)
        => _capabilitiesHandler.HandleAsync(methods);

    public ResultWrapper<ExecutionStatusResult> engine_executionStatus() => _executionStatusHandler.Handle();

    public Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByHashV1(Keccak[] blockHashes)
        => _executionGetPayloadBodiesByHashV1Handler.HandleAsync(blockHashes);

    public Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByRangeV1(long start, long count)
        => _executionGetPayloadBodiesByRangeV1Handler.Handle(start, count);
}
