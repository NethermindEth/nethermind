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

        ulong bestHeader = (ulong)(blockTree.BestSuggestedHeader?.Number ?? 0);
        return stateId.BlockNumber <= bestHeader ? stateId.BlockNumber : bestHeader;
    }
}
