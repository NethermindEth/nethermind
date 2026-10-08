// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Db;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Pbt.PersistedSnapshots;

internal sealed class PbtSnapshotCatalog([KeyFilter(PbtSnapshotCatalog.DatabaseKey)] IDb db) : ISnapshotCatalog
{
    internal const string DatabaseKey = "PbtRetainedSnapshotCatalog";
    private readonly SnapshotCatalog _catalog = new(db);

    public void Add(CatalogEntry entry)
    {
        _catalog.Add(entry);
        db.SyncWal();
    }

    public bool Remove(in StateId to, long depth)
    {
        bool removed = _catalog.Remove(to, depth);
        // A retry can see a live deletion whose previous WAL sync failed.
        db.SyncWal();
        return removed;
    }

    public IEnumerable<CatalogEntry> Load() => _catalog.Load();
}
