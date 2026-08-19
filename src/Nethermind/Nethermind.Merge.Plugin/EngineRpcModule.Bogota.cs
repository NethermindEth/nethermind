// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    // EIP-7805 (FOCIL): the inclusion-list compliance computed while validating a block via
    // engine_newPayloadV6 is retained here so engine_forkchoiceUpdatedV5 can report it for a VALID
    // head (execution-apis#609 — "using retained inclusion-list transactions if validation happens
    // during this call"). Bounded; a null entry means "not computed", which FCU reports as null.
    private readonly LruCache<Hash256, bool> _inclusionListSatisfiedByBlock = new(64, "inclusionListSatisfied");

    public Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1()
        => getInclusionListTransactionsHandler.Handle();

    public Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions)
        => NewPayloadWithInclusionList(
            new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions),
            EngineApiVersions.NewPayload.V6);

    /// <summary>Runs <see cref="NewPayload"/> and maps its result onto the Bogota <see cref="PayloadStatusV2"/> shape.</summary>
    protected async Task<ResultWrapper<PayloadStatusV2>> NewPayloadWithInclusionList(IExecutionPayloadParams executionPayloadParams, int version)
    {
        ResultWrapper<PayloadStatusV1> result = await NewPayload(executionPayloadParams, version);

        if (result.Result.ResultType != ResultType.Success)
            return ResultWrapper<PayloadStatusV2>.Fail(result.Result.Error!, result.ErrorCode, result.IsTemporary);

        // execution-apis#609: report IL compliance via inclusionListSatisfied and keep status VALID.
        // The internal pipeline flags a censoring payload with the INCLUSION_LIST_UNSATISFIED status.
        PayloadStatusV1 status = result.Data;
        // Both IL statuses are pipeline-internal: the block is VALID on the wire and the compliance
        // answer moves into inclusionListSatisfied, where null means "never evaluated".
        bool internalIlStatus = status.Status is PayloadStatus.InclusionListUnsatisfied or PayloadStatus.InclusionListNotEvaluated;
        bool? inclusionListSatisfied = status.Status switch
        {
            PayloadStatus.InclusionListUnsatisfied => false,
            PayloadStatus.Valid => true,
            _ => null
        };

        // Retain per-block so a later forkchoiceUpdatedV5 to this head can report the same result.
        if (inclusionListSatisfied is { } satisfied && status.LatestValidHash is { } validHash)
            _inclusionListSatisfiedByBlock.Set(validHash, satisfied);

        return ResultWrapper<PayloadStatusV2>.Success(new PayloadStatusV2
        {
            Status = internalIlStatus ? PayloadStatus.Valid : status.Status,
            LatestValidHash = status.LatestValidHash,
            ValidationError = status.ValidationError,
            InclusionListSatisfied = inclusionListSatisfied
        });
    }

    public async Task<ResultWrapper<ForkchoiceUpdatedV2Result>> engine_forkchoiceUpdatedV5(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        BitArray? custodyColumns = null)
    {
        // Out of fork the attributes are rejected below with -38005, so don't pay the decode first.
        if (payloadAttributes?.InclusionListTransactions is { } ilTxs
            && _specProvider.GetSpec(ForkActivation.TimestampOnly(payloadAttributes.Timestamp)) is { IsEip7805Enabled: true } spec)
        {
            // Bound the aggregate before the (expensive) RLP decode + sender recovery, matching the
            // newPayloadV6 input cap. An oversized or unparsable IL is a no-op, not a protocol error.
            if (ExceedsAggregateInclusionListBound(ilTxs))
            {
                // Warn once per FCU (not per improvement iteration) — the block will build without the IL.
                if (_logger.IsWarn) _logger.Warn($"engine_forkchoiceUpdatedV5: discarding oversized inclusion list ({ilTxs.Length} entries); building without it.");
            }
            else
            {
                try
                {
                    inclusionListTxSource.Set(ilTxs, spec);
                }
                catch (Exception ex) when (ex is RlpException or ArgumentException)
                {
                    if (_logger.IsWarn) _logger.Warn($"engine_forkchoiceUpdatedV5: discarding malformed inclusion list ({ex.GetType().Name}: {ex.Message}); building without it.");
                }
            }
        }

        ResultWrapper<ForkchoiceUpdatedV1Result> result = await ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V5);
        if (result.Result.ResultType != ResultType.Success)
            return ResultWrapper<ForkchoiceUpdatedV2Result>.Fail(result.Result.Error!, result.ErrorCode, result.IsTemporary);

        // execution-apis#609: report inclusion-list compliance for a VALID head from the result
        // retained when the head was validated via engine_newPayloadV6; null when not available.
        bool? inclusionListSatisfied = result.Data.PayloadStatus.Status == PayloadStatus.Valid
            && _inclusionListSatisfiedByBlock.TryGet(forkchoiceState.HeadBlockHash, out bool satisfied)
            ? satisfied
            : null;

        return ResultWrapper<ForkchoiceUpdatedV2Result>.Success(ForkchoiceUpdatedV2Result.From(result.Data, inclusionListSatisfied));
    }

    // Mirrors the newPayloadV6 aggregate bound (IExecutionPayloadParams.ValidateInitialParams).
    private static bool ExceedsAggregateInclusionListBound(byte[][] inclusionListTransactions)
    {
        if (inclusionListTransactions.Length > Eip7805Constants.MaxAggregateInclusionListTransactions) return true;
        long totalBytes = 0;
        for (int i = 0; i < inclusionListTransactions.Length; i++)
        {
            totalBytes += inclusionListTransactions[i]?.Length ?? 0;
            if (totalBytes > Eip7805Constants.MaxAggregateInclusionListBytes) return true;
        }
        return false;
    }
}
