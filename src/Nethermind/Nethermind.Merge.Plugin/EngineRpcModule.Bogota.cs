// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Trie;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    /// <summary>An inclusion list retained for a payload that could not yet be validated.</summary>
    private readonly record struct RetainedInclusionList(Hash256 ParentHash, ulong Number, byte[][] Transactions, bool Accepted);
    private readonly record struct InclusionListAnswer(Hash256 ParentHash, ulong Number, bool Satisfied);

    // EIP-7805 requires retaining ACCEPTED branch tips until they cease to be tips.
    private readonly Dictionary<Hash256, InclusionListAnswer> _inclusionListAnswers = [];
    private readonly Dictionary<Hash256, RetainedInclusionList> _retainedInclusionLists = [];
    private readonly Dictionary<Hash256, int> _inclusionListChildCounts = [];
    // bogota.md mandates retaining ACCEPTED tips; only best-effort SYNCING lists can be evicted.
    private readonly LinkedList<Hash256> _syncingInclusionListOrder = new();
    private readonly Dictionary<Hash256, LinkedListNode<Hash256>> _syncingInclusionListNodes = [];
    private readonly IBlockTree _inclusionListBlockTree = blockTree;
    private const int MaxRetainedSyncingInclusionLists = 64;
    private ulong _finalizedInclusionListNumber;
    private Hash256? _finalizedInclusionListHash;

    // A concurrent engine_newPayloadV6 must not have its newer list overwritten by an earlier evaluation.
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
            SetInclusionListAnswer(validHash, executionPayloadParams.ExecutionPayload.ParentHash,
                executionPayloadParams.ExecutionPayload.BlockNumber, satisfied);
        }
        else if (status.Status is PayloadStatus.Accepted or PayloadStatus.Syncing
            && executionPayloadParams is { InclusionListTransactions: { } retained, ExecutionPayload.BlockHash: { } blockHash })
        {
            SetRetainedInclusionList(blockHash, executionPayloadParams.ExecutionPayload.ParentHash,
                executionPayloadParams.ExecutionPayload.BlockNumber, retained,
                status.Status == PayloadStatus.Accepted);
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

        if (result.Data.PayloadStatus.Status is PayloadStatus.Valid or PayloadStatus.Syncing)
            PruneFinalizedInclusionLists(forkchoiceState.FinalizedBlockHash);

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
    /// that fallback, so a head with nothing retained costs only dictionary lookups. The answer is a pure function of
    /// (block, list), so concurrent calls for one head recompute the same value.
    /// </remarks>
    /// <returns><c>null</c> when no list was retained for the head, or its state is no longer readable.</returns>
    private bool? GetInclusionListSatisfied(Hash256 headBlockHash)
    {
        byte[][] retained;
        lock (_inclusionListLock)
        {
            if (_inclusionListAnswers.TryGetValue(headBlockHash, out InclusionListAnswer cached)) return cached.Satisfied;
            if (!_retainedInclusionLists.TryGetValue(headBlockHash, out RetainedInclusionList state)) return null;
            retained = state.Transactions;
        }

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
            // Unlike a pruned or healing subtrie, nothing failing here is expected to succeed later, so forget
            // the list instead of re-throwing and re-logging on every later update to this head.
            PublishInclusionListAnswer(headBlockHash, retained, null);
            return null;
        }

        if (evaluated is not { } answer)
        {
            // Routine while the head's state is still healing, and the entry survives so the next update retries.
            if (_logger.IsDebug) _logger.Debug($"Cannot evaluate the inclusion list of head {headBlockHash}; reporting inclusionListSatisfied as null.");
            return null;
        }

        PublishInclusionListAnswer(headBlockHash, retained, answer);

        return answer;
    }

    private void SetInclusionListAnswer(Hash256 blockHash, Hash256 parentHash, ulong number, bool answer)
    {
        lock (_inclusionListLock)
        {
            if (!IsAfterFinalization(blockHash, number)) return;
            RemoveRetainedInclusionList(blockHash);
            RemoveRetainedInclusionList(parentHash);
            RemoveInclusionListAnswer(parentHash);
            RemoveInclusionListAnswer(blockHash);
            _inclusionListAnswers[blockHash] = new InclusionListAnswer(parentHash, number, answer);
            TrackInclusionListChild(parentHash);
        }
    }

    internal void SetRetainedInclusionList(Hash256 blockHash, Hash256 parentHash, ulong number, byte[][] retained, bool accepted)
    {
        lock (_inclusionListLock)
        {
            RemoveInclusionListAnswer(blockHash);
            if (!IsAfterFinalization(blockHash, number)) return;
            if (accepted)
            {
                RemoveRetainedInclusionList(parentHash);
                RemoveInclusionListAnswer(parentHash);
            }
            // An out-of-order parent is not a branch tip if an unresolved child is already retained.
            if (_inclusionListChildCounts.ContainsKey(blockHash)) return;
            RemoveRetainedInclusionList(blockHash);
            _retainedInclusionLists[blockHash] = new RetainedInclusionList(parentHash, number, retained, accepted);
            if (accepted) TrackInclusionListChild(parentHash);
            else
            {
                _syncingInclusionListNodes[blockHash] = _syncingInclusionListOrder.AddLast(blockHash);
                if (_syncingInclusionListNodes.Count > MaxRetainedSyncingInclusionLists)
                    RemoveRetainedInclusionList(_syncingInclusionListOrder.First!.Value);
            }
        }
    }

    internal bool HasRetainedInclusionList(Hash256 blockHash)
    {
        lock (_inclusionListLock) return _retainedInclusionLists.ContainsKey(blockHash);
    }

    internal int RetainedInclusionListCount
    {
        get { lock (_inclusionListLock) return _retainedInclusionLists.Count; }
    }

    internal bool HasInclusionListAnswer(Hash256 blockHash)
    {
        lock (_inclusionListLock) return _inclusionListAnswers.ContainsKey(blockHash);
    }

    /// <summary>Stores what evaluating <paramref name="retained"/> established for <paramref name="blockHash"/>,
    /// but only while that is still the list retained for it.</summary>
    /// <remarks>A concurrent <c>engine_newPayloadV6</c> may have retained a newer list, which outranks anything
    /// derived from the older one.</remarks>
    private void PublishInclusionListAnswer(Hash256 blockHash, byte[][] retained, bool? answer)
    {
        lock (_inclusionListLock)
        {
            if (_retainedInclusionLists.TryGetValue(blockHash, out RetainedInclusionList current)
                && ReferenceEquals(current.Transactions, retained))
            {
                RemoveRetainedInclusionList(blockHash);
                if (answer is { } satisfied)
                {
                    RemoveRetainedInclusionList(current.ParentHash);
                    RemoveInclusionListAnswer(current.ParentHash);
                    if (IsAfterFinalization(blockHash, current.Number))
                    {
                        _inclusionListAnswers[blockHash] = new InclusionListAnswer(current.ParentHash, current.Number, satisfied);
                        TrackInclusionListChild(current.ParentHash);
                    }
                }
            }
        }
    }

    private bool IsAfterFinalization(Hash256 blockHash, ulong number) =>
        _finalizedInclusionListHash is null || number > _finalizedInclusionListNumber || blockHash == _finalizedInclusionListHash;

    private void PruneFinalizedInclusionLists(Hash256 finalizedHash)
    {
        if (finalizedHash == Hash256.Zero || _inclusionListBlockTree.FindHeader(finalizedHash,
                BlockTreeLookupOptions.DoNotCreateLevelIfMissing) is not { Number: ulong number }) return;

        lock (_inclusionListLock)
        {
            if (_finalizedInclusionListHash == finalizedHash) return;
            if (_finalizedInclusionListHash is not null && number < _finalizedInclusionListNumber) return;
            _finalizedInclusionListNumber = number;
            _finalizedInclusionListHash = finalizedHash;

            foreach ((Hash256 hash, RetainedInclusionList entry) in _retainedInclusionLists)
                if (entry.Number <= number && hash != finalizedHash) RemoveRetainedInclusionList(hash);
            foreach ((Hash256 hash, InclusionListAnswer entry) in _inclusionListAnswers)
                if (entry.Number <= number && hash != finalizedHash) RemoveInclusionListAnswer(hash);
        }
    }

    private void TrackInclusionListChild(Hash256 parentHash)
    {
        _inclusionListChildCounts.TryGetValue(parentHash, out int count);
        _inclusionListChildCounts[parentHash] = count + 1;
    }

    private void UntrackInclusionListChild(Hash256 parentHash)
    {
        int count = _inclusionListChildCounts[parentHash];
        if (count == 1) _inclusionListChildCounts.Remove(parentHash);
        else _inclusionListChildCounts[parentHash] = count - 1;
    }

    private void RemoveRetainedInclusionList(Hash256 blockHash)
    {
        if (!_retainedInclusionLists.Remove(blockHash, out RetainedInclusionList entry)) return;
        if (entry.Accepted) UntrackInclusionListChild(entry.ParentHash);
        else
        {
            _syncingInclusionListOrder.Remove(_syncingInclusionListNodes[blockHash]);
            _syncingInclusionListNodes.Remove(blockHash);
        }
    }

    private void RemoveInclusionListAnswer(Hash256 blockHash)
    {
        if (_inclusionListAnswers.Remove(blockHash, out InclusionListAnswer entry))
            UntrackInclusionListChild(entry.ParentHash);
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
