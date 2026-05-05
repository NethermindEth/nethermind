// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

public sealed class NullPersistedSnapshotRepository : IPersistedSnapshotRepository
{
    public static readonly NullPersistedSnapshotRepository Instance = new();

    private NullPersistedSnapshotRepository() { }

    public int SnapshotCount => 0;
    public long BaseSnapshotMemory => 0;
    public long CompactedSnapshotMemory => 0;
    public long KeyBloomMemory => 0;
    public long TrieBloomMemory => 0;
    public int ArenaFileCount => 0;
    public long ArenaMappedBytes => 0;
    public PersistedSnapshotBloomFilterManager BloomManager { get; } = new();
    public void LoadFromCatalog() { }
    public void ConvertSnapshotToPersistedSnapshot(Snapshot snapshot, bool isPersistable = false) { }
    public void AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, HashSet<int> referencedSnapshotIds, bool isPersistable, BloomFilter? bloom = null) { }
    public PersistedSnapshotList AssembleSnapshotsForCompaction(StateId toStateId, long minBlockNumber) => PersistedSnapshotList.Empty();
    public PersistedSnapshot? TryGetSnapshotFrom(StateId fromState) => null;
    public bool TryLeaseSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot) { snapshot = null; return false; }
    public bool TryLeaseCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot) { snapshot = null; return false; }
    public bool TryLeasePersistableCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot) { snapshot = null; return false; }
    public int PruneBefore(StateId stateId) => 0;
    public bool HasBaseSnapshot(in StateId stateId) => false;
    public void Dispose() { }
}
