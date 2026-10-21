// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain.Visitors;

/// <summary>
/// Moves the head over the processed canonical blocks above it whose state survived the restart, so they are not
/// re-executed. A flat DB keeps the state of its in-memory tail on a clean shutdown while its persisted state, where
/// the head is loaded from, stays at the finality-driven boundary.
/// </summary>
/// <remarks>
/// Runs before <see cref="StartupBlockTreeFixer"/>, which then suggests only the blocks above the new head. Stops at
/// the first canonical block that was not processed or has no state, so the head moves over an unbroken run. If the
/// head cannot be moved nothing changes and every block is re-executed as before.
/// <para>It does not depend on the state backend: a backend that keeps no state above the persisted head, such as the
/// trie, or the flat DB without long finality, leaves it nothing to do.</para>
/// </remarks>
public static class KeptStateResumer
{
    /// <summary>Resumes retained canonical state before startup block replay begins.</summary>
    /// <returns>The number of blocks the head moved over.</returns>
    public static ulong Resume(IBlockTree blockTree, IStateReader stateReader, ILogManager logManager)
    {
        if (blockTree.Head?.Header is not { } head) return 0;

        // Oldest-first persistence and this contiguous walk keep every retained snapshot reachable from the disk state.
        BlockHeader? last = null;
        for (ulong number = head.Number + 1; number <= blockTree.BestKnownNumber; number++)
        {
            BlockHeader? header = blockTree.FindHeader(number, BlockTreeLookupOptions.RequireCanonical);
            if (header?.Hash is null || header.StateRoot is null || !blockTree.WasProcessed(number, header.Hash)
                || !stateReader.HasStateForBlock(header))
                break;
            last = header;
        }

        if (last is null) return 0;

        ILogger logger = logManager.GetClassLogger(typeof(KeptStateResumer));
        ulong moved = last.Number - head.Number;
        if (blockTree.TryUpdateMainChain(last, wereProcessed: true) && blockTree.Head?.Hash == last.Hash)
        {
            if (logger.IsInfo) logger.Info($"Resumed from {last.ToString(BlockHeader.Format.Short)}: the state of the {moved} blocks above the persisted state was kept, so they are not re-executed.");
            return moved;
        }

        if (logger.IsWarn) logger.Warn($"Could not move the head to {last.ToString(BlockHeader.Format.Short)}, whose state was kept; the {moved} blocks up to it are re-executed.");
        return 0;
    }
}
