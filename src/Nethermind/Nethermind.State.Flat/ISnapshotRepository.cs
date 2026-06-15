// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat;

public interface ISnapshotRepository : IDisposable
{
    int SnapshotCount { get; }

    /// <summary>Total persisted snapshots across the base/compacted/persistable buckets.</summary>
    int PersistedSnapshotCount { get; }

    void AddStateId(in StateId stateId);
    StateId? LastRegisteredState { get; }
    bool TryAddSnapshot(Snapshot snapshot);
    bool TryAddCompactedSnapshot(Snapshot snapshot);
    bool TryLeaseState(in StateId stateId, [NotNullWhen(true)] out Snapshot? entry);
    bool TryLeaseCompactedState(in StateId stateId, [NotNullWhen(true)] out Snapshot? entry);
    bool RemoveAndReleaseCompactedKnownState(in StateId stateId);
    bool HasState(in StateId stateId);

    /// <summary>Persist an in-memory snapshot as a base entry in the persisted tier. The returned
    /// snapshot is pre-leased — the caller owns the lease and MUST dispose it.</summary>
    PersistedSnapshot ConvertSnapshotToPersistedSnapshot(Snapshot snapshot);

    /// <summary>Store a compacted (or, when <paramref name="isPersistable"/>, the CompactSize-wide
    /// persistable) snapshot with a pre-computed location/reservation. Returns it pre-leased.</summary>
    PersistedSnapshot AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, BloomFilter bloom, bool isPersistable = false);

    /// <summary>Lease every persisted base snapshot tiling <c>(from, to]</c>. Caller disposes the list.</summary>
    PersistedSnapshotList LeaseBaseSnapshotsInRange(StateId from, StateId to);

    /// <summary>Whether the persisted base bucket holds a snapshot at <paramref name="stateId"/>.</summary>
    bool HasBaseSnapshot(in StateId stateId);

    /// <summary>Prune persisted snapshots with <c>To.BlockNumber</c> before the given block number.</summary>
    void RemovePersistedStatesUntil(long blockNumber);
    AssembledSnapshotResult AssembleSnapshots(in StateId stateId, in StateId targetStateId, int estimatedSize);
    SnapshotPooledList AssembleSnapshotsUntil(in StateId stateId, long minBlockNumber, int estimatedSize);

    /// <summary>
    /// Backward BFS from <paramref name="seed"/> over the two-tier snapshot graph for the first
    /// snapshot whose <c>From</c> equals <paramref name="currentPersistedState"/> — the next thing
    /// to persist. Returns the leased persisted or in-memory snapshot (caller disposes), or
    /// <c>(null, null)</c> when none is reachable.
    /// </summary>
    (PersistedSnapshot? Persisted, Snapshot? InMemory) FindSnapshotToPersist(in StateId seed, in StateId currentPersistedState, int compactSize);

    /// <summary>
    /// Assemble the backward chain of persisted snapshots for compaction from <paramref name="toStateId"/>
    /// down to <paramref name="minBlockNumber"/> (widest persisted edge first). Oldest-first; empty when
    /// fewer than two are found. Caller disposes the returned list.
    /// </summary>
    PersistedSnapshotList AssembleSnapshotsForCompaction(in StateId toStateId, long minBlockNumber);
    StateId? GetLastSnapshotId();
    ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber);
    ArrayPoolList<StateId> GetStatesUpToBlock(long blockNumber);
    void RemoveStatesUntil(long blockNumber);
    void RemoveAndReleaseKnownState(in StateId stateId);

    /// <summary>
    /// Removes in-memory snapshots belonging to non-canonical forks that persisting
    /// <paramref name="canonicalStateId"/> orphans.
    /// </summary>
    /// <remarks>
    /// After a reorg a non-canonical fork can have descendants above the block being persisted.
    /// Once the fork's parent at the persisted block is dropped those descendants become
    /// unreachable yet still satisfy <see cref="HasState"/>. This must be called before the
    /// persist commits so no reader observes an advanced persisted state alongside such orphans.
    /// </remarks>
    /// <param name="canonicalStateId">The canonical state being persisted.</param>
    void RemoveSiblingAndDescendents(in StateId canonicalStateId);
}
