// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat;

public interface ISnapshotRepository
{
    int SnapshotCount { get; }

    void AddStateId(in StateId stateId);
    StateId? LastRegisteredState { get; }
    bool TryAddSnapshot(Snapshot snapshot);
    bool TryAddCompactedSnapshot(Snapshot snapshot);
    bool TryLeaseState(in StateId stateId, [NotNullWhen(true)] out Snapshot? entry);
    bool TryLeaseCompactedState(in StateId stateId, [NotNullWhen(true)] out Snapshot? entry);
    bool RemoveAndReleaseCompactedKnownState(in StateId stateId);
    bool HasState(in StateId stateId);
    AssembledSnapshotResult AssembleSnapshots(in StateId stateId, in StateId targetStateId, int estimatedSize);
    SnapshotPooledList AssembleSnapshotsUntil(in StateId stateId, long minBlockNumber, int estimatedSize);
    StateId? GetLastSnapshotId();
    ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber);
    ArrayPoolList<StateId> GetSnapshotBeforeStateId(long blockNumber);
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
