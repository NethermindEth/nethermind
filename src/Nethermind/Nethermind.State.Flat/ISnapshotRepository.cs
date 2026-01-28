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
    SnapshotPooledList AssembleSnapshots(in StateId stateId, in StateId targetStateId, int estimatedSize);
    SnapshotPooledList AssembleSnapshotsUntil(in StateId stateId, long minBlockNumber, int estimatedSize);
    StateId? GetLastSnapshotId();
    ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber);
    void RemoveStatesUntil(in StateId currentPersistedStateId);
}
