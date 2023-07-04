// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public static class BlockTreeExtensions
{
    public static bool IsOnMainChainBehindOrEqualHead(this IBlockTree blockTree, Block block) =>
        block.Number <= (blockTree.Head?.Number ?? 0) && blockTree.IsMainChain(block.Header);

    public static bool IsOnMainChainBehindHead(this IBlockTree blockTree, Block block) =>
        block.Number < (blockTree.Head?.Number ?? 0) && blockTree.IsMainChain(block.Header);
}
