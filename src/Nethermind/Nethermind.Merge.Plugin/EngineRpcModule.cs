// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Collections.Pooled;
using Nethermind.Api;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule(
    IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
    IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
    IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadHandlerV3,
    IAsyncHandler<byte[], GetPayloadV4Result?> getPayloadHandlerV4,
    IAsyncHandler<byte[], GetPayloadV5Result?> getPayloadHandlerV5,
    IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
    IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
    IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>
        executionGetPayloadBodiesByHashV1Handler,
    IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
    IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
    IHandler<ISet<string>, IEnumerable<string>> capabilitiesHandler,
    IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> getBlobsHandler,
    IAsyncHandler<byte[][], IEnumerable<BlobAndProofV2>?> getBlobsHandlerV2,
    IEngineRequestsTracker engineRequestsTracker,
    ISpecProvider specProvider,
    GCKeeper gcKeeper,
    ILogManager logManager)
    : IEngineRpcModule
{

    private readonly IHandler<ISet<string>, IEnumerable<string>> _capabilitiesHandler = capabilitiesHandler ?? throw new ArgumentNullException(nameof(capabilitiesHandler));
    protected readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    protected readonly ILogger _logger = logManager.GetClassLogger();

    public ResultWrapper<IEnumerable<string>> engine_exchangeCapabilities(IEnumerable<string> methods)
    {
        using PooledSet<string> pooledSet = methods.ToPooledSet();
        return _capabilitiesHandler.Handle(pooledSet);
    }

    public ResultWrapper<ClientVersionV1[]> engine_getClientVersionV1(ClientVersionV1 clientVersionV1) =>
        ResultWrapper<ClientVersionV1[]>.Success([new ClientVersionV1()]);
}
