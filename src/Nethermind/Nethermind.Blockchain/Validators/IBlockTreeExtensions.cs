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

using Nethermind.Core;

namespace Nethermind.Blockchain.Validators
{
    // ReSharper disable once InconsistentNaming
    public static class IBlockTreeExtensions
    {
        public static BlockHeader FindParentHeader(this IBlockTree tree, BlockHeader header)
        {
            return tree.FindHeader(header.ParentHash, false);
        }

        public static Block FindParent(this IBlockTree tree, Block block)
        {
            return tree.FindBlock(block.Header.ParentHash, false);
        }
        
        public static Block FindParent(this IBlockTree tree, BlockHeader blockHeader)
        {
            return tree.FindBlock(blockHeader.ParentHash, false);
        }

        public static Block RetrieveBestSuggestedBlock(this IBlockTree tree)
        {
            return tree.FindBlock(tree.BestSuggested.Hash, false);
        }

        public static Block RetrieveHeadBlock(this IBlockTree tree)
        {
            return tree.FindBlock(tree.Head.Hash, false);
        }

        public static Block RetrieveGenesisBlock(this IBlockTree tree)
        {
            return tree.FindBlock(tree.Genesis.Hash, true);
        }
    }
}