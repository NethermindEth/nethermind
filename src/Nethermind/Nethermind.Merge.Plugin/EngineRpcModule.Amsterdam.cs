// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    private readonly IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV4>, NewPayloadWithWitnessV1Result> _newPayloadWithWitnessHandlerV5 = newPayloadWithWitnessHandlerV5;

    private readonly IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?> _getBlobsHandlerV4 = getBlobsHandlerV4;

    public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId)
        => _getPayloadHandlerV6.HandleAsync(payloadId);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests), EngineApiVersions.NewPayload.V5);

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV5(
        ExecutionPayloadV4 executionPayload,
        Hash256?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests)
        => _newPayloadWithWitnessHandlerV5.HandleAsync(
            new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests));

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV4(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null, BitArray? custodyColumns = null)
        => ValidateAndApplyCustodyColumns(custodyColumns) is { } error
            ? Task.FromResult(ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error, ErrorCodes.InvalidParams))
            : ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V4);

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByHashV2(
        IReadOnlyList<Hash256> blockHashes)
        => _executionGetPayloadBodiesByHashV2Handler.Handle(blockHashes);

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(ulong start, ulong count)
        => _executionGetPayloadBodiesByRangeV2Handler.Handle(start, count);

    public Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> engine_getBlobsV4(byte[][] blobVersionedHashes, BitArray indicesBitarray)
        => _getBlobsHandlerV4.HandleAsync(new(blobVersionedHashes, indicesBitarray));

    /// <summary>Validates a custody-column update carried by a forkchoice call, and applies it
    /// (execution-apis#793).</summary>
    /// <remarks>Shared by every forkchoice version that accepts the field, so the validation and the
    /// tracker update cannot drift apart between them. Applying is best-effort per execution-apis#793 —
    /// a failure is logged and swallowed; only a malformed bitfield is reported back to the caller.</remarks>
    /// <returns><c>null</c> when there is nothing to reject, otherwise the <c>InvalidParams</c> message.</returns>
    private string? ValidateAndApplyCustodyColumns(BitArray? custodyColumns)
    {
        if (custodyColumns is null) return null;
        if (custodyColumns.Length != BlobCellMask.CellCount)
            return $"Custody columns must be exactly {BlobCellMask.FixedByteLength} bytes.";

        try
        {
            Span<byte> bytes = stackalloc byte[BlobCellMask.FixedByteLength];
            bytes.Clear();
            for (int i = 0; i < BlobCellMask.CellCount; i++)
            {
                if (custodyColumns.Get(i))
                {
                    bytes[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            _blobCustodyTracker.Update(BlobCellMask.FromBytes(bytes));
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to update blob custody columns: {ex.Message}");
        }

        return null;
    }
}
