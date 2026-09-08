// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// A processed block can lose its state again: flat state bounding drops orphaned siblings under a head that
/// never advances. Such a block is recovered by re-executing the branch from the nearest ancestor that still has
/// state, which the engine handlers only attempt within a short window so a node that is really behind syncs.
/// </summary>
internal static class PrunedStateRecovery
{
    /// <summary>Mirrors the few-blocks-to-process window of newPayload: re-execution starts at most this many blocks down.</summary>
    public const int MaxReExecutionDepth = 8;

    /// <summary>Whether an ancestor of <paramref name="header"/> within <see cref="MaxReExecutionDepth"/> still has state.</summary>
    public static bool HasAncestorWithState(IBlockTree blockTree, IStateReader stateReader, BlockHeader header)
    {
        BlockHeader? ancestor = header;
        for (int depth = 0; depth < MaxReExecutionDepth; depth++)
        {
            ancestor = blockTree.FindHeader(ancestor.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (ancestor is null) return false;
            if (stateReader.HasStateForBlock(ancestor)) return true;
        }

        return false;
    }
}
