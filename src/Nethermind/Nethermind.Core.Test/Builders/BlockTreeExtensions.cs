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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Core.Test.Builders
{
    public static class BlockTreeExtensions
    {
        public static void AddBranch(this BlockTree blockTree, int branchLength, int splitBlockNumber, int splitVariant)
        {
            BlockTree alternative = Build.A.BlockTree(blockTree.FindBlock(0, BlockTreeLookupOptions.RequireCanonical)).OfChainLength(branchLength, splitVariant).TestObject;
            List<Block> blocks = new List<Block>();
            Keccak parentHash = null;
            for (int i = splitBlockNumber + 1; i < branchLength; i++)
            {
                Block block = alternative.FindBlock(i, BlockTreeLookupOptions.RequireCanonical);
                if (i == splitBlockNumber + 1)
                {
                    var mainBlock = blockTree.FindBlock(i - 1, BlockTreeLookupOptions.RequireCanonical);
                    if (mainBlock != null)
                        parentHash = mainBlock.Hash;
                    //blockTree.SuggestBlock(block);
                    //blocks.Add(block);
                }
                block.Header.ParentHash = parentHash;
                block.Header.Hash = block.Header.CalculateHash();
                parentHash = block.Hash;
                blockTree.SuggestBlock(block, i == branchLength - 1, false);
                blocks.Add(block);
                //if (i == branchLength - 1)
                //{
                //    blockTree.UpdateBestSuggestedBlock(block);
                //}

            }

            // blockTree.UpdateMainChain(blocks.ToArray(), false);
        }

        public static void AddBranch(this BlockTree blockTree, int branchLength, int splitBlockNumber, int splitVariant, bool updateMainChain)
        {
            BlockTree alternative = Build.A.BlockTree(blockTree.FindBlock(0, BlockTreeLookupOptions.RequireCanonical)).OfChainLength(branchLength, splitVariant).TestObject;
            List<Block> blocks = new List<Block>();
            Keccak parrentHash = null;
            for (int i = splitBlockNumber; i < branchLength; i++)
            {
                if (i == splitBlockNumber)
                {
                    var mainBlock = blockTree.FindBlock(i, BlockTreeLookupOptions.RequireCanonical);
                    parrentHash = mainBlock.Hash;
                    continue;
                    //blockTree.SuggestBlock(block);
                    //blocks.Add(block);
                }
                else
                {
                    Block block = alternative.FindBlock(i, BlockTreeLookupOptions.RequireCanonical);
                    block.Header.ParentHash = parrentHash;
                    blockTree.SuggestBlock(block);
                    parrentHash = block.Hash;
                    blocks.Add(block);
                }
            }

            if (updateMainChain)
            {
                blockTree.UpdateMainChain(blocks.ToArray(), false);
            }
        }

        public static void UpdateMainChain(this BlockTree blockTree, Block block)
        {
            blockTree.UpdateMainChain(new[] { block }, false);
        }
    }
}
