// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IInclusionChecks _inclusionChecks = inclusionChecks;

    private readonly IAsyncHandler<InclusionListExecutionPayloadParams, NewPayloadWithWitnessV1Result> _newPayloadWithWitnessHandlerV6 = newPayloadWithWitnessHandlerV6;

    private readonly IAsyncHandler<byte[], GetPayloadV7Result?> _getPayloadHandlerV7 = getPayloadHandlerV7;

    public Task<ResultWrapper<GetPayloadV7Result?>> engine_getPayloadV7(byte[] payloadId)
        => _getPayloadHandlerV7.HandleAsync(payloadId);

    public Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1(Hash256? parentBlockHash = null)
        => getInclusionListTransactionsHandler.Handle(parentBlockHash);

    public Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests, byte[][][]? inclusionLists, OmissionClaim[]? omissionClaims)
        => NewPayloadWithInclusionCheck(
            new InclusionListExecutionPayloadParams(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionLists, omissionClaims),
            EngineApiVersions.NewPayload.V6);

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV6(
        ExecutionPayloadV4 executionPayload,
        Hash256?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests,
        byte[][][]? inclusionLists,
        OmissionClaim[]? omissionClaims)
        => _newPayloadWithWitnessHandlerV6.HandleAsync(
            new InclusionListExecutionPayloadParams(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionLists, omissionClaims));

    public Task<ResultWrapper<bool?>> engine_getInclusionListVerdictV1(Hash256 inclusionCheckId)
        => Task.FromResult(_inclusionChecks.TryGetVerdict(inclusionCheckId, out bool? verdict)
            ? ResultWrapper<bool?>.Success(verdict)
            : ResultWrapper<bool?>.Fail(MergeErrorMessages.UnknownInclusionCheck, MergeErrorCodes.UnknownInclusionCheck));

    /// <summary>Runs <see cref="NewPayload"/> and, once execution is <c>VALID</c>, schedules the inclusion check for
    /// this call's inputs (bogota.md § Deferred inclusion checking).</summary>
    /// <remarks>Execution validity is answered at once: the verdict is returned only if the identified check has
    /// already completed, and is otherwise polled through <c>engine_getInclusionListVerdictV1</c>.</remarks>
    protected async Task<ResultWrapper<PayloadStatusV2>> NewPayloadWithInclusionCheck(ExecutionPayloadParams<ExecutionPayloadV4> executionPayloadParams, int version)
    {
        ResultWrapper<PayloadStatusV1> result = await NewPayload(executionPayloadParams, version);

        if (result.Result.ResultType != ResultType.Success)
            return ResultWrapper<PayloadStatusV2>.Fail(result.Result.Error!, result.ErrorCode, result.IsTemporary);

        PayloadStatusV1 status = result.Data;
        PayloadStatusV2 statusV2 = new()
        {
            Status = status.Status,
            LatestValidHash = status.LatestValidHash,
            ValidationError = status.ValidationError
        };

        // newPayload validated both before it could answer VALID.
        if (status.Status == PayloadStatus.Valid
            && executionPayloadParams is { InclusionLists: { } inclusionLists, OmissionClaims: { } claims })
        {
            ExecutionPayload executionPayload = executionPayloadParams.ExecutionPayload;
            Hash256 inclusionCheckId = InclusionCheckId.Compute(executionPayload.BlockHash, inclusionLists, claims);
            statusV2.InclusionCheckId = inclusionCheckId;
            statusV2.InclusionListSatisfied = _inclusionChecks.Start(inclusionCheckId, executionPayload, inclusionLists, claims);
        }

        return ResultWrapper<PayloadStatusV2>.Success(statusV2);
    }

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV5(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        BitArray? custodyColumns = null)
    {
        if (ValidateAndApplyCustodyColumns(custodyColumns) is { } error)
            return Task.FromResult(ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error, ErrorCodes.InvalidParams));

        // bogota.md engine_forkchoiceUpdatedV5 point 1.1 precedes the fork check. Absent lists are left to the
        // attributes' own version checks.
        if (payloadAttributes?.InclusionLists is { } inclusionLists)
        {
            if (InclusionLists.Validate(inclusionLists) is { } attributesError)
                return Task.FromResult(ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(attributesError, MergeErrorCodes.InvalidPayloadAttributes));

            // Out of fork the attributes are rejected with -38005, so the lists are not retained at all. Set only
            // registers them: decoding and sender recovery are deferred to the build, so an update that never builds
            // pays nothing.
            if (_specProvider.GetSpec(ForkActivation.TimestampOnly(payloadAttributes.Timestamp)) is { IsEip7805Enabled: true } spec)
                inclusionListTxSource.Set(inclusionLists, spec);
        }

        // Forkchoice updates report execution validity only; inclusion verdicts are polled by identifier.
        return ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V5);
    }
}
