// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// DI shim bundling the single persisted-snapshot repository with its compactor so they
/// share the same <see cref="Storage.ArenaManager"/> instance — they must, otherwise
/// compaction would write through a different mmap than the repository reads from.
/// <c>FlatWorldStateModule</c> registers a single factory that constructs them together;
/// the per-component singletons just unwrap this.
/// </summary>
public sealed record PersistedSnapshotComponents(
    IPersistedSnapshotRepository Repository,
    IPersistedSnapshotCompactor Compactor);
