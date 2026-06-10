// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

public sealed class NullPersistedSnapshotRepository : IPersistedSnapshotRepository
{
    public static readonly NullPersistedSnapshotRepository Instance = new();

    private NullPersistedSnapshotRepository() { }

    public int SnapshotCount => 0;
    public long CompactedSnapshotMemory => 0;
    public StateId? LastRegisteredState => null;
    public void LoadFromCatalog() { }
    public PersistedSnapshot ConvertSnapshotToPersistedSnapshot(Snapshot snapshot)
        => throw new NotSupportedException($"{nameof(NullPersistedSnapshotRepository)} cannot host persisted snapshots.");
    public PersistedSnapshot AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, BloomFilter bloom, bool isPersistable = false)
        => throw new NotSupportedException($"{nameof(NullPersistedSnapshotRepository)} cannot host compacted snapshots.");
    public PersistedSnapshotList AssembleSnapshotsForCompaction(StateId toStateId, long minBlockNumber) => PersistedSnapshotList.Empty();
    public PersistedSnapshotList LeaseBaseSnapshotsInRange(StateId from, StateId to) => PersistedSnapshotList.Empty();
    public bool TryLeaseSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot) { snapshot = null; return false; }
    public bool TryLeaseCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot) { snapshot = null; return false; }
    public bool TryLeasePersistableCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot) { snapshot = null; return false; }
    public void RemoveStatesUntil(long blockNumber) { }
    public ArrayPoolList<StateId> GetPersistedStatesInRange(long startBlockInclusive, long endBlockInclusive) => ArrayPoolList<StateId>.Empty();
    public bool RemovePersistedStateExact(in StateId toState) => false;
    public bool HasBaseSnapshot(in StateId stateId) => false;
    public void Dispose() { }
}
