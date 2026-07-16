// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    public Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1(Hash256 blockHash)
        => getInclusionListTransactionsHandler.Handle(blockHash);

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
        return ResultWrapper<PayloadStatusV2>.Success(new PayloadStatusV2
        {
            Status = unsatisfied ? PayloadStatus.Valid : status.Status,
            LatestValidHash = status.LatestValidHash,
            ValidationError = status.ValidationError,
            InclusionListSatisfied = status.Status switch
            {
                PayloadStatus.InclusionListUnsatisfied => false,
                PayloadStatus.Valid => true,
                _ => null
            }
        });
    }

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV5(
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
        return ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V5);
    }
}
