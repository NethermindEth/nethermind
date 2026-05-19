// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

public interface IPersistedSnapshotRepository : IDisposable
{
    int SnapshotCount { get; }
    long BaseSnapshotMemory { get; }
    long CompactedSnapshotMemory { get; }

    /// <summary>
    /// Most-recently-registered <see cref="StateId"/> tracked under this repository's
    /// catalog lock. Used as a self-seed for backward walks
    /// (see <see cref="TryGetSnapshotFrom(StateId)"/>).
    /// </summary>
    StateId? LastRegisteredState { get; }

    void LoadFromCatalog();

    // Two-layer storage
    void ConvertSnapshotToPersistedSnapshot(Snapshot snapshot);
    PersistedSnapshot AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, BloomFilter bloom);

    // Compaction assembly (mirrors SnapshotRepository.AssembleSnapshotsUntil)
    PersistedSnapshotList AssembleSnapshotsForCompaction(StateId toStateId, long minBlockNumber);

    // Lookup
    PersistedSnapshot? TryGetSnapshotFrom(StateId fromState, StateId seedState);

    /// <summary>
    /// Self-seeded variant of <see cref="TryGetSnapshotFrom(StateId, StateId)"/> — uses
    /// this repository's <see cref="LastRegisteredState"/> as the seed. Returns <c>null</c>
    /// when no snapshot is registered yet.
    /// </summary>
    PersistedSnapshot? TryGetSnapshotFrom(StateId fromState);
    bool TryLeaseSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot);
    bool TryLeaseCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot);

    // Lifecycle
    int PruneBefore(StateId stateId);
    bool HasBaseSnapshot(in StateId stateId);
}
