/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Blockchain
{
    public class BlockhashProvider : IBlockhashProvider
    {
        private static int _maxDepth = 256;
        private readonly IBlockTree _blockTree;
        private ILogger _logger;

        public BlockhashProvider(IBlockTree blockTree, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public Keccak GetBlockhash(BlockHeader currentBlock, in long number)
        {
            long current = currentBlock.Number;
            if (number >= current || number < current - Math.Min(current, _maxDepth))
            {
                return null;
            }

            bool isFastSyncSearch = false;

            BlockHeader header = _blockTree.FindHeader(currentBlock.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            for (var i = 0; i < _maxDepth; i++)
            {
                if (number == header.Number)
                {
                    return header.Hash;
                }

                header = _blockTree.FindHeader(header.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (_blockTree.IsMainChain(header.Hash) && !isFastSyncSearch)
                {
                    try
                    {
                        header = _blockTree.FindHeader(number, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    }
                    catch (InvalidOperationException) // fast sync during the first 256 blocks after the transition
                    {
                        isFastSyncSearch = true;
                    }
                }
            }

            return null;
        }
    }
}