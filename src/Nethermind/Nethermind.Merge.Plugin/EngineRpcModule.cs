// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule(
    IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
    IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
    IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadHandlerV3,
    IAsyncHandler<byte[], GetPayloadV4Result?> getPayloadHandlerV4,
    IAsyncHandler<byte[], GetPayloadV5Result?> getPayloadHandlerV5,
    IAsyncHandler<byte[], GetPayloadV6Result?> getPayloadHandlerV6,
    IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
    IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
    IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV1Result?>> executionGetPayloadBodiesByHashV1Handler,
    IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
    IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
    IHandler<HashSet<string>, IReadOnlyList<string>> capabilitiesHandler,
    IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>> getBlobsHandler,
    IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?> getBlobsHandlerV2,
    IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?> getBlobsHandlerV4,
    IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>> getPayloadBodiesByHashV2Handler,
    IGetPayloadBodiesByRangeV2Handler getPayloadBodiesByRangeV2Handler,
    IEngineRequestsTracker engineRequestsTracker,
    IBlobCustodyTracker blobCustodyTracker,
    ISpecProvider specProvider,
    GCKeeper gcKeeper,
    ILogManager logManager) : IEngineRpcModule
{

    private readonly IHandler<HashSet<string>, IReadOnlyList<string>> _capabilitiesHandler = capabilitiesHandler ?? throw new ArgumentNullException(nameof(capabilitiesHandler));
    private readonly IBlobCustodyTracker _blobCustodyTracker = blobCustodyTracker ?? throw new ArgumentNullException(nameof(blobCustodyTracker));
    protected readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    protected readonly ILogger _logger = logManager.GetClassLogger<EngineRpcModule>();

    public ResultWrapper<IReadOnlyList<string>> engine_exchangeCapabilities(IEnumerable<string> methods)
        => _capabilitiesHandler.Handle(methods as HashSet<string> ?? [.. methods]);

    public ResultWrapper<ClientVersionV1[]> engine_getClientVersionV1(ClientVersionV1 clientVersionV1) =>
        ResultWrapper<ClientVersionV1[]>.Success(string.IsNullOrEmpty(clientVersionV1.Code) ? [new()] : [new(), clientVersionV1]);
}
