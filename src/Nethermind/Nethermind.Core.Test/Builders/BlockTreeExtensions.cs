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

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Validators;

namespace Nethermind.Core.Test.Builders
{
    public static class BlockTreeExtensions
    {
        public static void AddBranch(this BlockTree blockTree, int branchLength, int splitBlockNumber, int splitVariant)
        {
            BlockTree alternative = Build.A.BlockTree(blockTree.RetrieveGenesisBlock()).OfChainLength(branchLength, splitVariant).TestObject;
            List<Block> blocks = new List<Block>();
            for (int i = splitBlockNumber + 1; i < branchLength; i++)
            {
                Block block = alternative.FindBlock(i, BlockTreeLookupOptions.RequireCanonical);
                blockTree.SuggestBlock(block);
                blocks.Add(block);
            }
            
            if (branchLength > blockTree.Head.Number)
            {
                blockTree.UpdateMainChain(blocks.ToArray());    
            }
        }
        
        public static void UpdateMainChain(this BlockTree blockTree, Block block)
        {
            blockTree.UpdateMainChain(new [] {block});
        }
    }
}