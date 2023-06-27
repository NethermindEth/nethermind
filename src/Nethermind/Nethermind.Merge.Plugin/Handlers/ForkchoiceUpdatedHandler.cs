// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;

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
    private readonly IBlockTree _blockTree;
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
        ILogManager logManager)
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
        _logger = logManager.GetClassLogger();
    }

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Handle(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
    {
        string requestStr = payloadAttributes is null ? forkchoiceState.ToString() : $"{forkchoiceState} {payloadAttributes}";
        if (_logger.IsInfo) _logger.Info($"Received {requestStr}");

        if (_invalidChainTracker.IsOnKnownInvalidChain(forkchoiceState.HeadBlockHash, out Keccak? lastValidHash))
        {
            if (_logger.IsInfo) _logger.Info($" ForkChoiceUpdate: Invalid - {requestStr} {forkchoiceState.HeadBlockHash} is known to be a part of an invalid chain.");
            return ForkchoiceUpdatedV1Result.Invalid(lastValidHash);
        }

        Block? newHeadBlock = GetBlock(forkchoiceState.HeadBlockHash);
        if (newHeadBlock is null) // if a head is unknown we are syncing
        {
            if (_blockCacheService.BlockCache.TryGetValue(forkchoiceState.HeadBlockHash, out Block? block))
            {
                StartNewBeaconHeaderSync(forkchoiceState, block, requestStr);
            }
            else if (_logger.IsInfo)
            {
                _logger.Info($"Syncing Unknown ForkChoiceState head hash Request: {requestStr}.");
            }

            return ForkchoiceUpdatedV1Result.Syncing;
        }

        BlockInfo? blockInfo = _blockTree.GetInfo(newHeadBlock.Number, newHeadBlock.GetOrCalculateHash()).Info;
        if (blockInfo is null)
        {
            if (_logger.IsWarn) { _logger.Warn($"Block info for: {requestStr} wasn't found."); }
            return ForkchoiceUpdatedV1Result.Syncing;
        }
        if (!blockInfo.WasProcessed)
        {
            BlockHeader? blockParent = _blockTree.FindHeader(newHeadBlock.ParentHash!);
            if (blockParent is null)
            {
                if (_logger.IsInfo)
                    _logger.Info($"Parent of block {newHeadBlock} not available. Starting new beacon header. sync.");

                StartNewBeaconHeaderSync(forkchoiceState, newHeadBlock!, requestStr);

                return ForkchoiceUpdatedV1Result.Syncing;
            }

            if (_beaconPivot.ShouldForceStartNewSync)
            {
                if (_logger.IsInfo)
                    _logger.Info($"Force starting new sync.");

                StartNewBeaconHeaderSync(forkchoiceState, newHeadBlock!, requestStr);

                return ForkchoiceUpdatedV1Result.Syncing;
            }

            if (!blockInfo.IsBeaconMainChain && blockInfo.IsBeaconInfo)
                ReorgBeaconChainDuringSync(newHeadBlock!, blockInfo);

            int processingQueueCount = _processingQueue.Count;
            if (processingQueueCount == 0)
            {
                _peerRefresher.RefreshPeers(newHeadBlock!.Hash!, newHeadBlock.ParentHash!, forkchoiceState.FinalizedBlockHash);
                _blockCacheService.FinalizedHash = forkchoiceState.FinalizedBlockHash;
                _mergeSyncController.StopBeaconModeControl();

                if (_logger.IsInfo) { _logger.Info($"Syncing beacon headers, Request: {requestStr}"); }
            }
            else
            {
                if (_logger.IsInfo) { _logger.Info($"Processing {_processingQueue.Count} blocks, Request: {requestStr}"); }
            }

            _beaconPivot.ProcessDestination ??= newHeadBlock!.Header;
            return ForkchoiceUpdatedV1Result.Syncing;
        }

        if (_logger.IsDebug) _logger.Debug($"ForkChoiceUpdate: block {newHeadBlock} was processed.");

        BlockHeader? finalizedHeader = ValidateBlockHash(forkchoiceState.FinalizedBlockHash, out string? finalizationErrorMsg);
        if (finalizationErrorMsg is not null)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid finalized block hash {finalizationErrorMsg}. Request: {requestStr}.");
            return ForkchoiceUpdatedV1Result.Error(finalizationErrorMsg, MergeErrorCodes.InvalidForkchoiceState);
        }

        ValidateBlockHash(forkchoiceState.SafeBlockHash, out string? safeBlockErrorMsg);
        if (safeBlockErrorMsg is not null)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid safe block hash {finalizationErrorMsg}. Request: {requestStr}.");
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

        if (_blockTree.IsOnMainChainBehindHead(newHeadBlock))
        {
            if (_logger.IsInfo) _logger.Info($"Valid. ForkChoiceUpdated ignored - already in canonical chain. Request: {requestStr}.");
            return ForkchoiceUpdatedV1Result.Valid(null, forkchoiceState.HeadBlockHash);
        }

        bool newHeadTheSameAsCurrentHead = _blockTree.Head!.Hash == newHeadBlock.Hash;
        bool shouldUpdateHead = !newHeadTheSameAsCurrentHead && blocks is not null;
        if (shouldUpdateHead)
        {
            _blockTree.UpdateMainChain(blocks!, true, true);
        }

        if (IsInconsistent(forkchoiceState.FinalizedBlockHash))
        {
            string errorMsg = $"Inconsistent ForkChoiceState - finalized block hash. Request: {requestStr}";
            if (_logger.IsWarn) _logger.Warn(errorMsg);
            return ForkchoiceUpdatedV1Result.Error(errorMsg, MergeErrorCodes.InvalidForkchoiceState);
        }

        if (IsInconsistent(forkchoiceState.SafeBlockHash))
        {
            string errorMsg = $"Inconsistent ForkChoiceState - safe block hash. Request: {requestStr}";
            if (_logger.IsWarn) _logger.Warn(errorMsg);
            return ForkchoiceUpdatedV1Result.Error(errorMsg, MergeErrorCodes.InvalidForkchoiceState);
        }

        bool nonZeroFinalizedBlockHash = forkchoiceState.FinalizedBlockHash != Keccak.Zero;
        if (nonZeroFinalizedBlockHash)
        {
            _manualBlockFinalizationManager.MarkFinalized(newHeadBlock.Header, finalizedHeader!);
        }

        if (shouldUpdateHead)
        {
            _poSSwitcher.ForkchoiceUpdated(newHeadBlock.Header, forkchoiceState.FinalizedBlockHash);
            if (_logger.IsInfo) _logger.Info($"Synced chain Head to  {newHeadBlock.ToString(Block.Format.Short)}");
        }

        string? payloadId = null;
        if (payloadAttributes is not null)
        {
            if (newHeadBlock.Timestamp >= payloadAttributes.Timestamp)
            {
                var error = $"Payload timestamp {payloadAttributes.Timestamp} must be greater than block timestamp {newHeadBlock.Timestamp}.";

                if (_logger.IsWarn) _logger.Warn($"Invalid payload attributes: {error}");

                return ForkchoiceUpdatedV1Result.Error(error, MergeErrorCodes.InvalidPayloadAttributes);
            }

            payloadId = _payloadPreparationService.StartPreparingPayload(newHeadBlock.Header, payloadAttributes);
        }

        if (_logger.IsDebug) _logger.Debug($"Valid. Request: {requestStr}.");

        _blockTree.ForkChoiceUpdated(forkchoiceState.FinalizedBlockHash, forkchoiceState.SafeBlockHash);
        return ForkchoiceUpdatedV1Result.Valid(payloadId, forkchoiceState.HeadBlockHash);
    }

    private void StartNewBeaconHeaderSync(ForkchoiceStateV1 forkchoiceState, Block block, string requestStr)
    {
        _mergeSyncController.InitBeaconHeaderSync(block.Header);
        _beaconPivot.ProcessDestination = block.Header;
        _peerRefresher.RefreshPeers(block.Hash!, block.ParentHash!, forkchoiceState.FinalizedBlockHash);
        _blockCacheService.FinalizedHash = forkchoiceState.FinalizedBlockHash;

        if (_logger.IsInfo) _logger.Info($"Start a new sync process, Request: {requestStr}.");
    }

    private bool IsInconsistent(Keccak blockHash) => blockHash != Keccak.Zero && !_blockTree.IsMainChain(blockHash);

    private Block? GetBlock(Keccak headBlockHash)
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

    private BlockHeader? ValidateBlockHash(Keccak blockHash, out string? errorMessage, bool skipZeroHash = true)
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
            if (_logger.IsWarn) _logger.Warn(errorMessage);
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
                blocks = Array.Empty<Block>();
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
