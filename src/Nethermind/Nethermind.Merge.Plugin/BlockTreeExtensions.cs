// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public static class BlockTreeExtensions
{
    public static bool IsOnMainChainBehindOrEqualHead(this IBlockTree blockTree, BlockHeader header) =>
        header.Number <= (blockTree.Head?.Number ?? 0) && blockTree.IsMainChain(header);

    /// <summary>Whether <paramref name="header"/> is a canonical block below the latest known finalized block.</summary>
    /// <remarks>
    /// Only the finalized header's number is read, so it is looked up without the total difficulty, which can
    /// load its chain level. The number comparison runs first, so the chain-level load behind <c>IsMainChain</c>
    /// only happens for a block below finalized.
    /// </remarks>
    public static bool IsOnMainChainBehindFinalized(this IBlockTree blockTree, BlockHeader header) =>
        header.Number < FinalizedNumber(blockTree) && blockTree.IsMainChain(header);

    private static ulong FinalizedNumber(IBlockTree blockTree) =>
        blockTree.FinalizedHash is { } finalizedHash
            ? blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)?.Number ?? 0
            : 0;
}
