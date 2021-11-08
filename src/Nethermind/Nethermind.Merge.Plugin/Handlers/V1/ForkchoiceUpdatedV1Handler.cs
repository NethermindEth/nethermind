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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
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
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers.V1
{
    /// <summary>
    /// https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md
    /// Propagates the change in the fork choice to the execution client
    /// </summary>
    public class ForkchoiceUpdatedV1Handler : IForkchoiceUpdatedV1Handler
    {
        private readonly IBlockTree _blockTree;
        private readonly IStateProvider _stateProvider;
        private readonly IManualBlockFinalizationManager _manualBlockFinalizationManager;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private readonly IBlockConfirmationManager _blockConfirmationManager;
        private readonly IPayloadService _payloadService;
        private readonly ILogger _logger;

        public ForkchoiceUpdatedV1Handler(
            IBlockTree blockTree,
            IStateProvider stateProvider,
            IManualBlockFinalizationManager manualBlockFinalizationManager, 
            IPoSSwitcher poSSwitcher,
            IEthSyncingInfo ethSyncingInfo,
            IBlockConfirmationManager blockConfirmationManager,
            IPayloadService payloadService,
            ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _manualBlockFinalizationManager = manualBlockFinalizationManager ?? throw new ArgumentNullException(nameof(manualBlockFinalizationManager));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
            _blockConfirmationManager = blockConfirmationManager ?? throw new ArgumentNullException(nameof(blockConfirmationManager));
            _payloadService = payloadService;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<ForkchoiceUpdatedV1Result> Handle(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
        {
            if (_ethSyncingInfo.IsSyncing())
            {
                return ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result() { Status = EngineStatus.Syncing});
            }
            
            (BlockHeader? finalizedHeader, string? finalizationErrorMsg) = EnsureHeaderForFinalization(forkchoiceState.FinalizedBlockHash);
            if (finalizationErrorMsg != null)
                return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(finalizationErrorMsg, MergeErrorCodes.UnknownHeader);
            
            (BlockHeader? confirmedHeader, string? confirmationErrorMsg) = EnsureHeaderForConfirmation(forkchoiceState.SafeBlockHash);
            if (confirmationErrorMsg != null)
                return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(confirmationErrorMsg, MergeErrorCodes.UnknownHeader);
            
            (Block? newHeadBlock, Block[]? blocks, string? setHeadErrorMsg) = EnsureBlocksForSetHead(forkchoiceState.HeadBlockHash);
            if (setHeadErrorMsg != null)
                return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(setHeadErrorMsg, MergeErrorCodes.UnknownHeader);
 
            if (ShouldFinalize(forkchoiceState.FinalizedBlockHash))
                _manualBlockFinalizationManager.MarkFinalized(newHeadBlock!.Header, finalizedHeader!);
            else if (_manualBlockFinalizationManager.LastFinalizedHash != Keccak.Zero)
                if (_logger.IsWarn) _logger.Warn($"Cannot finalize block. The current finalized block is: {_manualBlockFinalizationManager.LastFinalizedHash}, the requested hash: {forkchoiceState.FinalizedBlockHash}");
            
            // _blockConfirmationManager.Confirm(confirmedHeader);
            byte[] payloadId = Array.Empty<byte>();
            _blockTree.UpdateMainChain(blocks!, true, true);
            bool success = _blockTree.Head == newHeadBlock;
            if (success)
            {
                _poSSwitcher.TrySwitchToPos(newHeadBlock!.Header);
                _stateProvider.ResetStateTo(newHeadBlock.StateRoot!);
                if (_logger.IsInfo) _logger.Info($"Block {forkchoiceState.HeadBlockHash} was set as head");

                if (payloadAttributes != null)
                {
                    payloadId = _payloadService.StartPreparingPayload(newHeadBlock!.Header, payloadAttributes);
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Block {forkchoiceState.FinalizedBlockHash} was not set as head.");
            }



            return ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result() { PayloadId = payloadId.ToHexString(), Status = EngineStatus.Success});
        }

        private (BlockHeader? BlockHeader, string? ErrorMsg) EnsureHeaderForConfirmation(Keccak confirmedBlockHash)
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

        private (Block? NewHeadBlock, Block[]? Blocks, string? ErrorMsg) EnsureBlocksForSetHead(Keccak headBlockHash)
        {
            string? errorMsg = null;
            Block? headBlock = _blockTree.FindBlock(headBlockHash, BlockTreeLookupOptions.None);
            if (headBlock == null)
            {
                errorMsg = $"Block {headBlockHash} cannot be found and it will not be set as head.";
                if (_logger.IsWarn) _logger.Warn(errorMsg);
                return (headBlock, null, errorMsg);
            }

            if (!TryGetBranch(headBlock, out Block[] branchOfBlocks))
            {
                errorMsg = $"Block's {headBlockHash} main chain predecessor cannot be found and it will not be set as head.";
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
                    errorMsg = $"Block {finalizedBlockHash} not found for finalization.";
                    if (_logger.IsWarn) _logger.Warn(errorMsg);
                }
            }

            return (blockHeader, errorMsg);
        }

        private bool ShouldFinalize(Keccak finalizedBlockHash) => finalizedBlockHash != Keccak.Zero;

        private bool TryGetBranch(Block block, out Block[] blocks)
        {
            List<Block> blocksList = new() {block};
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
                
            };
            
            blocksList.Reverse();
            blocks = blocksList.ToArray();
            return true;
        }
    }
}
