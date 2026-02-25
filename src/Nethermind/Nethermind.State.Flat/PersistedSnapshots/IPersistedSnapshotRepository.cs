// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.State.Flat.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

public interface IPersistedSnapshotRepository : IDisposable
{
    int SnapshotCount { get; }
    long BaseSnapshotMemory { get; }
    long CompactedSnapshotMemory { get; }
    void LoadFromCatalog();

    // Two-layer storage
    void ConvertSnapshotToPersistedSnapshot(Snapshot snapshot, bool isPersistable = false);
    void AddCompactedSnapshot(StateId from, StateId to, ArenaReservation reservation, int actualSize, HashSet<int> referencedSnapshotIds, bool isPersistable);

    // Compaction assembly (mirrors SnapshotRepository.AssembleSnapshotsUntil)
    PersistedSnapshotList AssembleSnapshotsForCompaction(StateId toStateId, long minBlockNumber);

    // Lookup
    PersistedSnapshot? TryGetSnapshotFrom(StateId fromState);
    bool TryLeaseSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot);
    bool TryLeaseCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot);
    bool TryLeasePersistableCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot);

    // Lifecycle
    int PruneBefore(StateId stateId);
    bool HasBaseSnapshot(in StateId stateId);
}
