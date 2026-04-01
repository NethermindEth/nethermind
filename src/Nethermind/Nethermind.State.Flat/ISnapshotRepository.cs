// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

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

    /// <summary>
    /// Returns true if any non-compacted snapshot exists with <c>To.BlockNumber == blockNumber</c>.
    /// Used to detect fork changes: if a snapshot exists at this height but with a different
    /// state root than the one being added, it indicates a fork switch.
    /// Does not check compacted snapshots.
    /// </summary>
    bool HasStatesAtBlockNumber(long blockNumber);
    SnapshotPooledList AssembleSnapshots(in StateId stateId, in StateId targetStateId, int estimatedSize);
    SnapshotPooledList AssembleSnapshotsUntil(in StateId stateId, long minBlockNumber, int estimatedSize);
    StateId? GetLastSnapshotId();
    ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber);
    void RemoveStatesUntil(in StateId currentPersistedStateId);

    /// <summary>
    /// Returns true if any compacted snapshot exists at or above the given block number
    /// (i.e. <c>To.BlockNumber &gt;= blockNumber</c>). Used to guard fork-pruning:
    /// removing sorted-set entries without the corresponding compacted snapshots would
    /// orphan them, so fork-pruning is skipped when compacted snapshots exist in this range.
    /// </summary>
    bool HasCompactedStateAtOrAbove(long blockNumber);

    /// <summary>
    /// Remove all non-compacted snapshots at or above the given block number.
    /// Used when a new block replaces existing state at the same height (e.g. different payload
    /// at the same block number), invalidating snapshots from that point onward.
    /// Compacted snapshots are left intact as they span block ranges and are cleaned up
    /// by <see cref="RemoveStatesUntil"/> when persistence advances.
    /// </summary>
    void RemoveStatesFrom(long blockNumber);
}
