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

    void AddStateId(StateId stateId);
    bool TryAddSnapshot(Snapshot snapshot);
    bool TryAddCompactedSnapshot(Snapshot snapshot);
    bool TryLeaseState(StateId stateId, [NotNullWhen(true)] out Snapshot? entry);
    bool TryLeaseCompactedState(StateId stateId, [NotNullWhen(true)] out Snapshot? entry);
    bool RemoveAndReleaseCompactedKnownState(StateId stateId);
    bool HasState(StateId stateId);
    SnapshotPooledList AssembleSnapshotsUntil(StateId stateId, long startingBlockNumber, int estimatedSize);
    bool TryFindStateIdForStateRoot(Hash256 stateRoot, out StateId outStateId);
    StateId? GetLastSnapshotId();
    ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber);
    void RemoveStatesUntil(StateId currentPersistedStateId);
}
