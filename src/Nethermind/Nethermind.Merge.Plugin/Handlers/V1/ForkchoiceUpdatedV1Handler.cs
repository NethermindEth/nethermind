﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

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
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;

namespace Nethermind.Merge.Plugin.Handlers.V1
{
    /// <summary>
    /// Propagates the change in the fork choice to the execution client. May initiate creating new payload.
    /// <seealso cref="http://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#engine_forkchoiceupdatedv1"/>.
    /// </summary>
    public class ForkchoiceUpdatedV1Handler : IForkchoiceUpdatedV1Handler
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

        public ForkchoiceUpdatedV1Handler(
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
            string requestStr = $"{forkchoiceState} {payloadAttributes}";
            if (_logger.IsInfo) _logger.Info($"Received: {requestStr}");

            if (_invalidChainTracker.IsOnKnownInvalidChain(forkchoiceState.HeadBlockHash, out Keccak? lastValidHash))
            {
                if (_logger.IsInfo) _logger.Info($" FCU - Invalid - {requestStr} {forkchoiceState.HeadBlockHash} is known to be a part of an invalid chain.");
                return ForkchoiceUpdatedV1Result.Invalid(lastValidHash);
            }

            Block? newHeadBlock = GetBlock(forkchoiceState.HeadBlockHash);
            if (newHeadBlock is null) // if a head is unknown we are syncing
            {
                if (_blockCacheService.BlockCache.TryGetValue(forkchoiceState.HeadBlockHash, out Block? block))
                {
                    StartNewBeaconHeaderSync(forkchoiceState, block, requestStr);

                    return ForkchoiceUpdatedV1Result.Syncing;
                }

                if (_logger.IsInfo)
                {
                    _logger.Info($"Syncing... Unknown forkchoiceState head hash... Request: {requestStr}.");
                }
                return ForkchoiceUpdatedV1Result.Syncing;
            }

            BlockInfo blockInfo = _blockTree.GetInfo(newHeadBlock.Number, newHeadBlock.GetOrCalculateHash()).Info;
            if (!blockInfo.WasProcessed)
            {
                BlockHeader? blockParent = _blockTree.FindHeader(newHeadBlock.ParentHash!);
                if (blockParent == null)
                {
                    if (_logger.IsDebug)
                        _logger.Debug($"Parent of block not available. Starting new beacon header. sync.");

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

                    if (_logger.IsInfo) { _logger.Info($"Syncing beacon headers... Request: {requestStr}."); }
                }
                else
                {
                    if (_logger.IsInfo) { _logger.Info($"Processing {_processingQueue.Count} blocks... Request: {requestStr}."); }
                }

                _beaconPivot.ProcessDestination ??= newHeadBlock!.Header;
                return ForkchoiceUpdatedV1Result.Syncing;
            }

            if (_logger.IsInfo) _logger.Info($"FCU - block {newHeadBlock} was processed.");

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
                if (_logger.IsInfo) _logger.Info($"Valid. ForkchoiceUpdated ignored - already in canonical chain. Request: {requestStr}.");
                return ForkchoiceUpdatedV1Result.Valid(null, forkchoiceState.HeadBlockHash);
            }

            EnsureTerminalBlock(forkchoiceState, blocks);

            bool newHeadTheSameAsCurrentHead = _blockTree.Head!.Hash == newHeadBlock.Hash;
            bool shouldUpdateHead = !newHeadTheSameAsCurrentHead && blocks is not null;
            if (shouldUpdateHead)
            {
                _blockTree.UpdateMainChain(blocks!, true, true);
            }

            if (IsInconsistent(forkchoiceState.FinalizedBlockHash))
            {
                string errorMsg = $"Inconsistent forkchoiceState - finalized block hash. Request: {requestStr}";
                if (_logger.IsWarn) _logger.Warn(errorMsg);
                return ForkchoiceUpdatedV1Result.Error(errorMsg, MergeErrorCodes.InvalidForkchoiceState);
            }

            if (IsInconsistent(forkchoiceState.SafeBlockHash))
            {
                string errorMsg = $"Inconsistent forkchoiceState - safe block hash. Request: {requestStr}";
                if (_logger.IsWarn) _logger.Warn(errorMsg);
                return ForkchoiceUpdatedV1Result.Error(errorMsg, MergeErrorCodes.InvalidForkchoiceState);
            }

            bool nonZeroFinalizedBlockHash = forkchoiceState.FinalizedBlockHash != Keccak.Zero;
            // bool nonZeroSafeBlockHash = forkchoiceState.SafeBlockHash != Keccak.Zero;
            if (nonZeroFinalizedBlockHash)
            {
                _manualBlockFinalizationManager.MarkFinalized(newHeadBlock.Header, finalizedHeader!);
            }

