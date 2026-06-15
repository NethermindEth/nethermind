// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

public interface IPersistedSnapshotRepository : IDisposable
{
    int SnapshotCount { get; }

    // Two-layer storage. Returned PersistedSnapshot is pre-leased — the caller owns the
    // lease and MUST dispose it (the repository's own dict entry holds an independent
    // lease, so disposing the returned reference does not remove the snapshot from the
    // repo). Pre-leasing closes a use-after-free window between return and use when a
    // concurrent RemoveStatesUntil may dispose the repo's dict entry.
    PersistedSnapshot ConvertSnapshotToPersistedSnapshot(Snapshot snapshot);
    PersistedSnapshot AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, BloomFilter bloom, bool isPersistable = false);

    /// <summary>
    /// Lease every base snapshot tiling <c>(from, to]</c> — used to bulk-prefetch their blob
    /// RLP regions before a linked persistable is persisted. Caller disposes the list.
    /// </summary>
    PersistedSnapshotList LeaseBaseSnapshotsInRange(StateId from, StateId to);

    // Lookup
    bool TryLeaseSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot);
    bool TryLeaseCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot);
    bool TryLeasePersistableCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot);

    // Lifecycle
    void RemoveStatesUntil(long blockNumber);

    /// <summary>
    /// Enumerate persisted <c>To</c>-StateIds across all buckets whose <c>To.BlockNumber</c> is
    /// in <c>[startBlockInclusive, endBlockInclusive]</c>. Snapshot taken under the repository's
    /// catalog lock; caller disposes the returned pooled list.
    /// </summary>
    ArrayPoolList<StateId> GetPersistedStatesInRange(long startBlockInclusive, long endBlockInclusive);

    /// <summary>
    /// Remove the persisted snapshot(s) at exactly <paramref name="toState"/> from every bucket it
    /// appears in (base/compacted/persistable), releasing their leases. Returns <c>true</c> when
    /// anything was removed. Used by orphan-fork pruning to drop a single non-canonical state.
    /// </summary>
    bool RemovePersistedStateExact(in StateId toState);

    bool HasBaseSnapshot(in StateId stateId);
}
