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
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.State;
using Result = Nethermind.Merge.Plugin.Data.Result;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class SetHeadBlockHandler : IHandler<Keccak, Result>
    {
        private readonly IBlockTree _blockTree;
        private readonly IStateProvider _stateProvider;
        private readonly ILogger _logger;

        public SetHeadBlockHandler(IBlockTree blockTree, IStateProvider stateProvider, ILogManager logManager)
        {
            _blockTree = blockTree;
            _stateProvider = stateProvider;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<Result> Handle(Keccak blockHash)
        {
            Block? newHeadBlock = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
            if (newHeadBlock == null)
            {
                if (_logger.IsWarn) _logger.Warn($"Block {blockHash} cannot be found and it will not be set as head.");
                return ResultWrapper<Result>.Success(Result.Fail);
            }

            if (!TryGetBranch(newHeadBlock, out Block[] blocks))
            {
                if (_logger.IsWarn) _logger.Warn($"Block's {blockHash} main chain predecessor cannot be found and it will not be set as head.");
                return ResultWrapper<Result>.Success(Result.Fail);
            }

            _blockTree.UpdateMainChain(blocks, true, true);

            bool success = _blockTree.Head == newHeadBlock;
            if (success)
            {
                _stateProvider.ResetStateTo(newHeadBlock.StateRoot!);
                if (_logger.IsInfo) _logger.Info($"Block {blockHash} was set as head.");
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Block {blockHash} was not set as head.");
            }

            return ResultWrapper<Result>.Success(success);
        }

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
