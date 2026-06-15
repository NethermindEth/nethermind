// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat;

public interface ISnapshotRepository
{
    int SnapshotCount { get; }

    /// <summary>Total persisted snapshots across the base/compacted/persistable buckets.</summary>
    int PersistedSnapshotCount { get; }

    void AddStateId(in StateId stateId);
    StateId? LastRegisteredState { get; }

    /// <summary>Add an in-memory snapshot to the <paramref name="tier"/> store. <paramref name="tier"/>
    /// must be <see cref="SnapshotTier.InMemoryBase"/> or <see cref="SnapshotTier.InMemoryCompacted"/>.</summary>
    bool TryAdd(Snapshot snapshot, SnapshotTier tier);

    /// <summary>Lease the in-memory snapshot at <paramref name="stateId"/> from the <paramref name="tier"/>
    /// store. <paramref name="tier"/> must be an <c>InMemory*</c> value.</summary>
    bool TryLeaseInMemoryState(in StateId stateId, SnapshotTier tier, [NotNullWhen(true)] out Snapshot? entry);

    /// <summary>Remove and release the in-memory snapshot at <paramref name="stateId"/> from the
    /// <paramref name="tier"/> store. <paramref name="tier"/> must be an <c>InMemory*</c> value.</summary>
    bool RemoveAndReleaseInMemoryKnownState(in StateId stateId, SnapshotTier tier);

    bool HasState(in StateId stateId);

    /// <summary>Index a caller-built <paramref name="snapshot"/> into the bucket selected by
    /// <paramref name="tier"/> (must be a <c>Persisted*</c> value), acquiring the bucket's own lease. The
    /// caller retains its construction lease and is responsible for the catalog entry — a freshly
    /// persisted/compacted snapshot writes one; a snapshot reloaded from the catalog does not.</summary>
    void AddPersistedSnapshot(PersistedSnapshot snapshot, SnapshotTier tier);

    /// <summary>Lease every persisted base snapshot tiling <c>(from, to]</c>. Caller disposes the list.</summary>
    PersistedSnapshotList LeaseBaseSnapshotsInRange(StateId from, StateId to);

    /// <summary>Whether the persisted base bucket holds a snapshot at <paramref name="stateId"/>.</summary>
    bool HasBaseSnapshot(in StateId stateId);

    /// <summary>Every loaded persisted snapshot across the three buckets, for one-off lifecycle iteration
    /// (bloom rebuild) at load time.</summary>
    IEnumerable<PersistedSnapshot> PersistedSnapshots { get; }

    /// <summary>Flag every persisted snapshot's files as shutdown-preserved so they survive process exit.
    /// Must run (across all buckets) before the repository is disposed — a file shared between a base and a
    /// compacted snapshot must be flagged before either snapshot is disposed. The implementation's
    /// <c>Dispose</c> (invoked by DI) then disposes the snapshots and clears the buckets.</summary>
    void MarkPersistedTierForShutdown();

    /// <summary>Prune persisted snapshots with <c>To.BlockNumber</c> before the given block number.</summary>
    void RemovePersistedStatesUntil(long blockNumber);
    AssembledSnapshotResult AssembleSnapshots(in StateId stateId, in StateId targetStateId, int estimatedSize);
    SnapshotPooledList AssembleInMemorySnapshotsForCompaction(in StateId toStateId, long minBlockNumber, int estimatedSize);

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
    PersistedSnapshotList AssemblePersistedSnapshotsForCompaction(in StateId toStateId, long minBlockNumber);
    StateId? GetLastSnapshotId();
    ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber);
    ArrayPoolList<StateId> GetStatesUpToBlock(long blockNumber);
    void RemoveStatesUntil(long blockNumber);

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
