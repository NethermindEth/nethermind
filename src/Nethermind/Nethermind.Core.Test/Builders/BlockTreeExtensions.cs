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
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Core.Test.Builders
{
    public static class BlockTreeExtensions
    {
        public static void AddBranch(this BlockTree blockTree, int branchLength, int splitBlockNumber)
        {
            var splitVariant = 0;
            BlockTree alternative = Build.A.BlockTree(blockTree.FindBlock(0, BlockTreeLookupOptions.RequireCanonical)!).OfChainLength(branchLength, splitVariant).TestObject;
            Block? parent = null;
            for (int i = splitBlockNumber + 1; i < branchLength; i++)
            {
                Block block = alternative.FindBlock(i, BlockTreeLookupOptions.RequireCanonical)!;
                if (i == splitBlockNumber + 1)
                {
                    Block? mainBlock = blockTree.FindBlock(i - 1, BlockTreeLookupOptions.RequireCanonical);
                    if (mainBlock != null)
                        parent = mainBlock;
                }

                block.Header.ParentHash = parent?.Hash;
                block.Header.StateRoot = parent?.StateRoot;
                block.Header.Hash = block.Header.CalculateHash();
                parent = block;
                blockTree.SuggestBlock(block, i == branchLength - 1, false);
            }
        }

        public static void AddBranch(this BlockTree blockTree, int branchLength, int splitBlockNumber, int splitVariant)
        {
            BlockTree alternative = Build.A.BlockTree(blockTree.FindBlock(0, BlockTreeLookupOptions.RequireCanonical)).OfChainLength(branchLength, splitVariant).TestObject;
            List<Block> blocks = new List<Block>();
            for (int i = splitBlockNumber + 1; i < branchLength; i++)
            {
                Block block = alternative.FindBlock(i, BlockTreeLookupOptions.RequireCanonical);
                blockTree.SuggestBlock(block);
                blocks.Add(block);
            }

            if (branchLength > blockTree.Head.Number)
            {
                blockTree.UpdateMainChain(blocks.ToArray(), true);
            }
        }

        public static void UpdateMainChain(this BlockTree blockTree, Block block)
        {
            blockTree.UpdateMainChain(new[] { block }, true);
        }
    }
}
