// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            int splitVariant = 0;
            BlockTree alternative = Build.A.BlockTree(blockTree.FindBlock(0, BlockTreeLookupOptions.RequireCanonical)!).OfChainLength(branchLength, splitVariant).TestObject;
            Block? parent = null;
            for (int i = splitBlockNumber + 1; i < branchLength; i++)
            {
                Block block = alternative.FindBlock(i, BlockTreeLookupOptions.RequireCanonical)!;
                if (i == splitBlockNumber + 1)
                {
                    Block? mainBlock = blockTree.FindBlock(i - 1, BlockTreeLookupOptions.RequireCanonical);
                    if (mainBlock is not null)
                        parent = mainBlock;
                }

                block.Header.ParentHash = parent?.Hash;
                block.Header.StateRoot = parent?.StateRoot;
                block.Header.Hash = block.Header.CalculateHash();
                parent = block;
                bool shouldProcess = i == branchLength - 1;
                BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ForceDontSetAsMain;
                options |= shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;
                blockTree.SuggestBlock(block, options);
            }
        }

        public static void AddBranch(this BlockTree blockTree, int branchLength, int splitBlockNumber, int splitVariant)
        {
            BlockTree alternative = Build.A.BlockTree(blockTree.FindBlock(0, BlockTreeLookupOptions.RequireCanonical)!).OfChainLength(branchLength, splitVariant).TestObject;
            List<Block> blocks = new();
            for (int i = splitBlockNumber + 1; i < branchLength; i++)
            {
                Block block = alternative.FindBlock(i, BlockTreeLookupOptions.RequireCanonical)!;
                blockTree.SuggestBlock(block);
                blocks.Add(block);
            }

            if (branchLength > blockTree.Head!.Number)
            {
                blockTree.UpdateMainChain(blocks, true);
            }
        }

        public static void UpdateMainChain(this BlockTree blockTree, Block block)
        {
            blockTree.UpdateMainChain(new[] { block }, true);
        }
    }
}
