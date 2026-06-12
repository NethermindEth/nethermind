// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public interface IPersistenceManager
{
    IPersistence.IPersistenceReader LeaseReader();
    StateId GetCurrentPersistedStateId();
    void AddToPersistence(StateId latestSnapshot);

    /// <summary>
    /// Raised under the persistence lock right after a snapshot's write batch is committed,
    /// while the snapshot still lives in the in-memory layer. Consumers invalidate
    /// persistence-read caches from its write-set.
    /// </summary>
    event Action<Snapshot>? SnapshotPersisted;
    StateId FlushToPersistence();
    void ResetPersistedStateId();
}
