// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public static class BlockTreeExtensions
    {
        public static ReadOnlyBlockTree AsReadOnly(this IBlockTree blockTree) => new(blockTree);

        public static BlockHeader? GetProducedBlockParent(this IBlockTree blockTree, BlockHeader? parentHeader) => parentHeader ?? blockTree.Head?.Header;

        public static (bool isSyncing, long headNumber, long bestSuggested) IsSyncing(this IBlockTree blockTree, int maxDistanceForSynced = 0)
        {
            long bestSuggestedNumber = (long)(blockTree.FindBestSuggestedHeader()?.Number ?? 0UL);
            long headNumberOrZero = (long)(blockTree.Head?.Number ?? 0UL);
            bool isSyncing = bestSuggestedNumber == 0 || bestSuggestedNumber > headNumberOrZero + maxDistanceForSynced;

            return (isSyncing, headNumberOrZero, bestSuggestedNumber);
        }
    }
}
