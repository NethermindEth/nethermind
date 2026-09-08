// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// A processed block can lose its state again: flat state bounding drops orphaned siblings under a head that
/// never advances. Such a block is recovered by re-executing the branch from the nearest ancestor that still has
/// state.
/// </summary>
internal static class PrunedStateRecovery
{
    /// <summary>Mirrors the few-blocks-to-process window of newPayload: re-execution starts at most this many blocks down.</summary>
    public const int MaxReExecutionDepth = 8;

    // Match the processing branch's search bound without scheduling that many blocks at once.
    private const int MaxAncestorSearchDepth = 8192;

    /// <summary>Finds the first bounded replay segment of a stored fork with an available state ancestor.</summary>
    public static BlockHeader? FindRecoveryHead(IBlockTree blockTree, IStateReader stateReader, BlockHeader header)
    {
        BlockHeader[] recent = new BlockHeader[MaxReExecutionDepth];
        recent[0] = header;
        BlockHeader? ancestor = header;
        for (int depth = 1; depth <= MaxAncestorSearchDepth && !ancestor.IsGenesis; depth++)
        {
            ancestor = blockTree.FindHeader(ancestor.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (ancestor is null) return null;
            if (stateReader.HasStateForBlock(ancestor)) return recent[depth <= MaxReExecutionDepth ? 0 : depth % MaxReExecutionDepth];
            recent[depth % MaxReExecutionDepth] = ancestor;
        }

        return null;
    }

    /// <summary>Whether an ancestor of <paramref name="header"/> within <see cref="MaxReExecutionDepth"/> still has state.</summary>
    public static bool HasAncestorWithState(IBlockTree blockTree, IStateReader stateReader, BlockHeader header)
    {
        BlockHeader? ancestor = header;
        for (int depth = 0; depth < MaxReExecutionDepth && !ancestor.IsGenesis; depth++)
        {
            ancestor = blockTree.FindHeader(ancestor.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (ancestor is null) return false;
            if (stateReader.HasStateForBlock(ancestor)) return true;
        }

        return false;
    }
}
