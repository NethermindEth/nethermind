// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public static class BlockTreeExtensions
{
    public static bool IsOnMainChainBehindOrEqualHead(this IBlockTree blockTree, BlockHeader header) =>
        header.Number <= (blockTree.Head?.Number ?? 0) && blockTree.IsMainChain(header);

    public static bool IsOnMainChainBehindFinalized(this IBlockTree blockTree, BlockHeader header) =>
        header.Number < (blockTree.FindFinalizedHeader()?.Number ?? 0) && blockTree.IsMainChain(header);

}
