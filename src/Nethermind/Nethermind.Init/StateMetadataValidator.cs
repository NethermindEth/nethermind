// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Init;

/// <summary>
/// Detects and clears state-availability metadata that no longer matches the on-disk state.
/// Runs at world-state-manager activation; if a user wipes the state DB without touching
/// MetadataDb/BlockInfoDb the recorded floors would otherwise misreport availability
/// (e.g. <c>eth_capabilities</c>) until sync rewrites them.
/// </summary>
internal static class StateMetadataValidator
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
        BlockHeader? header = blockTree.FindHeader(blockNumber);
        // Unknown header — can't verify, leave the marker alone (next writer will overwrite).
        if (header is null) return false;
        return !worldStateManager.GlobalStateReader.HasStateForBlock(header);
    }
}
