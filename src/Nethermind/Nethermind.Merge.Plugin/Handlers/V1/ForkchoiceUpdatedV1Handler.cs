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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
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
        private readonly IPayloadService _payloadService;
        private readonly ISynchronizer _synchronizer;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;
        private bool synced = false;

        public ForkchoiceUpdatedV1Handler(
            IBlockTree blockTree,
            IManualBlockFinalizationManager manualBlockFinalizationManager,
            IPoSSwitcher poSSwitcher,
            IEthSyncingInfo ethSyncingInfo,
            IBlockConfirmationManager blockConfirmationManager,
            IPayloadService payloadService,
            ISynchronizer synchronizer,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _manualBlockFinalizationManager = manualBlockFinalizationManager ??
                                              throw new ArgumentNullException(nameof(manualBlockFinalizationManager));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
            _blockConfirmationManager = blockConfirmationManager ??
                                        throw new ArgumentNullException(nameof(blockConfirmationManager));
            _payloadService = payloadService;
            _synchronizer = synchronizer;
            _syncConfig = syncConfig;
            _logger = logManager.GetClassLogger();
        }

        public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Handle(ForkchoiceStateV1 forkchoiceState,
            PayloadAttributes? payloadAttributes)
        {
            if (_syncConfig.FastSync && _blockTree.LowestInsertedBodyNumber != 0)
            {
                return ForkchoiceUpdatedV1Result.Syncing;
            }

            (BlockHeader? finalizedHeader, string? finalizationErrorMsg) =
                EnsureHeaderForFinalization(forkchoiceState.FinalizedBlockHash);
            if (finalizationErrorMsg != null)
                return ForkchoiceUpdatedV1Result.Syncing;

            (BlockHeader? confirmedHeader, string? confirmationErrorMsg) =
                EnsureHeaderForConfirmation(forkchoiceState.SafeBlockHash);
            if (confirmationErrorMsg != null)
                return ForkchoiceUpdatedV1Result.Syncing;

            (Block? newHeadBlock, Block[]? blocks, string? setHeadErrorMsg) =
                EnsureBlocksForSetHead(forkchoiceState.HeadBlockHash);
            if (setHeadErrorMsg != null)
                return ForkchoiceUpdatedV1Result.Syncing;

            // if (_ethSyncingInfo.IsSyncing() && synced == false)
            // {
            //     return ForkchoiceUpdatedV1Result.Syncing;
            // }
            // else if (synced == false)
            // {
            //     await _synchronizer.StopAsync();
            //     synced = true;
            // }
            
            await _synchronizer.StopAsync();

            if (_poSSwitcher.TerminalTotalDifficulty == null ||
                newHeadBlock!.Header.TotalDifficulty < _poSSwitcher.TerminalTotalDifficulty)
            {
                return ForkchoiceUpdatedV1Result.InvalidTerminalBlock;
            }

            if (payloadAttributes != null && newHeadBlock!.Timestamp >= payloadAttributes.Timestamp)
            {
                return ForkchoiceUpdatedV1Result.Error(
                    $"Invalid payload attributes timestamp: {payloadAttributes.Timestamp} parent block header: {newHeadBlock!.Header}",
                    ErrorCodes.InvalidParams);
            }

            bool newHeadTheSameAsCurrentHead = _blockTree.Head!.Hash == newHeadBlock.Hash;
            if (_blockTree.IsMainChain(forkchoiceState.HeadBlockHash) && !newHeadTheSameAsCurrentHead)
            {
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
            byte[]? payloadId = null;

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
                payloadId = await _payloadService.StartPreparingPayload(newHeadBlock!.Header, payloadAttributes);
            }

            return ForkchoiceUpdatedV1Result.Valid(payloadId?.ToHexString(true), forkchoiceState.HeadBlockHash);
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

        private (BlockHeader? BlockHeader, string? ErrorMsg) EnsureHeaderForConfirmation(Keccak confirmedBlockHash)
        {
            string? errorMsg = null;
            BlockHeader? blockHeader = _blockTree.FindHeader(confirmedBlockHash, BlockTreeLookupOptions.None);
            if (blockHeader is null)
            {
                errorMsg = $"Syncing... Block {confirmedBlockHash} not found for confirmation.";
                if (_logger.IsWarn) _logger.Warn(errorMsg);
            }

            return (blockHeader, errorMsg);
        }

        private (Block? NewHeadBlock, Block[]? Blocks, string? ErrorMsg) EnsureBlocksForSetHead(Keccak headBlockHash)
        {
            string? errorMsg = null;
            Block? headBlock = _blockTree.FindBlock(headBlockHash, BlockTreeLookupOptions.None);
            if (headBlock == null)
            {
                errorMsg = $"Syncing... Block {headBlockHash} cannot be found and it will not be set as head.";
                if (_logger.IsWarn) _logger.Warn(errorMsg);
                return (headBlock, null, errorMsg);
            }

            if (_blockTree.Head!.Hash == headBlockHash)
            {
                return (headBlock, null, errorMsg);
            }

            if (!TryGetBranch(headBlock, out Block[] branchOfBlocks))
            {
                errorMsg =
                    $"Syncing... Block's {headBlockHash} main chain predecessor cannot be found and it will not be set as head.";
                if (_logger.IsWarn) _logger.Warn(errorMsg);
            }

            return (headBlock, branchOfBlocks, errorMsg);
        }

        private (BlockHeader? BlockHeader, string? ErrorMsg) EnsureHeaderForFinalization(Keccak finalizedBlockHash)
        {
            string? errorMsg = null;
            BlockHeader? blockHeader = _blockTree.FindHeader(finalizedBlockHash, BlockTreeLookupOptions.None);

            if (ShouldFinalize(finalizedBlockHash))
            {
                blockHeader = _blockTree.FindHeader(finalizedBlockHash, BlockTreeLookupOptions.None);
                if (blockHeader is null)
                {
                    errorMsg = $"Syncing... Block {finalizedBlockHash} not found for finalization.";
                    if (_logger.IsWarn) _logger.Warn(errorMsg);
                }
            }

            return (blockHeader, errorMsg);
        }

        private bool ShouldFinalize(Keccak finalizedBlockHash) => finalizedBlockHash != Keccak.Zero;

        private bool TryGetBranch(Block block, out Block[] blocks)
        {
            List<Block> blocksList = new() { block };
            Block? predecessor = block;

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

            ;

            blocksList.Reverse();
            blocks = blocksList.ToArray();
            return true;
        }
    }
}
