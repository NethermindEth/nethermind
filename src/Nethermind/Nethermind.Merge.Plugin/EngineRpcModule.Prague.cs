// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.handlers;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV4Result?> _getPayloadHandlerV4;

    private readonly IAsyncHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>> _executionGetPayloadBodiesByHashV2Handler;
    private readonly IGetPayloadBodiesByRangeV2Handler _executionGetPayloadBodiesByRangeV2Handler;

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV4(ExecutionPayloadV4 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot) =>
        NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot), EngineApiVersions.Prague);

    public async Task<ResultWrapper<GetPayloadV4Result?>> engine_getPayloadV4(byte[] payloadId) =>
        await _getPayloadHandlerV4.HandleAsync(payloadId);

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByHashV2(IReadOnlyList<Hash256> blockHashes)
        => _executionGetPayloadBodiesByHashV2Handler.HandleAsync(blockHashes);

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(long start, long count)
        => _executionGetPayloadBodiesByRangeV2Handler.Handle(start, count);
}
