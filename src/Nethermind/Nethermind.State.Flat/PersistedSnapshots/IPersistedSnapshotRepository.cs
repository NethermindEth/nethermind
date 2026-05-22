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

    // Two-layer storage. Returned PersistedSnapshot is pre-leased — the caller owns the
    // lease and MUST dispose it (the repository's own dict entry holds an independent
    // lease, so disposing the returned reference does not remove the snapshot from the
    // repo). Pre-leasing closes a use-after-free window between return and use when a
    // concurrent PruneBefore may dispose the repo's dict entry.
    PersistedSnapshot ConvertSnapshotToPersistedSnapshot(Snapshot snapshot);
    PersistedSnapshot AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, BloomFilter bloom, bool isPersistable = false);

    // Compaction assembly (mirrors SnapshotRepository.AssembleSnapshotsUntil)
    PersistedSnapshotList AssembleSnapshotsForCompaction(StateId toStateId, long minBlockNumber);

    /// <summary>
    /// Lease every base snapshot tiling <c>(from, to]</c> — used to bulk-prefetch their blob
    /// RLP regions before a linked persistable is persisted. Caller disposes the list.
    /// </summary>
    PersistedSnapshotList LeaseBaseSnapshotsInRange(StateId from, StateId to);

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
    bool TryLeasePersistableCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot);

    // Lifecycle
    int PruneBefore(StateId stateId);
    bool HasBaseSnapshot(in StateId stateId);
}
