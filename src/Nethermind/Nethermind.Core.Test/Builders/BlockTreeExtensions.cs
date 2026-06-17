// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core.Events;
using Nethermind.Crypto;

namespace Nethermind.Core.Test.Builders
{
    public static class BlockTreeExtensions
    {
        public static void AddBranch(this IBlockTree blockTree, int branchLength, int splitBlockNumber)
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

        public static void AddBranch(this IBlockTree blockTree, int branchLength, int splitBlockNumber, int splitVariant)
        {
            BlockTree alternative = Build.A.BlockTree(blockTree.FindBlock(0, BlockTreeLookupOptions.RequireCanonical)!).OfChainLength(branchLength, splitVariant).TestObject;
            List<Block> blocks = [];
            for (int i = splitBlockNumber + 1; i < branchLength; i++)
            {
                Block block = alternative.FindBlock(i, BlockTreeLookupOptions.RequireCanonical)!;
                blockTree.SuggestBlock(block);
                blocks.Add(block);
            }

            if (branchLength > blockTree.Head!.Number && blocks.Count > 0)
            {
                blockTree.TryUpdateMainChain(blocks[^1].Header, true, preloadedBlocks: CollectionsMarshal.AsSpan(blocks));
            }
        }

        /// <summary>
        /// Test-only: marks exactly the given blocks canonical without the connectivity walk that
        /// <see cref="IBlockTree.TryUpdateMainChain"/> performs, so tests can stage disconnected fast-sync heads,
        /// beacon blocks above a stale head, or inconsistent level markers.
        /// </summary>
        public static void ForceMainChainForTest(this IBlockTree blockTree, IReadOnlyList<Block> blocks, bool wereProcessed = true, bool forceUpdateHeadBlock = false) =>
            ((BlockTree)blockTree).MarkBlocksCanonicalForTest(blocks, wereProcessed, forceUpdateHeadBlock);

        public static Task WaitForNewBlock(this IBlockTree blockTree, CancellationToken cancellation) => Wait.ForEventCondition<BlockReplacementEventArgs>(
                cancellation,
                (h) => blockTree.BlockAddedToMain += h,
                (h) => blockTree.BlockAddedToMain -= h,
                (e) => true);
    }
}
