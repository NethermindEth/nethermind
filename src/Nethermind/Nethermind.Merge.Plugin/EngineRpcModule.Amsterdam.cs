// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV6Result?> _getPayloadHandlerV6;
    private readonly IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>> _executionGetPayloadBodiesByHashV2Handler;
    private readonly IGetPayloadBodiesByRangeV2Handler _executionGetPayloadBodiesByRangeV2Handler;

    public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId)
        => _getPayloadHandlerV6.HandleAsync(payloadId);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV4 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests), EngineApiVersions.Amsterdam);

    public ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>> engine_getPayloadBodiesByHashV2(IReadOnlyList<Hash256> blockHashes)
        => _executionGetPayloadBodiesByHashV2Handler.Handle(blockHashes);

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(long start, long count)
        => _executionGetPayloadBodiesByRangeV2Handler.Handle(start, count);
}
