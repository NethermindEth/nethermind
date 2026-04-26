// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public static class BlockTreeExtensions
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="header"/> belongs to the canonical chain
    /// and is at or behind the current head — i.e. an unprocessed FCU to it can be safely
    /// answered <c>VALID</c> without reorging.
    /// </summary>
    /// <param name="blockTree">The block tree.</param>
    /// <param name="header">Header to test for canonical-ancestor membership.</param>
    /// <returns><c>true</c> if <paramref name="header"/> is on the main chain and its
    /// number does not exceed the current head's number; otherwise <c>false</c>.</returns>
    public static bool IsOnMainChainBehindOrEqualHead(this IBlockTree blockTree, BlockHeader header) =>
        header.Number <= (blockTree.Head?.Number ?? 0) && blockTree.IsMainChain(header);
}
