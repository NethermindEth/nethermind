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

    bool TryFindAncestorStateAtBlock(in StateId head, long blockNumber, out StateId ancestor);
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
}
