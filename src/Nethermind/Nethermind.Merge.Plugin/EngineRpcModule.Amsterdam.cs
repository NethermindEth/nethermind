// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV6Result?> _getPayloadHandlerV6 = getPayloadHandlerV6;
    private readonly IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>> _executionGetPayloadBodiesByHashV2Handler = getPayloadBodiesByHashV2Handler;
    private readonly IGetPayloadBodiesByRangeV2Handler _executionGetPayloadBodiesByRangeV2Handler = getPayloadBodiesByRangeV2Handler;

    private readonly IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?> _getBlobsHandlerV4 = getBlobsHandlerV4;

    public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId)
        => _getPayloadHandlerV6.HandleAsync(payloadId);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV4 executionPayload, Hash256[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests), EngineApiVersions.NewPayload.V5);

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV4(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null, BitArray? custodyColumns = null)
    {
        // Per execution-apis #793: custody-column updates are best-effort, errors swallowed.
        // No EL-side custody consumer wired yet — log at trace level so the CL request is auditable.
        // TODO(custody): once a consumer is wired, validate custodyColumns.Length == 128 here
        // (the SSZ wire enforces this on REST, but the JSON-RPC signature does not).
        if (custodyColumns is not null && _logger.IsTrace)
            _logger.Trace($"engine_forkchoiceUpdatedV4 received custody columns ({custodyColumns.Count} bits) — not yet applied");
        return ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V4);
    }

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByHashV2(IReadOnlyList<Hash256> blockHashes)
        => _executionGetPayloadBodiesByHashV2Handler.Handle(blockHashes);

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(ulong start, ulong count)
        => _executionGetPayloadBodiesByRangeV2Handler.Handle(start, count);

    public Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> engine_getBlobsV4(byte[][] blobVersionedHashes, System.Collections.BitArray indicesBitarray)
        => _getBlobsHandlerV4.HandleAsync(new(blobVersionedHashes, indicesBitarray));
}
