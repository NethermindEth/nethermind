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
using Nethermind.Blockchain.Filters;
using Nethermind.Core;

namespace Nethermind.Blockchain.Find
{
    public static class BlockFinderExtensions
    {
        public static Block GetBlock(this IBlockFinder blockFinder, FilterBlock blockFilter)
        {
            switch (blockFilter.Type)
            {
                case FilterBlockType.Pending:
                    return blockFinder.FindPendingBlock();

                case FilterBlockType.Latest:
                    return blockFinder.FindLatestBlock();

                case FilterBlockType.Earliest:
                    return blockFinder.FindEarliestBlock();

                case FilterBlockType.BlockNumber:
                    return blockFinder.FindBlock(blockFilter.BlockNumber);

                default:
                    throw new ArgumentException($"{nameof(FilterBlockType)} not supported: {blockFilter.Type}");
            }
        }
        
        public static BlockHeader GetHeader(this IBlockFinder blockFinder, FilterBlock blockFilter)
        {
            switch (blockFilter.Type)
            {
                case FilterBlockType.Pending:
                    return blockFinder.FindPendingHeader();

                case FilterBlockType.Latest:
                    return blockFinder.FindLatestHeader();

                case FilterBlockType.Earliest:
                    return blockFinder.FindEarliestHeader();

                case FilterBlockType.BlockNumber:
                    return blockFinder.FindHeader(blockFilter.BlockNumber);

                default:
                    throw new ArgumentException($"{nameof(FilterBlockType)} not supported: {blockFilter.Type}");
            }
        }
    }
}