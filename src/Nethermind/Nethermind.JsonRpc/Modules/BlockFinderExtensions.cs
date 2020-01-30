//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules
{
    public static class BlockFinderExtensions
    {
        public static SearchResult<BlockHeader> SearchForHeader(this IBlockFinder blockFinder, BlockParameter blockParameter)
        {
            BlockHeader header;
            if (blockParameter.RequireCanonical)
            {
                header = blockFinder.FindHeader(blockParameter.BlockHash, BlockTreeLookupOptions.RequireCanonical);
                if (header == null)
                {
                    header = blockFinder.FindHeader(blockParameter.BlockHash);
                    if (header != null)
                    {
                        return new SearchResult<BlockHeader>($"{blockParameter.BlockHash} block is not canonical", ErrorCodes.InvalidInput);
                    }
                }
            }
            else
            {
                header = blockFinder.FindHeader(blockParameter);
            }

            return header == null
                ? new SearchResult<BlockHeader>($"{blockParameter.BlockHash} could not be found", ErrorCodes.ResourceNotFound)
                : new SearchResult<BlockHeader>(header);
        }
        
        public static SearchResult<Block> SearchForBlock(this IBlockFinder blockFinder, BlockParameter blockParameter)
        {
            Block block;
            if (blockParameter.RequireCanonical)
            {
                block = blockFinder.FindBlock(blockParameter.BlockHash, BlockTreeLookupOptions.RequireCanonical);
                if (block == null)
                {
                    var header = blockFinder.FindHeader(blockParameter.BlockHash);
                    if (header != null)
                    {
                        return new SearchResult<Block>($"{blockParameter.BlockHash} block is not canonical", ErrorCodes.InvalidInput);
                    }
                }
            }
            else
            {
                block = blockFinder.FindBlock(blockParameter);
            }

            return block == null
                ? new SearchResult<Block>($"{blockParameter.BlockHash} could not be found", ErrorCodes.ResourceNotFound)
                : new SearchResult<Block>(block);
        }
    }
}