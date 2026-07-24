// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    public async Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions)
    {
        ResultWrapper<PayloadStatusV1> result = await NewPayload(
            new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions),
            EngineApiVersions.NewPayload.V6);

        if (result.Result.ResultType != ResultType.Success)
            return ResultWrapper<PayloadStatusV2>.Fail(result.Result.Error!, result.ErrorCode, result.IsTemporary);

        // execution-apis#609: report IL compliance via inclusionListSatisfied and keep status VALID.
        // The internal pipeline flags a censoring payload with the INCLUSION_LIST_UNSATISFIED status.
        PayloadStatusV1 status = result.Data;
        bool unsatisfied = status.Status == PayloadStatus.InclusionListUnsatisfied;
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
            Status = unsatisfied ? PayloadStatus.Valid : status.Status,
            LatestValidHash = status.LatestValidHash,
            ValidationError = status.ValidationError,
            InclusionListSatisfied = inclusionListSatisfied
        });
    }

    public async Task<ResultWrapper<ForkchoiceUpdatedV2Result>> engine_forkchoiceUpdatedV5(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        byte[]? custodyColumns = null)
    {
        if (payloadAttributes?.InclusionListTransactions is { } ilTxs)
        {
            IReleaseSpec spec = _specProvider.GetSpec(ForkActivation.TimestampOnly(payloadAttributes.Timestamp));
            // An unparsable IL is a no-op, not a protocol error.
            try
            {
                inclusionListTxSource.Set(ilTxs, spec);
            }
            catch (Exception ex) when (ex is RlpException or ArgumentException)
            {
                if (_logger.IsDebug) _logger.Debug($"engine_forkchoiceUpdatedV5: discarding malformed inclusion list ({ex.GetType().Name}: {ex.Message})");
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
}
