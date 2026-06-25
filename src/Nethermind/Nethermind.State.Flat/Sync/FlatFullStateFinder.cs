// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.State.Flat.Sync;

public class FlatFullStateFinder(PersistenceManager persistenceManager) : IFullStateFinder
{
    public ulong FindBestFullState()
    {
        StateId stateId = persistenceManager.GetCurrentPersistedStateId();
        return stateId == StateId.PreGenesis ? 0UL : stateId.BlockNumber;
    }
}
