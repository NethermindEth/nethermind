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

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Result = Nethermind.Merge.Plugin.Data.Result;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class SetHeadBlockHandler : IHandler<Keccak, Result>
    {
        private readonly IBlockTree _blockTree;
        private readonly SemaphoreSlim _locker;
        private readonly ILogger _logger;

        public SetHeadBlockHandler(IBlockTree blockTree, ILogManager logManager, SemaphoreSlim locker)
        {
            _blockTree = blockTree;
            _locker = locker;
            _logger = logManager.GetClassLogger();
        }
        
        public ResultWrapper<Result> Handle(Keccak blockHash)
        {
            _locker.Wait();
            try
            {
                Block? block = _blockTree.FindBlock(blockHash);
                if (block == null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Block {blockHash} cannot be found and will not be set as head.");
                    ResultWrapper<Result>.Success(Result.Fail);
                }

                List<Block> blocks = new();

                while (!_blockTree.IsMainChain(block!.Header))
                {
                    blocks.Add(block);
                    block = _blockTree.FindParent(block, BlockTreeLookupOptions.None);
                }

                blocks.Reverse();

                _blockTree.UpdateMainChain(blocks.ToArray(), true, true);
                bool success = _blockTree.Head == block;
                if (success)
                {
                    if (_logger.IsInfo) _logger.Info($"Block {blockHash} was set as head.");
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn($"Block {blockHash} was not set as head.");
                }

                return ResultWrapper<Result>.Success(success);
            }
            finally
            {
                _locker.Release();
            }
        }
    }
}
