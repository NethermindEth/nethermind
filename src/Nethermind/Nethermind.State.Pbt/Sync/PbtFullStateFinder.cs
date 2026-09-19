// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.State.Pbt.Sync;

public class PbtFullStateFinder(PbtPersistenceCoordinator coordinator) : IFullStateFinder
{
    public ulong FindBestFullState()
    {
        StateId stateId = coordinator.GetCurrentPersistedStateId();
        return stateId == StateId.PreGenesis ? 0UL : stateId.BlockNumber;
    }
}
