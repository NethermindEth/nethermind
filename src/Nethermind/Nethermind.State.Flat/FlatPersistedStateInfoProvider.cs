// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

/// <summary>
/// Lightweight startup/recovery seam for exact persisted flat-state identity.
/// This intentionally avoids the full <see cref="IFlatDbManager"/> graph so startup
/// diagnostics do not create a dependency cycle through persistence finalization.
/// </summary>
public class FlatPersistedStateInfoProvider(
    IPersistence persistence,
    ISnapshotRepository snapshotRepository) : IPersistedStateInfoProvider
{
    public bool TryGetPersistedStateInfo(out PersistedStateInfo persistedStateInfo)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        StateId currentState = reader.CurrentState;
        if (currentState == StateId.PreGenesis)
        {
            persistedStateInfo = default;
            return false;
        }

        persistedStateInfo = new PersistedStateInfo(currentState.BlockNumber, currentState.StateRoot);
        return true;
    }

    public bool HasRecoverableStateForBlock(BlockHeader? blockHeader)
    {
        StateId stateId = new(blockHeader);
        if (snapshotRepository.HasState(stateId))
        {
            return true;
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        return reader.CurrentState == stateId;
    }
}
