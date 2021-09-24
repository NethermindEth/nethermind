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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;
using Result = Nethermind.Merge.Plugin.Data.Result;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class ForkChoiceUpdatedHandler : IHandler<ForkChoiceUpdatedRequest, Result>
    {
        private readonly IBlockTree _blockTree;
        private readonly IStateProvider _stateProvider;
        private readonly IManualBlockFinalizationManager _manualBlockFinalizationManager;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly ILogger _logger;

        public ForkChoiceUpdatedHandler(
            IBlockTree blockTree,
            IStateProvider stateProvider,
            IManualBlockFinalizationManager manualBlockFinalizationManager, 
            IPoSSwitcher poSSwitcher,
            ILogManager logManager)
        {
            _blockTree = blockTree;
            _stateProvider = stateProvider;
            _manualBlockFinalizationManager = manualBlockFinalizationManager;
            _poSSwitcher = poSSwitcher;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<Result> Handle(ForkChoiceUpdatedRequest request)
        {
            (BlockHeader? finalizedHeader, string? finalizationErrorMsg) = EnsureHeaderForFinalization(request.FinalizedBlockHash);
            if (finalizationErrorMsg != null)
                return ResultWrapper<Result>.Success(Result.Fail);
            (BlockHeader? confirmedHeader, string? confirmationErrorMsg) = EnsureHeaderForConfirmation(request.ConfirmedBlockHash);
            if (confirmationErrorMsg != null)
                return ResultWrapper<Result>.Success(Result.Fail);
            (Block? newHeadBlock, Block[]? blocks, string? setHeadErrorMsg) = EnsureBlocksForSetHead(request.HeadBlockHash);
            if (setHeadErrorMsg != null)
                return ResultWrapper<Result>.Success(Result.Fail);
 
            if (ShouldFinalize(request.FinalizedBlockHash))
                _manualBlockFinalizationManager.MarkFinalized(newHeadBlock!.Header, finalizedHeader!);
            else if (_manualBlockFinalizationManager.LastFinalizedHash != Keccak.Zero)
                if (_logger.IsWarn) _logger.Warn($"Cannot finalize block. The current finalized block is: {_manualBlockFinalizationManager.LastFinalizedHash}, the requested hash: {request.FinalizedBlockHash}");
            
            _blockTree.UpdateMainChain(blocks!, true, true);
            bool success = _blockTree.Head == newHeadBlock;
            if (success)
            {
                _poSSwitcher.TrySwitchToPos(newHeadBlock!.Header);
                _stateProvider.ResetStateTo(newHeadBlock.StateRoot!);
                if (_logger.IsInfo) _logger.Info($"Block {request.FinalizedBlockHash} was set as head.");
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Block {request.FinalizedBlockHash} was not set as head.");
            }
            
            // ToDo add confirmation to block tree

            return ResultWrapper<Result>.Success(Result.Ok);
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
            
            do
            {
                predecessor = _blockTree.FindParent(predecessor, BlockTreeLookupOptions.None);
                if (predecessor == null)
                {
                    blocks = Array.Empty<Block>();
                    return false;
                }
                blocksList.Add(predecessor);
                
            } while (!_blockTree.IsMainChain(predecessor.Header));
            
            blocksList.Reverse();
            blocks = blocksList.ToArray();
            return true;
        }
    }
}
