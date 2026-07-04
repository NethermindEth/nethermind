// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

public class FlatPersistedStateSource(IPersistenceManager persistenceManager) : IPersistedStateSource
{
    // Queried on every suggested block; cache the materialized root so the per-block path allocates
    // only when the persisted state advances (once per compaction batch).
    private CachedRoot? _cached;

    public bool TryGetPersistedState(out ulong blockNumber, [NotNullWhen(true)] out Hash256? stateRoot)
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted == StateId.PreGenesis || persisted == StateId.Sync)
        {
            blockNumber = 0;
            stateRoot = null;
            return false;
        }

        CachedRoot? cached = Volatile.Read(ref _cached);
        if (cached is null || cached.Id != persisted)
        {
            cached = new CachedRoot(persisted, persisted.StateRoot.ToCommitment());
            Volatile.Write(ref _cached, cached);
        }

        blockNumber = persisted.BlockNumber;
        stateRoot = cached.Root;
        return true;
    }

    private sealed record CachedRoot(StateId Id, Hash256 Root);
}
