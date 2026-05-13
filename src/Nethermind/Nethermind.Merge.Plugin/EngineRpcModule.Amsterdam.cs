// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
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
    private readonly IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofsV1?>?> _getBlobsHandlerV4 = getBlobsHandlerV4;

    public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId)
        => _getPayloadHandlerV6.HandleAsync(payloadId);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV4 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests), EngineApiVersions.NewPayload.V5);

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV4(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null, byte[]? custodyColumns = null)
    {
        BlobCellMask? custodyMask = null;
        if (custodyColumns is not null)
        {
            if (!TryGetBlobCellMask(custodyColumns, out BlobCellMask parsedMask, out string? error))
            {
                return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error!, ErrorCodes.InvalidParams);
            }

            custodyMask = parsedMask;
        }

        ResultWrapper<ForkchoiceUpdatedV1Result> result = await ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V4);
        if (result.Result == Result.Success && custodyMask is BlobCellMask mask)
        {
            _blobCustodyTracker.Update(mask);
        }

        return result;
    }

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByHashV2(IReadOnlyList<Hash256> blockHashes)
        => _executionGetPayloadBodiesByHashV2Handler.Handle(blockHashes);

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(long start, long count)
        => _executionGetPayloadBodiesByRangeV2Handler.Handle(start, count);

    public Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofsV1?>?>> engine_getBlobsV4(byte[][] blobVersionedHashes, byte[]? indicesBitarray)
    {
        if (!TryGetBlobCellMask(indicesBitarray, out BlobCellMask cellMask, out string? error))
        {
            return Task.FromResult(ResultWrapper<IReadOnlyList<BlobCellsAndProofsV1?>?>.Fail(error!, ErrorCodes.InvalidParams));
        }

        return _getBlobsHandlerV4.HandleAsync(new(blobVersionedHashes, cellMask));
    }

    private static bool TryGetBlobCellMask(byte[]? bitarray, out BlobCellMask cellMask, out string? error)
    {
        cellMask = default;
        if (bitarray is not { Length: BlobCellMask.FixedByteLength })
        {
            error = $"Blob cell bitarray must be exactly {BlobCellMask.FixedByteLength} bytes.";
            return false;
        }

        cellMask = BlobCellMask.FromBytes(bitarray);
        error = null;
        return true;
    }
}
