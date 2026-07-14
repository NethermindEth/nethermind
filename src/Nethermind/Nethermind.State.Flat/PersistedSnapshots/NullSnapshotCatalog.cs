// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// No-op <see cref="ISnapshotCatalog"/> wired alongside <see cref="NullPersistedSnapshotLoader"/> and
/// <see cref="NullPersistedSnapshotCompactor"/> when long finality is disabled: the persisted tier is
/// always empty, so nothing is recorded, removed, or loaded.
/// </summary>
public sealed class NullSnapshotCatalog : ISnapshotCatalog
{
    public static readonly NullSnapshotCatalog Instance = new();

    private NullSnapshotCatalog() { }

    public void Add(CatalogEntry entry) { }

    public bool Remove(in StateId to, long depth) => false;

    public IEnumerable<CatalogEntry> Load() => [];
}
