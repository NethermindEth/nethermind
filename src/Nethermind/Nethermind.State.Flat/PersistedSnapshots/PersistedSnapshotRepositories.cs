// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Bundles the two per-tier <see cref="IPersistedSnapshotRepository"/> instances
/// so consumers (<c>PersistenceManager</c>, <c>SnapshotRepository</c>, the
/// compactors) can resolve both from DI as a single dependency.
/// <para>
/// <see cref="Small"/> holds snapshots whose block range is strictly less than
/// <c>CompactSize</c>. <see cref="Large"/> holds snapshots of exactly
/// <c>CompactSize</c> and the larger compacted snapshots produced by the
/// large-tier compactor.
/// </para>
/// </summary>
public sealed record PersistedSnapshotRepositories(
    IPersistedSnapshotRepository Small,
    IPersistedSnapshotRepository Large);

/// <summary>
/// Bundles the two per-tier <see cref="IPersistedSnapshotCompactor"/> instances.
/// Each compactor operates within its repo's size band — see
/// <see cref="PersistedSnapshotCompactor.Mode"/>.
/// </summary>
public sealed record PersistedSnapshotCompactors(
    IPersistedSnapshotCompactor Small,
    IPersistedSnapshotCompactor Large);

/// <summary>
/// DI shim that bundles the two per-tier records so the
/// <see cref="PersistedSnapshotRepository"/> and <see cref="PersistedSnapshotCompactor"/>
/// for each tier share the same <see cref="Storage.ArenaManager"/> instance — they
/// must, otherwise compaction would write through a different mmap than the
/// repo reads from. <c>FlatWorldStateModule</c> registers a single factory that
/// constructs both records together; the per-record singletons just unwrap this.
/// </summary>
public sealed record PerTierState(
    PersistedSnapshotRepositories Repositories,
    PersistedSnapshotCompactors Compactors);
