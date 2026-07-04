// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.State.Flat.Sync;

public class FlatFullStateFinder(PersistenceManager persistenceManager, IBlockTree blockTree) : IFullStateFinder
{
    public ulong FindBestFullState()
    {
        StateId stateId = persistenceManager.GetCurrentPersistedStateId();
        if (stateId == StateId.PreGenesis) return 0UL;

        // On an unclean shutdown the flat state flush can land up to a compaction batch ahead of the block
        // tree's flush. Reporting a state above the best-known header permanently trips the sync selector's
        // State > Header invariant, so clamp: processing then re-runs the gap blocks on top of the reorg
        // window (MaxReorgDepth covers a full batch) and the re-applied writes are idempotent.
        ulong bestHeader = (ulong)(blockTree.BestSuggestedHeader?.Number ?? 0);
        return stateId.BlockNumber <= bestHeader ? stateId.BlockNumber : bestHeader;
    }
}
