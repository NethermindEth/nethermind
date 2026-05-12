// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain;

/// <summary>
/// Detects and clears state-availability metadata that no longer matches the on-disk state.
/// Runs at world-state-manager activation; if a user wipes the state DB without touching
/// MetadataDb/BlockInfoDb the recorded floors would otherwise misreport availability
/// (e.g. <c>eth_capabilities</c>) until sync rewrites them.
/// </summary>
public static class StateMetadataValidator
{
    public static void DiscardStaleFloors(IWorldStateManager worldStateManager, IBlockTree blockTree, ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger(typeof(StateMetadataValidator));

        if (worldStateManager.OldestStateBlock is { } oldestStateBlock
            && IsStateMissing(oldestStateBlock, worldStateManager, blockTree))
        {
            if (logger.IsInfo) logger.Info($"State at OldestStateBlock={oldestStateBlock} not present on disk; clearing stale floor.");
            worldStateManager.OldestStateBlock = null;
        }

        if (blockTree.BestPersistedState is { } bestPersisted
            && IsStateMissing(bestPersisted, worldStateManager, blockTree))
        {
            if (logger.IsInfo) logger.Info($"State at BestPersistedState={bestPersisted} not present on disk; clearing stale marker.");
            blockTree.BestPersistedState = null;
        }
    }

    private static bool IsStateMissing(long blockNumber, IWorldStateManager worldStateManager, IBlockTree blockTree)
    {
        BlockHeader? header = blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        // No header: can't verify. Not a deadlock — FullPruner aborts its cycle on null header,
        // and the next sync writer overwrites the marker.
        if (header is null) return false;
        return !worldStateManager.GlobalStateReader.HasStateForBlock(header);
    }
}
