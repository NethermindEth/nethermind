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
public class ForkchoiceUpdatedHandler : IForkchoiceUpdatedHandler
{
    protected readonly IBlockTree _blockTree;
    private readonly IManualBlockFinalizationManager _manualBlockFinalizationManager;
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IPayloadPreparationService _payloadPreparationService;
    private readonly IBlockProcessingQueue _processingQueue;
    private readonly IBlockCacheService _blockCacheService;
    private readonly IInvalidChainTracker _invalidChainTracker;
    private readonly IMergeSyncController _mergeSyncController;
    private readonly IBeaconPivot _beaconPivot;
    private readonly ILogger _logger;
    private readonly IPeerRefresher _peerRefresher;
    private readonly ISpecProvider _specProvider;
    private readonly bool _simulateBlockProduction;
    private readonly ISyncPeerPool _syncPeerPool;

    public ForkchoiceUpdatedHandler(
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
        ILogManager logManager,
        bool simulateBlockProduction = false)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _manualBlockFinalizationManager = manualBlockFinalizationManager ?? throw new ArgumentNullException(nameof(manualBlockFinalizationManager));
        _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        _payloadPreparationService = payloadPreparationService;
        _processingQueue = processingQueue;
        _blockCacheService = blockCacheService;
        _invalidChainTracker = invalidChainTracker;
        _mergeSyncController = mergeSyncController;
        _beaconPivot = beaconPivot;
        _peerRefresher = peerRefresher;
        _specProvider = specProvider;
        _syncPeerPool = syncPeerPool;
        _simulateBlockProduction = simulateBlockProduction;
        _logger = logManager.GetClassLogger();
    }

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Handle(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, int version)
    {
        Block? newHeadBlock = GetBlock(forkchoiceState.HeadBlockHash);
        return await ApplyForkchoiceUpdate(newHeadBlock, forkchoiceState, payloadAttributes)
            ?? ValidateAttributes(payloadAttributes, version)
            ?? StartBuildingPayload(newHeadBlock!, forkchoiceState, payloadAttributes);
    }

    protected virtual bool IsOnMainChainBehindHead(Block newHeadBlock, ForkchoiceStateV1 forkchoiceState,
       [NotNullWhen(false)] out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult)
    {
        if (_blockTree.IsOnMainChainBehindHead(newHeadBlock))
        {
            if (_logger.IsInfo) _logger.Info($"Valid. ForkChoiceUpdated ignored - already in canonical chain.");
            errorResult = ForkchoiceUpdatedV1Result.Valid(null, forkchoiceState.HeadBlockHash);
            return false;
        }

        errorResult = null;
        return true;
    }

    private async Task<ResultWrapper<ForkchoiceUpdatedV1Result>?> ApplyForkchoiceUpdate(Block? newHeadBlock, ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
    {
        // if a head is unknown we are syncing
        bool isDefinitelySyncing = newHeadBlock is null;
        using ThreadExtensions.Disposable handle = isDefinitelySyncing ?
            default : // Don't boost priority if we are definitely syncing
            Thread.CurrentThread.BoostPriority();

        if (_invalidChainTracker.IsOnKnownInvalidChain(forkchoiceState.HeadBlockHash, out Hash256? lastValidHash))
        {
            if (_logger.IsWarn) _logger.Warn($"Received Invalid {forkchoiceState} {payloadAttributes} - {forkchoiceState.HeadBlockHash} is known to be a part of an invalid chain.");
            return ForkchoiceUpdatedV1Result.Invalid(lastValidHash);
        }

        if (isDefinitelySyncing)
        {
            string simpleRequestStr = payloadAttributes is null ? forkchoiceState.ToString() : $"{forkchoiceState} {payloadAttributes}";
            if (_logger.IsInfo) _logger.Info($"Received {simpleRequestStr}");

            BlockHeader? headBlockHeader = null;

            if (_blockCacheService.BlockCache.TryGetValue(forkchoiceState.HeadBlockHash, out Block? block))
            {
                headBlockHeader = block.Header;
            }

            if (headBlockHeader is null)
            {
                if (_logger.IsDebug) _logger.Debug($"Attempting to fetch header from peer: {simpleRequestStr}.");
                headBlockHeader = await _syncPeerPool.FetchHeaderFromPeer(forkchoiceState.HeadBlockHash);
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

        BlockInfo? blockInfo = _blockTree.GetInfo(newHeadBlock!.Number, newHeadBlock.GetOrCalculateHash()).Info;

        BlockHeader? safeBlockHeader = ValidateBlockHash(ref safeBlockHash, out string? safeBlockErrorMsg);
        BlockHeader? finalizedHeader = ValidateBlockHash(ref finalizedBlockHash, out string? finalizationErrorMsg);

        string requestStr = forkchoiceState.ToString(newHeadBlock.Number, safeBlockHeader?.Number, finalizedHeader?.Number);

        if (_logger.IsInfo) _logger.Info($"Received {requestStr}");

        if (blockInfo is null)
        {
            if (_logger.IsWarn) { _logger.Warn($"Block info for: {requestStr} wasn't found."); }
            return ForkchoiceUpdatedV1Result.Syncing;
        }

        if (!blockInfo.WasProcessed)
        {
            if (!IsOnMainChainBehindHead(newHeadBlock, forkchoiceState, out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult))
            {
                return errorResult;
            }

            BlockHeader? blockParent = _blockTree.FindHeader(newHeadBlock.ParentHash!, blockNumber: newHeadBlock.Number - 1);
            if (blockParent is null)
            {
                if (_logger.IsInfo) _logger.Info($"Parent of block {newHeadBlock} not available. Starting new beacon header sync.");

                StartNewBeaconHeaderSync(forkchoiceState, newHeadBlock!.Header, requestStr);

                return ForkchoiceUpdatedV1Result.Syncing;
            }

            if (_beaconPivot.ShouldForceStartNewSync)
            {
                if (_logger.IsInfo) _logger.Info("Force starting new sync.");

                StartNewBeaconHeaderSync(forkchoiceState, newHeadBlock!.Header, requestStr);

                return ForkchoiceUpdatedV1Result.Syncing;
            }

            if (blockInfo is { IsBeaconMainChain: false, IsBeaconInfo: true })
            {
                ReorgBeaconChainDuringSync(newHeadBlock!, blockInfo);
            }

            int processingQueueCount = _processingQueue.Count;
            if (processingQueueCount == 0)
            {
                _peerRefresher.RefreshPeers(newHeadBlock!.Hash!, newHeadBlock.ParentHash!, finalizedBlockHash);
                _blockCacheService.FinalizedHash = finalizedBlockHash;
                _blockCacheService.HeadBlockHash = forkchoiceState.HeadBlockHash;
                _mergeSyncController.StopBeaconModeControl();

                // Debug as already output in Received ForkChoice
                if (_logger.IsDebug) _logger.Debug($"Syncing beacon headers, Request: {requestStr}");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Processing {_processingQueue.Count} blocks, Request: {requestStr}");
            }

            _beaconPivot.ProcessDestination ??= newHeadBlock!.Header;
            return ForkchoiceUpdatedV1Result.Syncing;
        }

        if (_logger.IsDebug) _logger.Debug($"ForkChoiceUpdate: block {newHeadBlock} was processed.");

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

        if ((newHeadBlock.TotalDifficulty ?? 0) != 0 && (_poSSwitcher.MisconfiguredTerminalTotalDifficulty() || _poSSwitcher.BlockBeforeTerminalTotalDifficulty(newHeadBlock.Header)))
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid terminal block. Nethermind TTD {_poSSwitcher.TerminalTotalDifficulty}, NewHeadBlock TD: {newHeadBlock.Header.TotalDifficulty}. Request: {requestStr}.");

            // https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#specification
            // {status: INVALID, latestValidHash: 0x0000000000000000000000000000000000000000000000000000000000000000, validationError: errorMessage | null} if terminal block conditions are not satisfied
            return ForkchoiceUpdatedV1Result.Invalid(Keccak.Zero);
        }

        Block[]? blocks = EnsureNewHead(newHeadBlock, out string? setHeadErrorMsg);
        if (setHeadErrorMsg is not null)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid new head block {setHeadErrorMsg}. Request: {requestStr}.");
            return ForkchoiceUpdatedV1Result.Error(setHeadErrorMsg, ErrorCodes.InvalidParams);
        }

        if (!IsOnMainChainBehindHead(newHeadBlock, forkchoiceState, out ResultWrapper<ForkchoiceUpdatedV1Result>? result))
        {
            return result;
        }

        bool newHeadTheSameAsCurrentHead = _blockTree.Head!.Hash == newHeadBlock.Hash;
        bool shouldUpdateHead = !newHeadTheSameAsCurrentHead && blocks is not null;
        if (shouldUpdateHead)
        {
            _blockTree.UpdateMainChain(blocks!, true, true);
        }

        if (IsInconsistent(finalizedBlockHash))
        {
            string errorMsg = $"Inconsistent ForkChoiceState - finalized block hash. Request: {requestStr}";
            if (_logger.IsWarn) _logger.Warn(errorMsg);
            return ForkchoiceUpdatedV1Result.Error(errorMsg, MergeErrorCodes.InvalidForkchoiceState);
        }

        if (IsInconsistent(safeBlockHash))
        {
            string errorMsg = $"Inconsistent ForkChoiceState - safe block hash. Request: {requestStr}";
            if (_logger.IsWarn) _logger.Warn(errorMsg);
            return ForkchoiceUpdatedV1Result.Error(errorMsg, MergeErrorCodes.InvalidForkchoiceState);
        }

        bool nonZeroFinalizedBlockHash = finalizedBlockHash != Keccak.Zero;
        if (nonZeroFinalizedBlockHash)
        {
            _manualBlockFinalizationManager.MarkFinalized(newHeadBlock.Header, finalizedHeader!);
        }

        if (shouldUpdateHead)
        {
            _poSSwitcher.ForkchoiceUpdated(newHeadBlock.Header, finalizedBlockHash);
            if (_logger.IsInfo) _logger.Info($"Synced Chain Head to {newHeadBlock.ToString(Block.Format.Short)}");
        }

        return null;
    }

    protected virtual bool IsPayloadAttributesTimestampValid(Block newHeadBlock, ForkchoiceStateV1 forkchoiceState, PayloadAttributes payloadAttributes,
        [NotNullWhen(false)] out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult)
    {
        if (newHeadBlock.Timestamp >= payloadAttributes.Timestamp)
        {
            string error = $"Payload timestamp {payloadAttributes.Timestamp} must be greater than block timestamp {newHeadBlock.Timestamp}.";
            errorResult = ForkchoiceUpdatedV1Result.Error(error, MergeErrorCodes.InvalidPayloadAttributes);
            return false;
        }

        errorResult = null;
        return true;
    }

    private ResultWrapper<ForkchoiceUpdatedV1Result> StartBuildingPayload(Block newHeadBlock, ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
    {
        string? payloadId = null;
        bool isPayloadSimulated = _simulateBlockProduction && payloadAttributes is null;

        if (isPayloadSimulated)
        {
            payloadAttributes = newHeadBlock.Header.GenerateSimulatedPayload();
        }

        if (payloadAttributes is not null)
        {
            if (!IsPayloadAttributesTimestampValid(newHeadBlock, forkchoiceState, payloadAttributes, out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid payload attributes: {errorResult.Result.Error}");
                return errorResult;
            }

            payloadId = _payloadPreparationService.StartPreparingPayload(newHeadBlock.Header, payloadAttributes);
        }

        _blockTree.ForkChoiceUpdated(forkchoiceState.FinalizedBlockHash, forkchoiceState.SafeBlockHash);
        return ForkchoiceUpdatedV1Result.Valid(isPayloadSimulated ? null : payloadId, forkchoiceState.HeadBlockHash);
    }

    private ResultWrapper<ForkchoiceUpdatedV1Result>? ValidateAttributes(PayloadAttributes? payloadAttributes, int version)
    {
        string? error = null;
        return payloadAttributes?.Validate(_specProvider, version, out error) switch
        {
            PayloadAttributesValidationResult.InvalidParams =>
                ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error!, ErrorCodes.InvalidParams),
            PayloadAttributesValidationResult.InvalidPayloadAttributes =>
                ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error!, MergeErrorCodes.InvalidPayloadAttributes),
            PayloadAttributesValidationResult.UnsupportedFork =>
                ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error!, MergeErrorCodes.UnsupportedFork),
            _ => null,
        };
    }

    private void StartNewBeaconHeaderSync(ForkchoiceStateV1 forkchoiceState, BlockHeader blockHeader, string requestStr)
    {
        bool isSyncInitialized = _mergeSyncController.TryInitBeaconHeaderSync(blockHeader);
        _beaconPivot.ProcessDestination = blockHeader;
        _peerRefresher.RefreshPeers(blockHeader.Hash!, blockHeader.ParentHash!, forkchoiceState.FinalizedBlockHash);
        _blockCacheService.FinalizedHash = forkchoiceState.FinalizedBlockHash;
        _blockCacheService.HeadBlockHash = forkchoiceState.HeadBlockHash;

        if (isSyncInitialized && _logger.IsInfo) _logger.Info($"Start a new sync process, Request: {requestStr}.");
    }

    private bool IsInconsistent(Hash256 blockHash) => blockHash != Keccak.Zero && !_blockTree.IsMainChain(blockHash);

    private Block? GetBlock(Hash256 headBlockHash)
    {
        Block? block = _blockTree.FindBlock(headBlockHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (block is null)
        {
            if (_logger.IsInfo) _logger.Info($"Syncing, Block {headBlockHash} not found.");
        }

        return block;
    }

    private Block[]? EnsureNewHead(Block newHeadBlock, out string? errorMessage)
    {
        errorMessage = null;
        if (_blockTree.Head!.Hash == newHeadBlock.Hash)
        {
            return null;
        }

        if (!TryGetBranch(newHeadBlock, out Block[] branchOfBlocks))
        {
            errorMessage = $"Block's {newHeadBlock} main chain predecessor cannot be found and it will not be set as head.";
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


    private bool TryGetBranch(Block newHeadBlock, out Block[] blocks)
    {
        List<Block> blocksList = new() { newHeadBlock };
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
        blocks = blocksList.ToArray();
        return true;
    }

    private void ReorgBeaconChainDuringSync(Block newHeadBlock, BlockInfo newHeadBlockInfo)
    {
        if (_logger.IsInfo) _logger.Info("BeaconChain reorged during the sync or cache rebuilt");
        BlockInfo[] beaconMainChainBranch = GetBeaconChainBranch(newHeadBlock, newHeadBlockInfo);
        _blockTree.UpdateBeaconMainChain(beaconMainChainBranch, Math.Max(_beaconPivot.ProcessDestination?.Number ?? 0, newHeadBlock.Number));
        _beaconPivot.ProcessDestination = newHeadBlock.Header;
    }

    private BlockInfo[] GetBeaconChainBranch(Block newHeadBlock, BlockInfo newHeadBlockInfo)
    {
        newHeadBlockInfo.BlockNumber = newHeadBlock.Number;
        List<BlockInfo> blocksList = new() { newHeadBlockInfo };
        Block? predecessor = newHeadBlock;

        while (true)
        {
            predecessor = _blockTree.FindParent(predecessor, BlockTreeLookupOptions.TotalDifficultyNotNeeded);

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

        return blocksList.ToArray();
    }
}
