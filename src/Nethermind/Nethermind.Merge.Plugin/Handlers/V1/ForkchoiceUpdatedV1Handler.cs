//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Handlers.V1
{
    /// <summary>
    /// https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md
    /// Propagates the change in the fork choice to the execution client
    /// </summary>
    public class ForkchoiceUpdatedV1Handler : IForkchoiceUpdatedV1Handler
    {
        private readonly IBlockTree _blockTree;
        private readonly IManualBlockFinalizationManager _manualBlockFinalizationManager;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private readonly IBlockConfirmationManager _blockConfirmationManager;
        private readonly IPayloadPreparationService _payloadPreparationService;
        private readonly IBlockCacheService _blockCacheService;
        private readonly IBeaconSyncStrategy _beaconSyncStrategy;
        private readonly IMergeSyncController _mergeSyncController;
        private readonly IBeaconPivot _beaconPivot;
        private readonly ILogger _logger;
        private readonly IPeerRefresher _peerRefresher;

        public ForkchoiceUpdatedV1Handler(
            IBlockTree blockTree,
            IManualBlockFinalizationManager manualBlockFinalizationManager,
            IPoSSwitcher poSSwitcher,
            IEthSyncingInfo ethSyncingInfo,
            IBlockConfirmationManager blockConfirmationManager,
            IPayloadPreparationService payloadPreparationService,
            IBlockCacheService blockCacheService,
            IBeaconSyncStrategy beaconSyncStrategy,
            IMergeSyncController mergeSyncController,
            IBeaconPivot beaconPivot,
            IPeerRefresher peerRefresher,
            ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _manualBlockFinalizationManager = manualBlockFinalizationManager ??
                                              throw new ArgumentNullException(nameof(manualBlockFinalizationManager));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
            _blockConfirmationManager = blockConfirmationManager ??
                                        throw new ArgumentNullException(nameof(blockConfirmationManager));
            _payloadPreparationService = payloadPreparationService;
            _blockCacheService = blockCacheService;
            _beaconSyncStrategy = beaconSyncStrategy;
            _mergeSyncController = mergeSyncController;
            _beaconPivot = beaconPivot;
            _peerRefresher = peerRefresher;
            _logger = logManager.GetClassLogger();
        }

        public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Handle(ForkchoiceStateV1 forkchoiceState,
            PayloadAttributes? payloadAttributes)
        {
            string requestStr = $"{forkchoiceState} {payloadAttributes}";
            if (!_beaconSyncStrategy.IsBeaconSyncHeadersFinished())
            {
                _blockCacheService.SyncingHead = forkchoiceState.HeadBlockHash;
                if (_logger.IsInfo) { _logger.Info($"Syncing... Request: {requestStr}"); }
                return ForkchoiceUpdatedV1Result.Syncing;
            }
            
            Block? newHeadBlock = EnsureHeadBlockHash(forkchoiceState.HeadBlockHash);
            if (newHeadBlock == null)
            {
                if (_blockCacheService.BlockCache.TryGetValue(forkchoiceState.HeadBlockHash, out Block? block))
                {
                    _mergeSyncController.InitSyncing(block.Header);
                    _blockCacheService.SyncingHead = forkchoiceState.HeadBlockHash;
                    
                    if (_logger.IsInfo) { _logger.Info($"Syncing... Request: {requestStr}"); }
                    return ForkchoiceUpdatedV1Result.Syncing;
                }
                
                if (_logger.IsWarn) { _logger.Info($"Syncing... Unknown forkchoiceState head hash... Request: {requestStr}"); }
                return ForkchoiceUpdatedV1Result.Syncing;
            }
            
            if (!_beaconSyncStrategy.IsBeaconSyncFinished(newHeadBlock.Header))
            {
                _peerRefresher.RefreshPeers(newHeadBlock.Hash!);
                _blockCacheService.SyncingHead = forkchoiceState.HeadBlockHash;
                if (_logger.IsInfo) { _logger.Info($"Syncing beacon headers... Request: {requestStr}"); }
                return ForkchoiceUpdatedV1Result.Syncing;
            }

            // TODO: beaconsync investigate why this would occur
            if (newHeadBlock.Header.TotalDifficulty == 0)
            {
                newHeadBlock.Header.TotalDifficulty = _blockTree.BackFillTotalDifficulty(_beaconPivot.PivotNumber, newHeadBlock.Number);
            }
            
            if (_logger.IsInfo) _logger.Info($"Block {newHeadBlock} was processed");

                (BlockHeader? finalizedHeader, string? finalizationErrorMsg) =
                ValidateHashForFinalization(forkchoiceState.FinalizedBlockHash);
            if (finalizationErrorMsg != null)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Invalid finalized block hash {finalizationErrorMsg}. Request: {requestStr}");

                return ForkchoiceUpdatedV1Result.Error(finalizationErrorMsg, ErrorCodes.InvalidParams);
            }

            (BlockHeader? confirmedHeader, string? safeBlockErrorMsg) =
                ValidateSafeBlockHash(forkchoiceState.SafeBlockHash);
            if (safeBlockErrorMsg != null)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Invalid safe block hash {finalizationErrorMsg}. Request: {requestStr}");

                return ForkchoiceUpdatedV1Result.Error(safeBlockErrorMsg, ErrorCodes.InvalidParams);
            }

            (Block[]? blocks, string? setHeadErrorMsg) =
                EnsureNewHeadHeader(newHeadBlock);
            if (setHeadErrorMsg != null)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Invalid new head block {setHeadErrorMsg}. Request: {requestStr}");

                return ForkchoiceUpdatedV1Result.Error(setHeadErrorMsg, ErrorCodes.InvalidParams);
            }

            if (_poSSwitcher.TerminalTotalDifficulty == null ||
                newHeadBlock!.Header.TotalDifficulty < _poSSwitcher.TerminalTotalDifficulty)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Invalid terminal block. Nethermind TTD {_poSSwitcher.TerminalTotalDifficulty}, NewHeadBlock TD: {newHeadBlock!.Header.TotalDifficulty}. Request: {requestStr}");
                return ForkchoiceUpdatedV1Result.InvalidTerminalBlock;
            }
 
            if (payloadAttributes != null && newHeadBlock!.Timestamp >= payloadAttributes.Timestamp)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Invalid payload attributes timestamp {payloadAttributes.Timestamp}, block timestamp {newHeadBlock!.Timestamp}. Request: {requestStr}");

                return ForkchoiceUpdatedV1Result.Error(
                    $"Invalid payload attributes timestamp {payloadAttributes.Timestamp}, block timestamp {newHeadBlock!.Timestamp}. Request: {requestStr}",
                    ErrorCodes.InvalidParams);
            }

            bool newHeadTheSameAsCurrentHead = _blockTree.Head!.Hash == newHeadBlock.Hash;
            if (_blockTree.IsMainChain(forkchoiceState.HeadBlockHash) && !newHeadTheSameAsCurrentHead)
            {
                if (_logger.IsInfo) { _logger.Info($"VALID. ForkchoiceUpdated ignored - already in canonical chain. Request: {requestStr}"); }
                return ForkchoiceUpdatedV1Result.Valid(null, forkchoiceState.HeadBlockHash);
            }

            EnsureTerminalBlock(forkchoiceState, blocks);

            if (ShouldFinalize(forkchoiceState.FinalizedBlockHash))
            {
                _manualBlockFinalizationManager.MarkFinalized(newHeadBlock!.Header, finalizedHeader!);
            }
            else if (_manualBlockFinalizationManager.LastFinalizedHash != Keccak.Zero)
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Cannot finalize block. The current finalized block is: {_manualBlockFinalizationManager.LastFinalizedHash}, the requested hash: {forkchoiceState.FinalizedBlockHash}");


            // In future safeBlockHash will be added to JSON-RPC
            _blockConfirmationManager.Confirm(confirmedHeader!.Hash!);
            string? payloadId = null;

            bool headUpdated = false;
            bool shouldUpdateHead = blocks != null && !newHeadTheSameAsCurrentHead;
            if (shouldUpdateHead)
            {
                _blockTree.UpdateMainChain(blocks!, true, true);
                headUpdated = _blockTree.Head == newHeadBlock;
            }

            if (headUpdated && shouldUpdateHead)
            {
                _poSSwitcher.ForkchoiceUpdated(newHeadBlock!.Header, forkchoiceState.FinalizedBlockHash);
                if (_logger.IsInfo) _logger.Info($"Block {forkchoiceState.HeadBlockHash} was set as head");
            }
            else if (headUpdated == false && shouldUpdateHead)
            {
                if (_logger.IsWarn) _logger.Warn($"Block {forkchoiceState.FinalizedBlockHash} was not set as head.");
            }

            if (payloadAttributes != null)
            {
                payloadId = _payloadPreparationService.StartPreparingPayload(newHeadBlock!.Header, payloadAttributes);
            }

            if (_logger.IsInfo) { _logger.Info($"Valid. Request: {requestStr}"); }
            return ForkchoiceUpdatedV1Result.Valid(payloadId, forkchoiceState.HeadBlockHash);
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
                BlockHeader? parent = null;
                for (int i = 0; i < blocks.Length; ++i)
                {
                    if (blocks[i].TotalDifficulty < _poSSwitcher.TerminalTotalDifficulty)
                        parent = blocks[i].Header;
                    else
                    {
                        if (_poSSwitcher.TryUpdateTerminalBlock(blocks[i].Header, parent))
                        {
                            if (_logger.IsInfo)
                                _logger.Info($"Terminal block {blocks[i].Header} updated during the forkchoice");
                        }

                        break;
                    }
                }
            }
        }

        private Block? EnsureHeadBlockHash(Keccak headBlockHash)
        {
            Block? block = _blockTree.FindBlock(headBlockHash, BlockTreeLookupOptions.None);
            if (block is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Syncing... Block {headBlockHash} not found.");
            }

            return block;
        }

        private (BlockHeader? BlockHeader, string? ErrorMsg) ValidateSafeBlockHash(Keccak confirmedBlockHash)
        {
            string? errorMsg = null;
            BlockHeader? blockHeader = _blockTree.FindHeader(confirmedBlockHash, BlockTreeLookupOptions.None);
            if (blockHeader is null)
            {
                errorMsg = $"Block {confirmedBlockHash} not found for confirmation.";
                if (_logger.IsWarn) _logger.Warn(errorMsg);
            }

            return (blockHeader, errorMsg);
        }

        private (Block[]? Blocks, string? ErrorMsg) EnsureNewHeadHeader(Block newHeadBlock)
        {
            string? errorMsg = null;
            if (_blockTree.Head!.Hash == newHeadBlock!.Hash)
            {
                return (null, errorMsg);
            }

            if (!TryGetBranch(newHeadBlock, out Block[] branchOfBlocks))
            {
                errorMsg =
                    $"Block's {newHeadBlock} main chain predecessor cannot be found and it will not be set as head.";
                if (_logger.IsWarn) _logger.Warn(errorMsg);
            }

            return (branchOfBlocks, errorMsg);
        }

        private (BlockHeader? BlockHeader, string? ErrorMsg) ValidateHashForFinalization(Keccak finalizedBlockHash)
        {
            string? errorMsg = null;
            BlockHeader? blockHeader = _blockTree.FindHeader(finalizedBlockHash, BlockTreeLookupOptions.None);

            if (ShouldFinalize(finalizedBlockHash))
            {
                blockHeader = _blockTree.FindHeader(finalizedBlockHash, BlockTreeLookupOptions.None);
                if (blockHeader is null)
                {
                    errorMsg = $"Block {finalizedBlockHash} not found for finalization.";
                    if (_logger.IsWarn) _logger.Warn(errorMsg);
                }
            }

            return (blockHeader, errorMsg);
        }

        private bool ShouldFinalize(Keccak finalizedBlockHash) => finalizedBlockHash != Keccak.Zero;

        private bool TryGetBranch(Block newHeadBlock, out Block[] blocks)
        {
            List<Block> blocksList = new() { newHeadBlock };
            Block? predecessor = newHeadBlock;

            while (!_blockTree.IsMainChain(predecessor.Header))
            {
                predecessor = _blockTree.FindParent(predecessor, BlockTreeLookupOptions.None);
                if (predecessor == null)
                {
                    blocks = Array.Empty<Block>();
                    return false;
                }

                blocksList.Add(predecessor);
            }

            blocksList.Reverse();
            blocks = blocksList.ToArray();
            return true;
        }
    }
}
