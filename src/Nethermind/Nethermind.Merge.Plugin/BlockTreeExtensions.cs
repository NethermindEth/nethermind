// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public static class BlockTreeExtensions
{
    public const int AncestorReorgDepthLimit = 32;

    public static bool IsOnMainChainBehindOrEqualHead(this IBlockTree blockTree, Block block) =>
        block.Number <= (blockTree.Head?.Number ?? 0) && blockTree.IsMainChain(block.Header);

    public static bool IsAncestorOnMainChainBeyondReorgDepthLimit(this IBlockTree blockTree, Block block) =>
        (blockTree.Head?.Number ?? 0) - block.Number > AncestorReorgDepthLimit && blockTree.IsMainChain(block.Header);
}
