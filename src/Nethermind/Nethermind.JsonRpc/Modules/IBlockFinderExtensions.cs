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

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules
{
    public static class IBlockFinderExtensions
    {
        public static Block GetBlock(this IBlockFinder blockFinder, BlockParameter blockParameter, bool allowNulls = false)
        {
            Block block;
            switch (blockParameter.Type)
            {
                case BlockParameterType.BlockNumber:
                {
                    if (blockParameter.BlockNumber == null)
                    {
                        throw new JsonRpcException(ErrorCodes.InvalidParams, $"Block number is required for {BlockParameterType.BlockNumber}");
                    }

                    block = blockFinder.FindBlock(blockParameter);
                    break;
                }

                case BlockParameterType.Pending:
                case BlockParameterType.Latest:
                case BlockParameterType.Earliest:
                {
                    block = blockFinder.FindBlock(blockParameter);
                    break;
                }

                default:
                    throw new ArgumentException($"{nameof(BlockParameterType)} not supported: {blockParameter.Type}");
            }

            if (block == null && !allowNulls)
            {
                throw new JsonRpcException(ErrorCodes.NotFound, $"Cannot find block {blockParameter}");
            }
            
            return block;
        }
        
        public static BlockHeader GetHeader(this IBlockFinder blockFinder, BlockParameter blockParameter, bool allowNulls = false)
        {
            BlockHeader header;
            switch (blockParameter.Type)
            {
                case BlockParameterType.BlockNumber:
                {
                    if (blockParameter.BlockNumber == null)
                    {
                        throw new JsonRpcException(ErrorCodes.InvalidParams, $"Block number is required for {BlockParameterType.BlockNumber}");
                    }

                    header = blockFinder.FindHeader(blockParameter);
                    break;
                }

                case BlockParameterType.Pending:
                case BlockParameterType.Latest:
                case BlockParameterType.Earliest:
                {
                    header = blockFinder.FindHeader(blockParameter);
                    break;
                }

                default:
                    throw new ArgumentException($"{nameof(BlockParameterType)} not supported: {blockParameter.Type}");
            }

            if (header == null && !allowNulls)
            {
                throw new JsonRpcException(ErrorCodes.NotFound, $"Cannot find block {blockParameter}");
            }
            
            return header;
        }
    }
}