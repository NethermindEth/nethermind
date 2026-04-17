// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Provides a fork choice update handler as defined in Engine API
/// <see href="https://github.com/ethereum/execution-apis/blob/main/src/engine/shanghai.md#engine_forkchoiceupdatedv2">
/// Shanghai</see> specification.
/// </summary>
/// <remarks>
/// May initiate a new payload creation.
/// </remarks>
public class ForkchoiceUpdatedHandler(
    IBlockTree blockTree,
    IManualBlockFinalizationManager manualBlockFinalizationManager,
    IPoSSwitcher poSSwitcher,
    IPayloadPreparationService payloadPreparationService,
    IBlockProcessingQueue processingQueue,
    IBlockCacheService blockCacheService,
    IInvalidChainTracker invalidChainTracker,
    IMergeSyncController mergeSyncController,
    IBeaconPivot beaconPivot,
    IPeerRefresher peerRefresher,
    ISpecProvider specProvider,
    ISyncPeerPool syncPeerPool,
    IMergeConfig mergeConfig,
    ILogManager logManager) : IForkchoiceUpdatedHandler
{
    protected readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
    private readonly IManualBlockFinalizationManager _manualBlockFinalizationManager = manualBlockFinalizationManager ?? throw new ArgumentNullException(nameof(manualBlockFinalizationManager));
    private readonly IPoSSwitcher _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
    private readonly ILogger _logger = logManager.GetClassLogger<ForkchoiceUpdatedHandler>();
    private readonly bool _simulateBlockProduction = mergeConfig.SimulateBlockProduction;

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Handle(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, int version)
    {
        BlockHeader? newHeadHeader = GetBlockHeader(forkchoiceState.HeadBlockHash);
        return await ApplyForkchoiceUpdate(newHeadHeader, forkchoiceState, payloadAttributes)
            ?? ValidateAttributes(payloadAttributes, version)
            ?? StartBuildingPayload(newHeadHeader!, forkchoiceState, payloadAttributes);
    }

    protected virtual bool IsOnMainChainBehindHead(BlockHeader newHeadHeader, ForkchoiceStateV1 forkchoiceState,
       [NotNullWhen(false)] out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult)
    {
        if (_blockTree.IsOnMainChainBehindHead(newHeadHeader))
        {
            if (_logger.IsInfo) _logger.Info($"Valid. ForkChoiceUpdated ignored - already in canonical chain.");
            errorResult = ForkchoiceUpdatedV1Result.Valid(null, forkchoiceState.HeadBlockHash);
            return false;
        }

        errorResult = null;
        return true;
    }

    // Rejects a finalized/safe entry that fails the numeric spec-ordering bounds (Casper FFG
    // monotonicity for finalized, safe >= finalized for safe) or the ancestry check against
    // newHead. L1-derived finality models override this to relax the bounds check while keeping
    // ancestry validation.
    protected virtual ResultWrapper<ForkchoiceUpdatedV1Result>? RejectIfInconsistent(
        BlockHeader? header, long lowerBound, string label, BlockHeader newHeadHeader, string requestStr)
    {
        if ((header is not null && (header.Number < lowerBound || header.Number > newHeadHeader.Number))
            || IsInconsistent(header, newHeadHeader))
        {
            string errorMsg = $"Inconsistent ForkChoiceState - {label} block hash. Request: {requestStr}";
            if (_logger.IsWarn) _logger.Warn(errorMsg);
            return ForkchoiceUpdatedV1Result.Error(errorMsg, MergeErrorCodes.InvalidForkchoiceState);
        }
        return null;
    }

    private async Task<ResultWrapper<ForkchoiceUpdatedV1Result>?> ApplyForkchoiceUpdate(BlockHeader? newHeadHeader, ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
    {
        // if a head is unknown we are syncing
        bool isDefinitelySyncing = newHeadHeader is null;
        using ThreadExtensions.Disposable handle = isDefinitelySyncing ?
            default : // Don't boost priority if we are definitely syncing
            Thread.CurrentThread.BoostPriority();

        if (invalidChainTracker.IsOnKnownInvalidChain(forkchoiceState.HeadBlockHash, out Hash256? lastValidHash))
        {
            if (_logger.IsWarn) _logger.Warn($"Received Invalid {forkchoiceState} {payloadAttributes} - {forkchoiceState.HeadBlockHash} is known to be a part of an invalid chain.");
            return ForkchoiceUpdatedV1Result.Invalid(lastValidHash);
        }

        if (isDefinitelySyncing)
        {
            string simpleRequestStr = payloadAttributes is null ? forkchoiceState.ToString() : $"{forkchoiceState} {payloadAttributes}";
            if (_logger.IsInfo) _logger.Info($"Received {simpleRequestStr}");

            BlockHeader? headBlockHeader = null;

            if (blockCacheService.BlockCache.TryGetValue(forkchoiceState.HeadBlockHash, out Block? block))
            {
                headBlockHeader = block.Header;
            }

            if (headBlockHeader is null)
            {
                if (_logger.IsDebug) _logger.Debug($"Attempting to fetch header from peer: {simpleRequestStr}.");
                headBlockHeader = await syncPeerPool.FetchHeaderFromPeer(forkchoiceState.HeadBlockHash);
            }

            if (headBlockHeader is not null)
            {
                StartNewBeaconHeaderSync(forkchoiceState, headBlockHeader, simpleRequestStr);
                return ForkchoiceUpdatedV1Result.Syncing;
            }

            if (_logger.IsInfo) _logger.Info($"Syncing Unknown ForkChoiceState head hash Request: {simpleRequestStr}.");
            return ForkchoiceUpdatedV1Result.Syncing;
        }

        Hash256 safeBlockHash = forkchoiceState.SafeBlockHash;
        Hash256 finalizedBlockHash = forkchoiceState.FinalizedBlockHash;

        BlockInfo? blockInfo = _blockTree.GetInfo(newHeadHeader!.Number, newHeadHeader.GetOrCalculateHash()).Info;

        BlockHeader? safeBlockHeader = ValidateBlockHash(ref safeBlockHash, out string? safeBlockErrorMsg);
        BlockHeader? finalizedHeader = ValidateBlockHash(ref finalizedBlockHash, out string? finalizationErrorMsg);

        string requestStr = forkchoiceState.ToString(newHeadHeader.Number, safeBlockHeader?.Number, finalizedHeader?.Number);

        if (_logger.IsInfo) _logger.Info($"Received {requestStr}");

        if (blockInfo is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Block info for: {requestStr} wasn't found.");
            return ForkchoiceUpdatedV1Result.Syncing;
        }

        if (!blockInfo.WasProcessed)
        {
            if (!IsOnMainChainBehindHead(newHeadHeader, forkchoiceState, out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult))
            {
                return errorResult;
            }

            BlockHeader? blockParent = _blockTree.FindHeader(newHeadHeader.ParentHash!, blockNumber: newHeadHeader.Number - 1);
            if (blockParent is null)
            {
                if (_logger.IsInfo) _logger.Info($"Parent of block {newHeadHeader} not available. Starting new beacon header sync.");

                StartNewBeaconHeaderSync(forkchoiceState, newHeadHeader, requestStr);

                return ForkchoiceUpdatedV1Result.Syncing;
            }

            if (beaconPivot.ShouldForceStartNewSync)
            {
                if (_logger.IsInfo) _logger.Info("Force starting new sync.");

                StartNewBeaconHeaderSync(forkchoiceState, newHeadHeader, requestStr);

                return ForkchoiceUpdatedV1Result.Syncing;
            }

            if (blockInfo is { IsBeaconMainChain: false, IsBeaconInfo: true })
            {
                ReorgBeaconChainDuringSync(newHeadHeader, blockInfo);
            }

            int processingQueueCount = processingQueue.Count;
            if (processingQueueCount == 0)
            {
                peerRefresher.RefreshPeers(newHeadHeader.Hash!, newHeadHeader.ParentHash!, finalizedBlockHash);
                blockCacheService.FinalizedHash = finalizedBlockHash;
                blockCacheService.HeadBlockHash = forkchoiceState.HeadBlockHash;
                mergeSyncController.StopBeaconModeControl();

                // Debug as already output in Received ForkChoice
                if (_logger.IsDebug) _logger.Debug($"Syncing beacon headers, Request: {requestStr}");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Processing {processingQueue.Count} blocks, Request: {requestStr}");
            }

            beaconPivot.ProcessDestination ??= newHeadHeader;
            return ForkchoiceUpdatedV1Result.Syncing;
        }

        if (_logger.IsDebug) _logger.Debug($"ForkChoiceUpdate: block {newHeadHeader} was processed.");

        if (finalizationErrorMsg is not null)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid finalized block hash {finalizationErrorMsg}. Request: {requestStr}.");
            return ForkchoiceUpdatedV1Result.Error(finalizationErrorMsg, MergeErrorCodes.InvalidForkchoiceState);
        }

        if (safeBlockErrorMsg is not null)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid safe block hash {safeBlockErrorMsg}. Request: {requestStr}.");
            return ForkchoiceUpdatedV1Result.Error(safeBlockErrorMsg, MergeErrorCodes.InvalidForkchoiceState);
        }

        if ((newHeadHeader.TotalDifficulty ?? 0) != 0 && (_poSSwitcher.MisconfiguredTerminalTotalDifficulty() || _poSSwitcher.BlockBeforeTerminalTotalDifficulty(newHeadHeader)))
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid terminal block. Nethermind TTD {_poSSwitcher.TerminalTotalDifficulty}, NewHeadBlock TD: {newHeadHeader.TotalDifficulty}. Request: {requestStr}.");

            // https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#specification
            // {status: INVALID, latestValidHash: 0x0000000000000000000000000000000000000000000000000000000000000000, validationError: errorMessage | null} if terminal block conditions are not satisfied
            return ForkchoiceUpdatedV1Result.Invalid(Keccak.Zero);
        }

        IReadOnlyList<Block>? blocks = EnsureNewHead(newHeadHeader, out string? setHeadErrorMsg);
        if (setHeadErrorMsg is not null)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid new head block {setHeadErrorMsg}. Request: {requestStr}.");
            return ForkchoiceUpdatedV1Result.Error(setHeadErrorMsg, ErrorCodes.InvalidParams);
        }

        if (!IsOnMainChainBehindHead(newHeadHeader, forkchoiceState, out ResultWrapper<ForkchoiceUpdatedV1Result>? result))
        {
            return result;
        }

        bool newHeadTheSameAsCurrentHead = _blockTree.Head!.Hash == newHeadHeader.Hash;
        bool shouldUpdateHead = !newHeadTheSameAsCurrentHead && blocks is not null;
        if (shouldUpdateHead)
        {
            _blockTree.UpdateMainChain(blocks!, true, true);
        }

        // Spec ordering: prevFinalized <= finalized <= safe <= head. Ancestry must be re-validated
        // on every FCU - the binding is (head, finalized, safe), so a repeated finalized/safe hash
        // paired with a new head on a sibling branch is still a spec violation.
        long prevFinalizedLevel = _manualBlockFinalizationManager.LastFinalizedBlockLevel;
        long finalizedNumber = finalizedHeader?.Number ?? 0;

        if (RejectIfInconsistent(finalizedHeader, prevFinalizedLevel, "finalized", newHeadHeader, requestStr) is { } finalizedError) return finalizedError;
        if (RejectIfInconsistent(safeBlockHeader, finalizedNumber, "safe", newHeadHeader, requestStr) is { } safeError) return safeError;

        bool nonZeroFinalizedBlockHash = finalizedBlockHash != Keccak.Zero;
        if (nonZeroFinalizedBlockHash)
        {
            _manualBlockFinalizationManager.MarkFinalized(newHeadHeader, finalizedHeader!);
        }

        if (shouldUpdateHead)
        {
            _poSSwitcher.ForkchoiceUpdated(newHeadHeader, finalizedBlockHash);
            if (_logger.IsInfo) _logger.Info($"Synced Chain Head to {newHeadHeader.ToString(BlockHeader.Format.Short)}");
        }

        _blockTree.ForkChoiceUpdated(forkchoiceState.FinalizedBlockHash, forkchoiceState.SafeBlockHash);
        return null;
    }

    protected virtual bool IsPayloadTimestampValid(BlockHeader newHeadHeader, PayloadAttributes payloadAttributes)
        => payloadAttributes.Timestamp > newHeadHeader.Timestamp;

    protected bool ArePayloadAttributesTimestampAndSlotNumberValid(BlockHeader newHeadHeader, ForkchoiceStateV1 forkchoiceState, PayloadAttributes payloadAttributes,
        [NotNullWhen(false)] out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult)
    {
        if (!IsPayloadTimestampValid(newHeadHeader, payloadAttributes))
        {
            string error = $"Invalid payload timestamp {payloadAttributes.Timestamp} for block timestamp {newHeadHeader.Timestamp}.";
            errorResult = ForkchoiceUpdatedV1Result.Error(error, MergeErrorCodes.InvalidPayloadAttributes);
            return false;
        }

        if (newHeadHeader.SlotNumber >= payloadAttributes.SlotNumber)
        {
            string error = $"Payload slot number {payloadAttributes.SlotNumber} must be greater than block slot number {newHeadHeader.SlotNumber}.";
            errorResult = ForkchoiceUpdatedV1Result.Error(error, MergeErrorCodes.InvalidPayloadAttributes);
            return false;
        }
        errorResult = null;
        return true;
    }

    private ResultWrapper<ForkchoiceUpdatedV1Result> StartBuildingPayload(BlockHeader newHeadHeader, ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
    {
        string? payloadId = null;
        bool isPayloadSimulated = _simulateBlockProduction && payloadAttributes is null;

        if (isPayloadSimulated)
        {
            payloadAttributes = newHeadHeader.GenerateSimulatedPayload();
        }

        if (payloadAttributes is not null)
        {
            if (!ArePayloadAttributesTimestampAndSlotNumberValid(newHeadHeader, forkchoiceState, payloadAttributes, out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid payload attributes: {errorResult.Result.Error}");
                return errorResult;
            }

            payloadId = payloadPreparationService.StartPreparingPayload(newHeadHeader, payloadAttributes);
        }

        _blockTree.ForkChoiceUpdated(forkchoiceState.FinalizedBlockHash, forkchoiceState.SafeBlockHash);
        return ForkchoiceUpdatedV1Result.Valid(isPayloadSimulated ? null : payloadId, forkchoiceState.HeadBlockHash);
    }

    private ResultWrapper<ForkchoiceUpdatedV1Result>? ValidateAttributes(PayloadAttributes? payloadAttributes, int version)
    {
        string? error = null;
        return payloadAttributes?.Validate(specProvider, version, out error) switch
        {
            PayloadAttributesValidationResult.InvalidPayloadAttributes =>
                ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error!, MergeErrorCodes.InvalidPayloadAttributes),
            PayloadAttributesValidationResult.UnsupportedFork =>
                ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error!, MergeErrorCodes.UnsupportedFork),
            _ => null,
        };
    }

    private void StartNewBeaconHeaderSync(ForkchoiceStateV1 forkchoiceState, BlockHeader blockHeader, string requestStr)
    {
        bool isSyncInitialized = mergeSyncController.TryInitBeaconHeaderSync(blockHeader);
        beaconPivot.ProcessDestination = blockHeader;
        peerRefresher.RefreshPeers(blockHeader.Hash!, blockHeader.ParentHash!, forkchoiceState.FinalizedBlockHash);
        blockCacheService.FinalizedHash = forkchoiceState.FinalizedBlockHash;
        blockCacheService.HeadBlockHash = forkchoiceState.HeadBlockHash;

        if (isSyncInitialized && _logger.IsInfo) _logger.Info($"Start a new sync process, Request: {requestStr}.");
    }

    // Validates that candidateHeader is an ancestor of newHeadHeader per the Engine API spec
    // (https://github.com/ethereum/execution-apis/blob/main/src/engine/paris.md#specification-1).
    // A null candidateHeader (Keccak.Zero in the FCU) is treated as consistent.
    private bool IsInconsistent(BlockHeader? candidateHeader, BlockHeader newHeadHeader)
    {
        if (candidateHeader is null) return false;
        if (candidateHeader.Number > newHeadHeader.Number) return true;

        bool candidateIsMain = _blockTree.IsMainChain(candidateHeader);
        if (_blockTree.IsMainChain(newHeadHeader)) return !candidateIsMain;

        // newHead is not main; walk parents. Depth bounded by (newHead.Number - candidate.Number).
        BlockHeader cursor = newHeadHeader;
        while (cursor.Number > candidateHeader.Number)
        {
            if (_blockTree.FindParentHeader(cursor, BlockTreeLookupOptions.TotalDifficultyNotNeeded) is not { } parent) return true;

            // Candidate on main chain: any main-chain ancestor proves ancestry. Checked after the
            // parent step so we don't re-probe newHeadHeader itself (already known non-main above).
            if (candidateIsMain && _blockTree.IsMainChain(parent)) return false;
            cursor = parent;
        }
        return cursor.GetOrCalculateHash() != candidateHeader.GetOrCalculateHash();
    }

    private BlockHeader? GetBlockHeader(Hash256 headBlockHash)
    {
        BlockHeader? header = _blockTree.FindHeader(headBlockHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (header is null)
        {
            if (_logger.IsInfo) _logger.Info($"Syncing, Block {headBlockHash} not found.");
        }

        return header;
    }

    private IReadOnlyList<Block>? EnsureNewHead(BlockHeader newHeadHeader, out string? errorMessage)
    {
        errorMessage = null;
        if (_blockTree.Head!.Hash == newHeadHeader.Hash)
        {
            return null;
        }

        if (!TryGetBranch(newHeadHeader, out IReadOnlyList<Block> branchOfBlocks))
        {
            errorMessage = $"Block's {newHeadHeader} main chain predecessor cannot be found and it will not be set as head.";
            if (_logger.IsWarn) _logger.Warn(errorMessage);
        }

        return branchOfBlocks;
    }

    protected virtual BlockHeader? ValidateBlockHash(ref Hash256 blockHash, out string? errorMessage, bool skipZeroHash = true)
    {
        errorMessage = null;
        if (skipZeroHash && blockHash == Keccak.Zero)
        {
            return null;
        }

        BlockHeader? blockHeader = _blockTree.FindHeader(blockHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (blockHeader is null)
        {
            errorMessage = $"Block {blockHash} not found.";
        }
        return blockHeader;
    }


    protected virtual bool TryGetBranch(BlockHeader newHeadHeader, out IReadOnlyList<Block> blocks)
    {
        Block? newHeadBlock = _blockTree.FindBlock(newHeadHeader.Hash!, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (newHeadBlock is null)
        {
            blocks = [];
            return false;
        }

        List<Block> blocksList = [newHeadBlock];
        Block? predecessor = newHeadBlock;

        while (true)
        {
            predecessor = _blockTree.FindParent(predecessor, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            if (predecessor is null)
            {
                blocks = [];
                return false;
            }
            if (_blockTree.IsMainChain(predecessor.Header)) break;
            blocksList.Add(predecessor);
        }

        blocksList.Reverse();
        blocks = blocksList;
        return true;
    }

    private void ReorgBeaconChainDuringSync(BlockHeader newHeadHeader, BlockInfo newHeadBlockInfo)
    {
        if (_logger.IsInfo) _logger.Info("BeaconChain reorged during the sync or cache rebuilt");
        IReadOnlyList<BlockInfo> beaconMainChainBranch = GetBeaconChainBranch(newHeadHeader, newHeadBlockInfo);
        _blockTree.UpdateBeaconMainChain(beaconMainChainBranch, Math.Max(beaconPivot.ProcessDestination?.Number ?? 0, newHeadHeader.Number));
        beaconPivot.ProcessDestination = newHeadHeader;
    }

    private IReadOnlyList<BlockInfo> GetBeaconChainBranch(BlockHeader newHeadHeader, BlockInfo newHeadBlockInfo)
    {
        newHeadBlockInfo.BlockNumber = newHeadHeader.Number;
        List<BlockInfo> blocksList = [newHeadBlockInfo];
        BlockHeader? predecessor = newHeadHeader;

        while (true)
        {
            predecessor = _blockTree.FindParentHeader(predecessor, BlockTreeLookupOptions.TotalDifficultyNotNeeded);

            if (predecessor is null)
            {
                break;
            }
            BlockInfo? predecessorInfo = _blockTree.GetInfo(predecessor.Number, predecessor.GetOrCalculateHash()).Info;
            if (predecessorInfo is null) break;
            predecessorInfo.BlockNumber = predecessor.Number;
            if (predecessorInfo.IsBeaconMainChain || !predecessorInfo.IsBeaconInfo) break;
            if (_logger.IsInfo) _logger.Info($"Reorged to beacon block ({predecessorInfo.BlockNumber}) {predecessorInfo.BlockHash} or cache rebuilt");
            blocksList.Add(predecessorInfo);
        }

        blocksList.Reverse();

        return blocksList;
    }
}
