// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    // Inclusion-list compliance computed during engine_newPayloadV6, retained so a later
    // engine_forkchoiceUpdatedV5 to that head can report it (execution-apis#609).
    private readonly LruCache<Hash256, bool> _inclusionListSatisfiedByBlock = new(64, "inclusionListSatisfied");

    private readonly IAsyncHandler<InclusionListExecutionPayloadParams, NewPayloadWithWitnessV1Result> _newPayloadWithWitnessHandlerV6 = newPayloadWithWitnessHandlerV6;

    private readonly IAsyncHandler<byte[], GetPayloadV7Result?> _getPayloadHandlerV7 = getPayloadHandlerV7;

    public Task<ResultWrapper<GetPayloadV7Result?>> engine_getPayloadV7(byte[] payloadId)
        => _getPayloadHandlerV7.HandleAsync(payloadId);

    public Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1(Hash256? parentBlockHash = null)
        => getInclusionListTransactionsHandler.Handle(parentBlockHash);

    /// <remarks>A list sent without its membership, as an older consensus client sends, carries no committee
    /// provenance, so the EIP-8369 per-IL VERIFY budget is not applied to it.</remarks>
    public Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions, byte[][]? inclusionListMembership = null, InclusionListClaim[]? inclusionListClaims = null)
    {
        if (ValidateMembershipAndClaims(inclusionListTransactions, inclusionListMembership, inclusionListClaims) is { } error)
            return Task.FromResult(ResultWrapper<PayloadStatusV2>.Fail(error, ErrorCodes.InvalidParams));

        executionPayload.InclusionListMembership = inclusionListMembership;
        executionPayload.InclusionListClaims = inclusionListClaims;
        return NewPayloadWithInclusionList(
            new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions),
            EngineApiVersions.NewPayload.V6);
    }

    /// <summary>bogota.md <c>engine_newPayloadV6</c> point 2; a missing list is left to the aggregate checks.</summary>
    private static string? ValidateMembershipAndClaims(byte[][]? transactions, byte[][]? membership, InclusionListClaim[]? claims) =>
        claims is { Length: > Eip8369Constants.MaxInclusionListClaims }
            ? $"Inclusion list claims exceed the maximum of {Eip8369Constants.MaxInclusionListClaims}"
            : membership is null
                ? null
                : transactions is null
                    ? "Inclusion list membership requires inclusion list transactions"
                    : InclusionListMembership.Validate(transactions, membership);

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV6(
        ExecutionPayloadV4 executionPayload,
        Hash256?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests,
        byte[][]? inclusionListTransactions)
        => _newPayloadWithWitnessHandlerV6.HandleAsync(
            new InclusionListExecutionPayloadParams(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions));

    /// <summary>Runs <see cref="NewPayload"/> and maps its result onto the Bogota <see cref="PayloadStatusV2"/> shape.</summary>
    protected async Task<ResultWrapper<PayloadStatusV2>> NewPayloadWithInclusionList(IExecutionPayloadParams executionPayloadParams, int version)
    {
        ResultWrapper<PayloadStatusV1> result = await NewPayload(executionPayloadParams, version);

        if (result.Result.ResultType != ResultType.Success)
            return ResultWrapper<PayloadStatusV2>.Fail(result.Result.Error!, result.ErrorCode, result.IsTemporary);

        PayloadStatusV1 status = result.Data;
        // INCLUSION_LIST_UNSATISFIED is pipeline-internal: on the wire the block is VALID and the
        // compliance answer moves into inclusionListSatisfied (execution-apis#609).
        bool unsatisfied = status.Status == PayloadStatus.InclusionListUnsatisfied;
        bool? inclusionListSatisfied = status.Status switch
        {
            PayloadStatus.InclusionListUnsatisfied => false,
            PayloadStatus.Valid => true,
            _ => null
        };

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

    public Task<ResultWrapper<ForkchoiceUpdatedV2Result>> engine_forkchoiceUpdatedV5(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        BitArray? custodyColumns = null)
    {
        if (ValidateAndApplyCustodyColumns(custodyColumns) is { } error)
            return Task.FromResult(ResultWrapper<ForkchoiceUpdatedV2Result>.Fail(error, ErrorCodes.InvalidParams));
        // bogota.md engine_forkchoiceUpdatedV5 point 1.1 precedes the fork check.
        if (payloadAttributes is not null && ValidateMembershipAndClaims(payloadAttributes.InclusionListTransactions, payloadAttributes.InclusionListMembership, null) is { } attributesError)
            return Task.FromResult(ResultWrapper<ForkchoiceUpdatedV2Result>.Fail(attributesError, MergeErrorCodes.InvalidPayloadAttributes));

        return ForkchoiceUpdatedWithInclusionList(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V5);
    }

    /// <summary>Registers any inclusion list for the build, then runs <see cref="ForkchoiceUpdated"/> and maps
    /// its result onto the Bogota <see cref="ForkchoiceUpdatedV2Result"/> shape.</summary>
    protected async Task<ResultWrapper<ForkchoiceUpdatedV2Result>> ForkchoiceUpdatedWithInclusionList(
        ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, int version)
    {
        // Out of fork the attributes are rejected below with -38005, so don't retain the list at all.
        if (payloadAttributes?.InclusionListTransactions is { } ilTxs
            && _specProvider.GetSpec(ForkActivation.TimestampOnly(payloadAttributes.Timestamp)) is { IsEip7805Enabled: true } spec)
        {
            // An oversized IL is a no-op, not a protocol error. Set only registers the list: decoding and
            // sender recovery are deferred to the build, so an update that never builds pays nothing.
            if (ExceedsAggregateInclusionListBound(ilTxs))
            {
                if (_logger.IsWarn) _logger.Warn($"engine_forkchoiceUpdatedV{version}: discarding oversized inclusion list ({ilTxs.Length} entries); building without it.");
            }
            else
            {
                inclusionListTxSource.Set(ilTxs, spec, payloadAttributes.InclusionListMembership);
            }
        }

        ResultWrapper<ForkchoiceUpdatedV1Result> result = await ForkchoiceUpdated(forkchoiceState, payloadAttributes, version);
        if (result.Result.ResultType != ResultType.Success)
            return ResultWrapper<ForkchoiceUpdatedV2Result>.Fail(result.Result.Error!, result.ErrorCode, result.IsTemporary);

        // execution-apis#609: report compliance retained from the head's engine_newPayloadV6 validation.
        // The list is not part of the block body, so a head this process never validated leaves nothing
        // to re-derive from and the field stays null.
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
