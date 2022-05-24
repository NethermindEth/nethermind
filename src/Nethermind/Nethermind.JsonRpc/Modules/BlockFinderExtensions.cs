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

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules
{
    public static class BlockFinderExtensions
    {
        public static SearchResult<BlockHeader> SearchForHeader(this IBlockFinder blockFinder, BlockParameter? blockParameter, bool allowNulls = false)
        {
            if (blockFinder.Head == null)
            {
                return new SearchResult<BlockHeader>("Incorrect head block", ErrorCodes.InternalError);
            }
            
            blockParameter ??= BlockParameter.Latest;

            BlockHeader header;
            if (blockParameter.RequireCanonical)
            {
                header = blockFinder.FindHeader(blockParameter.BlockHash, BlockTreeLookupOptions.RequireCanonical);
                if (header == null && !allowNulls)
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

            return header == null && !allowNulls
                ? new SearchResult<BlockHeader>($"{blockParameter.BlockHash?.ToString() ?? blockParameter.BlockNumber?.ToString() ?? blockParameter.Type.ToString()} could not be found", ErrorCodes.ResourceNotFound)
                : new SearchResult<BlockHeader>(header);
        }
        
        public static SearchResult<Block> SearchForBlock(this IBlockFinder blockFinder, BlockParameter blockParameter, bool allowNulls = false)
        {
            Block block;
            if (blockParameter.RequireCanonical)
            {
                block = blockFinder.FindBlock(blockParameter.BlockHash, BlockTreeLookupOptions.RequireCanonical);
                if (block == null && !allowNulls)
                {
                    BlockHeader? header = blockFinder.FindHeader(blockParameter.BlockHash);
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

            if (block == null)
            {
                if (blockParameter.Equals(BlockParameter.Finalized) || blockParameter.Equals(BlockParameter.Safe))
                {
                    return new SearchResult<Block>("Unknown block error", ErrorCodes.UnknownBlockError);
                }

                if (!allowNulls)
                {
                    return new SearchResult<Block>(
                        $"Block {blockParameter.BlockHash?.ToString() ?? blockParameter.BlockNumber?.ToString() ?? blockParameter.Type.ToString()} could not be found",
                        ErrorCodes.ResourceNotFound);
                }
            }

            return new SearchResult<Block>(block);
        }

        public static IEnumerable<SearchResult<Block>> SearchForBlocksOnMainChain(this IBlockFinder blockFinder, BlockParameter fromBlock, BlockParameter toBlock)
        {
            SearchResult<Block> startingBlock = SearchForBlock(blockFinder, fromBlock);
            if (startingBlock.IsError || startingBlock.Object == null)
                yield return startingBlock;
            else
            {
                SearchResult<BlockHeader> finalBlockHeader = SearchForHeader(blockFinder, toBlock);
                if (finalBlockHeader.IsError || finalBlockHeader.Object == null)
                    yield return new SearchResult<Block>(finalBlockHeader.Error ?? string.Empty, finalBlockHeader.ErrorCode);
                bool isFinalBlockOnMainChain =  blockFinder.IsMainChain(finalBlockHeader.Object!);
                bool isStartingBlockOnMainChain =  blockFinder.IsMainChain(startingBlock.Object.Header);
                if (!isFinalBlockOnMainChain || !isStartingBlockOnMainChain)
                {
                    Keccak? notCanonicalBlockHash = isFinalBlockOnMainChain
                        ? startingBlock.Object.Hash
                        : finalBlockHeader.Object.Hash;
                    yield return new SearchResult<Block>($"{notCanonicalBlockHash} block is not canonical", ErrorCodes.InvalidInput);
                }
                else
                {
                    yield return startingBlock;
                    long startingBlockNumber = startingBlock.Object.Number;
                    long finalBlockNumber = finalBlockHeader.Object.Number;
                    if (startingBlockNumber > finalBlockNumber)
                    {
                        yield return new SearchResult<Block>($"From block number: {startingBlockNumber} is greater than to block number {finalBlockNumber}", ErrorCodes.InvalidInput);
                    }
                    
                    for (long i = startingBlock.Object.Number + 1; i <= finalBlockHeader.Object.Number; ++i)
                    {
                        yield return SearchForBlock(blockFinder, new BlockParameter(i));
                    }
                }

            }
            
        }
    }
}
