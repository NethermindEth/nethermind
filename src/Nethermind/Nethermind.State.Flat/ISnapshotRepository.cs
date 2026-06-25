// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Persistence.BloomFilter;

namespace Nethermind.State.Flat;

public interface ISnapshotRepository
{
    /// <summary>Number of in-memory base snapshots currently held.</summary>
    int SnapshotCount { get; }

    /// <summary>Total persisted snapshots across the base/compacted/CompactSized buckets.</summary>
    int PersistedSnapshotCount { get; }

    /// <summary>Register <paramref name="stateId"/> as a known in-memory tip: adds it to the block-ordered
    /// set and records it as the last-registered tip.</summary>
    void AddStateId(in StateId stateId);

    /// <summary>Add an in-memory snapshot to the <paramref name="tier"/> store. <paramref name="tier"/>
    /// must be <see cref="SnapshotTier.InMemoryBase"/> or <see cref="SnapshotTier.InMemoryCompacted"/>.</summary>
    bool TryAdd(Snapshot snapshot, SnapshotTier tier);

    /// <summary>Lease the in-memory snapshot at <paramref name="stateId"/> from the <paramref name="tier"/>
    /// store. <paramref name="tier"/> must be an <c>InMemory*</c> value.</summary>
    bool TryLeaseInMemoryState(in StateId stateId, SnapshotTier tier, [NotNullWhen(true)] out Snapshot? entry);

    /// <summary>Remove and release the in-memory snapshot at <paramref name="stateId"/> from the
    /// <paramref name="tier"/> store. <paramref name="tier"/> must be an <c>InMemory*</c> value.</summary>
    bool RemoveAndReleaseInMemoryKnownState(in StateId stateId, SnapshotTier tier);

    /// <summary>Whether a snapshot exists at <paramref name="stateId"/> in either the in-memory base store
    /// or the persisted base bucket.</summary>
    bool HasState(in StateId stateId);

    /// <summary>Index a caller-built <paramref name="snapshot"/> into the bucket selected by
    /// <paramref name="tier"/> (must be a <c>Persisted*</c> value), acquiring the bucket's own lease. The
    /// caller retains its construction lease and is responsible for the catalog entry — a freshly
    /// persisted/compacted snapshot writes one; a snapshot reloaded from the catalog does not.</summary>
    void AddPersistedSnapshot(PersistedSnapshot snapshot, SnapshotTier tier);

    /// <summary>Atomically swap the snapshot registered at <paramref name="to"/> in <paramref name="tier"/>'s
    /// bucket for <paramref name="replacement"/>, which must wrap the same on-disk reservation. The previous
    /// entry's bucket lease is released so its <c>CleanUp</c> runs once any in-flight reader drains. Returns
    /// <c>false</c> (leaving <paramref name="replacement"/> unregistered) when no entry is present.</summary>
    bool ReplacePersistedSnapshot(in StateId to, PersistedSnapshot replacement, SnapshotTier tier);

    /// <summary>Adopt <paramref name="sharedBloom"/> (a correct superset pre-filter) across every persisted
    /// snapshot fully contained in <c>(from, to]</c>, freeing each one's own bloom. Walks the base parent
    /// chain from <paramref name="to"/> back to <paramref name="from"/>; at each block re-registers a twin
    /// over the same reservation carrying a lease on the shared bloom. Best-effort and lock-free across
    /// buckets — a racing prune just leaves a snapshot with its own bloom. Pure live-memory optimization:
    /// blooms are not persisted, so reload rebuilds independent blooms.</summary>
    void ShareBloomAcrossRange(StateId from, StateId to, RefCountedBloomFilter sharedBloom, BlobArenaManager blobs);

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
    void RemovePersistedStatesUntil(ulong blockNumber);
    /// <summary>Assemble the backward chain from <paramref name="stateId"/> down to
    /// <paramref name="targetStateId"/> across both tiers, returning the in-memory and persisted snapshots
    /// along the winning path (oldest-first). Empty when no path reaches the target; caller disposes the result.</summary>
    AssembledSnapshotResult AssembleSnapshots(in StateId stateId, in StateId targetStateId, int estimatedSize);

    /// <summary>Assemble the backward chain of in-memory snapshots from <paramref name="toStateId"/> down to
    /// <paramref name="minBlockNumber"/> for compaction (widest in-memory edge first). Oldest-first; empty when
    /// the terminus is unreachable. Caller disposes the list.</summary>
    SnapshotPooledList AssembleInMemorySnapshotsForCompaction(in StateId toStateId, ulong minBlockNumber, int estimatedSize);

    /// <summary>
    /// Backward BFS from <paramref name="seed"/> over the two-tier snapshot graph for the first
    /// snapshot whose <c>From</c> equals <paramref name="currentPersistedState"/> — the next thing
    /// to persist. Returns the leased persisted or in-memory snapshot (caller disposes), or
    /// <c>(null, null)</c> when none is reachable.
    /// </summary>
    (PersistedSnapshot? Persisted, Snapshot? InMemory) FindSnapshotToPersist(in StateId seed, in StateId currentPersistedState, ulong compactSize);

    /// <summary>
    /// Assemble the backward chain of persisted snapshots for compaction from <paramref name="toStateId"/>
    /// down to <paramref name="minBlockNumber"/> (widest persisted edge first). Oldest-first; empty when
    /// fewer than two are found. Caller disposes the returned list.
    /// </summary>
    PersistedSnapshotList AssemblePersistedSnapshotsForCompaction(in StateId toStateId, ulong minBlockNumber);
    /// <summary>The greatest known <see cref="StateId"/> across the in-memory ordered set and the
    /// persisted-tier maxima (the true cross-tier tip). <c>null</c> when empty.</summary>
    StateId? GetLastSnapshotId();

    /// <summary>
    /// Records <paramref name="stateId"/> as the most recently committed state (the block the main
    /// processing scope just committed).
    /// </summary>
    /// <remarks>
    /// Always overwrites the previous value with no monotonic guard: a reorg legitimately moves the head
    /// to a same- or lower-numbered state with a different root. Unlike <see cref="GetLastSnapshotId"/>
    /// (the longest in-memory chain) this follows the canonical head, so a forced persist does not start
    /// its ancestor walk from a longer non-canonical fork.
    /// </remarks>
    void SetLastCommittedStateId(in StateId stateId);

    /// <summary>Returns the most recently committed state, or <c>null</c> if nothing was committed this session.</summary>
    StateId? GetLastCommittedStateId();

    /// <summary>All registered in-memory state ids at <paramref name="blockNumber"/> (a fork can have
    /// several). Caller disposes the list.</summary>
    ArrayPoolList<StateId> GetStatesAtBlockNumber(ulong blockNumber);

    /// <summary>All registered in-memory state ids with <c>BlockNumber</c> up to and including
    /// <paramref name="blockNumber"/>. Caller disposes the list.</summary>
    ArrayPoolList<StateId> GetStatesUpToBlock(ulong blockNumber);

    /// <summary>Remove every snapshot a persist to <paramref name="blockNumber"/> supersedes: in-memory
    /// snapshots (both tiers) with <c>To.BlockNumber</c> up to and including <paramref name="blockNumber"/>,
    /// and persisted-tier snapshots with <c>To.BlockNumber</c> strictly below it (the base at the persisted
    /// block stays until the state advances past it). Folds in <see cref="RemovePersistedStatesUntil"/>.</summary>
    void RemoveStatesUntil(ulong blockNumber);

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
