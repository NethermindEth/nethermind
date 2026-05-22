// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Bundles the two <see cref="IPersistedSnapshotCompactor"/> instances that operate over the
/// one shared <see cref="IPersistedSnapshotRepository"/>.
/// <para>
/// <see cref="Batched"/> is wired with <c>max = CompactSize</c> — its widest output, the
/// <c>CompactSize</c>-wide merge, is the persistable snapshot. <see cref="Boundary"/> is wired
/// with <c>min = 2 * CompactSize</c> for the wider hierarchical merges.
/// </para>
/// </summary>
public sealed record PersistedSnapshotCompactors(
    IPersistedSnapshotCompactor Batched,
    IPersistedSnapshotCompactor Boundary);

/// <summary>
/// DI shim bundling the single persisted-snapshot repository with its compactor pair so the
/// repository and both compactors share the same <see cref="Storage.ArenaManager"/> instance —
/// they must, otherwise compaction would write through a different mmap than the repository
/// reads from. <c>FlatWorldStateModule</c> registers a single factory that constructs them
/// together; the per-component singletons just unwrap this.
/// </summary>
public sealed record PersistedSnapshotComponents(
    IPersistedSnapshotRepository Repository,
    PersistedSnapshotCompactors Compactors);
