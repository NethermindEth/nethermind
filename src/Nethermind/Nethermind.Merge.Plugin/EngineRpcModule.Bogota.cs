// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Trie;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    /// <summary>Entries the per-block inclusion-list cache holds.</summary>
    /// <remarks>
    /// A retained entry is bounded by <see cref="Eip7805Constants.MaxAggregateInclusionListBytes"/> (128 KiB), so
    /// this caps retention at ~2 MiB. Only a branch tip is ever asked for an answer and bogota.md allows discarding
    /// the rest, so this only has to span the few unresolved payloads a lagging processor can leave behind.
    /// </remarks>
    private const int InclusionListCacheCapacity = 16;

    /// <summary>What is known about a block's inclusion-list compliance: either the answer
    /// <c>engine_newPayloadV6</c> computed, or the list retained for a payload it could not answer for.</summary>
    private readonly record struct InclusionListState(bool? Answer, byte[][]? Retained);

    // Compliance computed during engine_newPayloadV6, kept so a later engine_forkchoiceUpdatedV5 to that head can
    // report it (execution-apis#609); bogota.md engine_newPayloadV6 (3) keeps the list itself for payloads
    // newPayloadV6 could not answer for. One entry per block, because the list is a per-call parameter rather than
    // a property of the block: a list from a newer call must not be shadowed by the answer to an older one.
    private readonly LruCache<Hash256, InclusionListState> _inclusionListByBlock = new(InclusionListCacheCapacity, "inclusionListByBlock");

    // The cache's own lock makes each operation atomic, not the read-evaluate-publish sequence in
    // GetInclusionListSatisfied, which must not overwrite a list a concurrent engine_newPayloadV6 retained.
    private readonly Lock _inclusionListLock = new();

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
            SetInclusionListState(validHash, new InclusionListState(satisfied, null));
        }
        else if (status.Status is PayloadStatus.Accepted or PayloadStatus.Syncing
            && executionPayloadParams is { InclusionListTransactions: { } retained, ExecutionPayload.BlockHash: { } blockHash })
        {
            // Those two statuses carry no answer but can still become a VALID head, so keep the list — which
            // replaces any answer an earlier call cached, since that was computed for a different list.
            SetInclusionListState(blockHash, new InclusionListState(null, retained));
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
    /// Uses the answer <c>engine_newPayloadV6</c> already computed, falling back to the list retained for a
    /// payload that resolved to <c>ACCEPTED</c>/<c>SYNCING</c>. Decoding and sender recovery are deferred to
    /// that fallback, so a head with nothing retained costs one cache probe. The answer is a pure function of
    /// (block, list), so concurrent calls for one head recompute the same value.
    /// </remarks>
    /// <returns><c>null</c> when no list was retained for the head, or its state is no longer readable.</returns>
    private bool? GetInclusionListSatisfied(Hash256 headBlockHash)
    {
        if (!_inclusionListByBlock.TryGet(headBlockHash, out InclusionListState state)) return null;
        if (state.Answer is { } satisfied) return satisfied;
        if (state.Retained is not { } retained) return null;

        bool? evaluated;
        try
        {
            // An ecrecover per retained transaction plus a state read per sender, charged to the forkchoice
            // response the consensus client awaits for payloadId. Accepted: only a head its own newPayloadV6
            // left unanswered pays it, and publishing the answer below caps it at once per head.
            evaluated = _inclusionListComplianceEvaluator.TryEvaluate(headBlockHash, retained);
        }
        // The head is already applied and a build may be under way, and bogota.md permits a null answer, so
        // a state read that hits a pruned or still-healing subtrie must not fail the forkchoice update.
        catch (TrieException exception)
        {
            _logger.DebugError($"Cannot evaluate the inclusion list of head {headBlockHash}: {exception}");
            return null;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Cannot evaluate the inclusion list of head {headBlockHash}: {exception}");
            return null;
        }

        if (evaluated is not { } answer)
        {
            // Routine while the head's state is still healing, and the entry survives so the next update retries.
            if (_logger.IsDebug) _logger.Debug($"Cannot evaluate the inclusion list of head {headBlockHash}; reporting inclusionListSatisfied as null.");
            return null;
        }

        // Publish only while the list the answer was computed for is still the retained one: a concurrent
        // engine_newPayloadV6 may have replaced it, and the newer list outranks this answer.
        lock (_inclusionListLock)
        {
            if (_inclusionListByBlock.TryGet(headBlockHash, out InclusionListState current)
                && ReferenceEquals(current.Retained, retained))
            {
                _inclusionListByBlock.Set(headBlockHash, new InclusionListState(answer, null));
            }
        }

        return answer;
    }

    private void SetInclusionListState(Hash256 blockHash, InclusionListState state)
    {
        lock (_inclusionListLock)
        {
            _inclusionListByBlock.Set(blockHash, state);
        }
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
