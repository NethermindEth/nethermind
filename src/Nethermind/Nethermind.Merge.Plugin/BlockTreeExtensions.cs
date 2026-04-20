// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public static class BlockTreeExtensions
{
    public const int AncestorReorgDepthLimit = 32;

    public static bool IsOnMainChainBehindOrEqualHead(this IBlockTree blockTree, BlockHeader header) =>
        header.Number <= (blockTree.Head?.Number ?? 0) && blockTree.IsMainChain(header);

    public static bool IsOnMainChainBehindHead(this IBlockTree blockTree, BlockHeader header) =>
        (blockTree.Head?.Number ?? 0) - header.Number > AncestorReorgDepthLimit && blockTree.IsMainChain(header);
}