            if (shouldUpdateHead)
            {
                _poSSwitcher.ForkchoiceUpdated(newHeadBlock.Header, forkchoiceState.FinalizedBlockHash);
                if (_logger.IsInfo) _logger.Info($"Block {forkchoiceState.HeadBlockHash} was set as head.");
            }

            string? payloadId = null;
            if (payloadAttributes is not null)
            {
                payloadAttributes.GasLimit = null;
                if (newHeadBlock.Timestamp >= payloadAttributes.Timestamp)
                {
                    if (_logger.IsWarn) _logger.Warn($"Invalid payload attributes timestamp {payloadAttributes.Timestamp}, block timestamp {newHeadBlock.Timestamp}. Request: {requestStr}.");

                    return ForkchoiceUpdatedV1Result.Error(
                        $"Invalid payload attributes timestamp {payloadAttributes.Timestamp}, block timestamp {newHeadBlock.Timestamp}. Request: {requestStr}",
                        MergeErrorCodes.InvalidPayloadAttributes);
                }
                else
                {
                    payloadId = _payloadPreparationService.StartPreparingPayload(newHeadBlock.Header, payloadAttributes);
                }
            }

            if (_logger.IsInfo) _logger.Info($"Valid. Request: {requestStr}.");

            _blockTree.ForkChoiceUpdated(forkchoiceState.FinalizedBlockHash, forkchoiceState.SafeBlockHash);
            return ForkchoiceUpdatedV1Result.Valid(payloadId, forkchoiceState.HeadBlockHash);
        }

        private void StartNewBeaconHeaderSync(ForkchoiceStateV1 forkchoiceState, Block block, string requestStr)
        {
            _mergeSyncController.InitBeaconHeaderSync(block.Header);
            _beaconPivot.ProcessDestination = block.Header;
            _peerRefresher.RefreshPeers(block.Hash!, block.ParentHash!, forkchoiceState.FinalizedBlockHash);
            _blockCacheService.FinalizedHash = forkchoiceState.FinalizedBlockHash;

            if (_logger.IsInfo) _logger.Info($"Start a new sync process... Request: {requestStr}.");
        }

        // This method will detect reorg in terminal PoW block
        private void EnsureTerminalBlock(ForkchoiceStateV1 forkchoiceState, Block[]? blocks)
        {
            // we can reorg terminal block only if we haven't finalized PoS yet and we're not finalizing PoS now
            // https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#ability-to-jump-between-terminal-pow-blocks
            bool notFinalizingPoS = forkchoiceState.FinalizedBlockHash == Keccak.Zero;
            bool notFinalizedPoS = _manualBlockFinalizationManager.LastFinalizedHash == Keccak.Zero;
            if (notFinalizingPoS && notFinalizedPoS && blocks != null)
            {
                for (int i = 0; i < blocks.Length; ++i)
                {
                    if (blocks[i].Header.Difficulty != 0 && blocks[i].TotalDifficulty >= _poSSwitcher.TerminalTotalDifficulty)
                    {
                        if (_poSSwitcher.TryUpdateTerminalBlock(blocks[i].Header))
                        {
                            if (_logger.IsInfo) _logger.Info($"Terminal block {blocks[i].Header} updated during the forkchoice");
                        }

                        break;
                    }
                }
            }
        }

        private bool IsInconsistent(Keccak blockHash) => blockHash != Keccak.Zero && !_blockTree.IsMainChain(blockHash);

        private Block? GetBlock(Keccak headBlockHash)
        {
            Block? block = _blockTree.FindBlock(headBlockHash, BlockTreeLookupOptions.DoNotCalculateTotalDifficulty);
            if (block is null)
            {
                if (_logger.IsInfo) _logger.Info($"Syncing... Block {headBlockHash} not found.");
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

            BlockHeader? blockHeader = _blockTree.FindHeader(blockHash, BlockTreeLookupOptions.DoNotCalculateTotalDifficulty);
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
                predecessor = _blockTree.FindParent(predecessor, BlockTreeLookupOptions.DoNotCalculateTotalDifficulty);
                if (predecessor == null)
                {
                    blocks = Array.Empty<Block>();
                    return false;
                }
                if(_blockTree.IsMainChain(predecessor.Header)) break;
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

                if (predecessor == null)
                {
                    break;
                }
                BlockInfo predecessorInfo = _blockTree.GetInfo(predecessor.Number, predecessor.GetOrCalculateHash()).Info;
                predecessorInfo.BlockNumber = predecessor.Number;
                if (predecessorInfo.IsBeaconMainChain || !predecessorInfo.IsBeaconInfo) break;
                if (_logger.IsInfo) _logger.Info($"Reorged to beacon block ({predecessorInfo.BlockNumber}) {predecessorInfo.BlockHash} or cache rebuilt");
                blocksList.Add(predecessorInfo);
            }

            blocksList.Reverse();

            return blocksList.ToArray();
        }

    }
}
