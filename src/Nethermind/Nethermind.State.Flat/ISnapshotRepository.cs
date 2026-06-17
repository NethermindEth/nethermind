// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat;

public interface ISnapshotRepository
{
    int SnapshotCount { get; }
    int CompactedSnapshotCount { get; }

    void AddStateId(in StateId stateId);
    bool TryAddSnapshot(Snapshot snapshot);
    bool TryAddCompactedSnapshot(Snapshot snapshot);
    bool TryLeaseState(in StateId stateId, [NotNullWhen(true)] out Snapshot? entry);
    bool TryLeaseCompactedState(in StateId stateId, [NotNullWhen(true)] out Snapshot? entry);
    bool RemoveAndReleaseCompactedKnownState(in StateId stateId);
    bool HasState(in StateId stateId);
    SnapshotPooledList AssembleSnapshots(in StateId stateId, in StateId targetStateId, int estimatedSize);
    SnapshotPooledList AssembleSnapshotsUntil(in StateId stateId, long minBlockNumber, int estimatedSize);
    StateId? GetLastSnapshotId();
    ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber);
    void RemoveStatesUntil(in StateId currentPersistedStateId);

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

    /// <summary>
    /// Registers the reverse diff of a persisted chunk, keyed by its older end (<see cref="Snapshot.To"/>),
    /// so historical reads can walk the chain from a chunk boundary up to the persisted state.
    /// </summary>
    bool TryAddReverseDiff(Snapshot reverseDiff);

    /// <summary>
    /// True when <paramref name="stateId"/> is kept as a historical snapshot below the persisted state.
    /// </summary>
    bool HasHistoricalState(in StateId stateId);

    /// <summary>
    /// Early-persist replacement for <see cref="RemoveStatesUntil"/>: moves the canonical per-block
    /// chain at or below <paramref name="persistedStateId"/> into the historical set (releasing the
    /// persisted state's own snapshot, compacted snapshots, and non-canonical leftovers) so it stays
    /// available for snap serving.
    /// </summary>
    void ArchiveStatesUntil(in StateId persistedStateId);

    /// <summary>
    /// Assembles the snapshot stack for a historical state below the persisted state: per-block forward
    /// snapshots down to the nearest chunk boundary, then reverse diffs up to <paramref name="persistedState"/>.
    /// Returns an empty list when the chain is broken or the state is outside the serving window.
    /// </summary>
    SnapshotPooledList AssembleHistoricalSnapshots(in StateId baseBlock, in StateId persistedState, int estimatedSize);

    /// <summary>
    /// Releases reverse diffs and historical snapshots no longer needed to serve states at or above
    /// <paramref name="oldestServedBlockNumber"/>, keeping the reverse chain connected from
    /// <paramref name="persistedState"/> down to the chunk boundary at or below it.
    /// </summary>
    void PruneHistory(long oldestServedBlockNumber, in StateId persistedState);

    /// <summary>
    /// Releases all reverse diffs and historical snapshots, collapsing the historical serving window.
    /// Used when a chunk cannot be reversed (irreversible self-destruct).
    /// </summary>
    void ClearHistory();
}
