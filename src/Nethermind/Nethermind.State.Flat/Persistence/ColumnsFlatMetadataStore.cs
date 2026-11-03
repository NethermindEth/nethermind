// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence;

/// <summary>The markers of the RocksDB layouts, kept in the flat DB's <see cref="FlatDbColumns.Metadata"/> column.</summary>
public sealed class ColumnsFlatMetadataStore : IFlatMetadataStore
{
    private readonly IColumnsDb<FlatDbColumns> _db;

    /// <summary>Opens the markers of <paramref name="db"/>, refusing a DB synced with a layout other than the configured one.</summary>
    /// <remarks>
    /// Built before the trie node log, so a DB of another layout is refused before the log recovers against these
    /// markers and truncates every record they do not confirm.
    /// </remarks>
    /// <exception cref="Core.Exceptions.InvalidConfigurationException">The DB was synced with another layout.</exception>
    public ColumnsFlatMetadataStore(IColumnsDb<FlatDbColumns> db, IFlatDbConfig config)
    {
        BasePersistence.ValidateLayout(db, config.Layout);
        _db = db;
    }

    /// <inheritdoc/>
    public IReadOnlyKeyValueStore Committed => _db.GetColumnDb(FlatDbColumns.Metadata);

    /// <inheritdoc/>
    public IFlatMetadataStore.ISnapshot OpenSnapshot(IColumnDbSnapshot<FlatDbColumns> rocksDbSnapshot) =>
        new SnapshotMetadata(rocksDbSnapshot.GetColumn(FlatDbColumns.Metadata));

    /// <inheritdoc/>
    public void WipeForResync()
    {
        BasePersistence.ClearAllColumns(_db);
        _db.Flush();
    }

    /// <summary>The metadata column of a RocksDB snapshot; the snapshot's owner disposes the snapshot itself.</summary>
    internal sealed class SnapshotMetadata(IReadOnlyKeyValueStore metadata) : IFlatMetadataStore.ISnapshot
    {
        public IReadOnlyKeyValueStore Metadata => metadata;

        public void Dispose() { }
    }
}
