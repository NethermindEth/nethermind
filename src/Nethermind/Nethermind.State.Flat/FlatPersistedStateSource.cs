// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.State.Flat;

public class FlatPersistedStateSource(IPersistenceManager persistenceManager) : IPersistedStateSource
{
    public bool TryGetPersistedState(out ulong blockNumber, out Hash256 stateRoot)
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted == StateId.PreGenesis || persisted == StateId.Sync)
        {
            blockNumber = 0;
            stateRoot = Keccak.EmptyTreeHash;
            return false;
        }

        blockNumber = persisted.BlockNumber;
        stateRoot = persisted.StateRoot.ToCommitment();
        return true;
    }
}
