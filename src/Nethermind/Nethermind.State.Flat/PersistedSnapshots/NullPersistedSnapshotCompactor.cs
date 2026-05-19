// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// No-op <see cref="IPersistedSnapshotCompactor"/> wired alongside
/// <see cref="NullPersistedSnapshotRepository"/> when the long-finality feature is
/// disabled, so the rest of the persistence pipeline can resolve a compactor
/// without spinning up real arena-backed compaction work.
/// </summary>
public sealed class NullPersistedSnapshotCompactor : IPersistedSnapshotCompactor
{
    public static readonly NullPersistedSnapshotCompactor Instance = new();

    private NullPersistedSnapshotCompactor() { }

    public void DoCompactSnapshot(StateId state) { }
}
