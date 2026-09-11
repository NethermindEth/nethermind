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
    /// <summary>Entries the retained-inclusion-list cache holds.</summary>
    /// <remarks>
    /// Each entry is bounded by <see cref="Eip7805Constants.MaxAggregateInclusionListBytes"/> (128 KiB), so this
    /// caps retention at ~2 MiB. Only a branch tip is ever asked for an answer and bogota.md allows discarding
    /// the rest, so this only has to span the few unresolved payloads a lagging processor can leave behind.
    /// </remarks>
    private const int RetainedInclusionListCapacity = 16;

    // Inclusion-list compliance computed during engine_newPayloadV6, retained so a later
    // engine_forkchoiceUpdatedV5 to that head can report it (execution-apis#609).
    private readonly LruCache<Hash256, bool> _inclusionListSatisfiedByBlock = new(64, "inclusionListSatisfied");

    // bogota.md engine_newPayloadV6 (3): the list itself is retained for payloads newPayloadV6 could not
    // answer for, so a forkchoice update that promotes one to a VALID head can still answer (:163-169).
    private readonly LruCache<Hash256, byte[][]> _retainedInclusionLists = new(RetainedInclusionListCapacity, "retainedInclusionLists");

    private readonly IInclusionListComplianceEvaluator _inclusionListComplianceEvaluator = inclusionListComplianceEvaluator;

    private readonly IAsyncHandler<InclusionListExecutionPayloadParams, NewPayloadWithWitnessV1Result> _newPayloadWithWitnessHandlerV6 = newPayloadWithWitnessHandlerV6;

    public Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1(Hash256? parentBlockHash = null)
        => getInclusionListTransactionsHandler.Handle(parentBlockHash);

    public Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions)
        => NewPayloadWithInclusionList(
            new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions),
            EngineApiVersions.NewPayload.V6);

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
        {
            _inclusionListSatisfiedByBlock.Set(validHash, satisfied);
        }
        else if (status.Status is PayloadStatus.Accepted or PayloadStatus.Syncing
            && executionPayloadParams is { InclusionListTransactions: { } retained, ExecutionPayload.BlockHash: { } blockHash })
        {
            // Those two statuses carry no answer but can still become a VALID head, so keep the list.
            _retainedInclusionLists.Set(blockHash, retained);
        }

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
        => ValidateAndApplyCustodyColumns(custodyColumns) is { } error
            ? Task.FromResult(ResultWrapper<ForkchoiceUpdatedV2Result>.Fail(error, ErrorCodes.InvalidParams))
            : ForkchoiceUpdatedWithInclusionList(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V5);

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
                inclusionListTxSource.Set(ilTxs, spec);
            }
        }

        ResultWrapper<ForkchoiceUpdatedV1Result> result = await ForkchoiceUpdated(forkchoiceState, payloadAttributes, version);
        if (result.Result.ResultType != ResultType.Success)
            return ResultWrapper<ForkchoiceUpdatedV2Result>.Fail(result.Result.Error!, result.ErrorCode, result.IsTemporary);

        bool? inclusionListSatisfied = result.Data.PayloadStatus.Status == PayloadStatus.Valid
            ? GetInclusionListSatisfied(forkchoiceState.HeadBlockHash)
            : null;

        return ResultWrapper<ForkchoiceUpdatedV2Result>.Success(ForkchoiceUpdatedV2Result.From(result.Data, inclusionListSatisfied));
    }

    /// <summary>Inclusion-list compliance of a <c>VALID</c> forkchoice head (bogota.md
    /// <c>engine_forkchoiceUpdatedV5</c> (2)).</summary>
    /// <remarks>
    /// Prefers the answer <c>engine_newPayloadV6</c> already computed, falling back to the list retained for a
    /// payload that resolved to <c>ACCEPTED</c>/<c>SYNCING</c>. Decoding and sender recovery are deferred to
    /// that fallback, so a head with nothing retained costs one cache probe. Both caches are lock-guarded and
    /// the answer is a pure function of (block, list), so concurrent calls for one head recompute the same value.
    /// </remarks>
    /// <returns><c>null</c> when no list was retained for the head, or its state is no longer readable.</returns>
    private bool? GetInclusionListSatisfied(Hash256 headBlockHash)
    {
        if (_inclusionListSatisfiedByBlock.TryGet(headBlockHash, out bool satisfied)) return satisfied;
        if (!_retainedInclusionLists.TryGet(headBlockHash, out byte[][]? retained)) return null;

        if (_inclusionListComplianceEvaluator.TryEvaluate(headBlockHash, retained) is not { } evaluated)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot evaluate the inclusion list of head {headBlockHash}; reporting inclusionListSatisfied as null.");
            return null;
        }

        // The answer supersedes the bytes, and the head is no longer a payload awaiting one.
        _inclusionListSatisfiedByBlock.Set(headBlockHash, evaluated);
        _retainedInclusionLists.Delete(headBlockHash);
        return evaluated;
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
