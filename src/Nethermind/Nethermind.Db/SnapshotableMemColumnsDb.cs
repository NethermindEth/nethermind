// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    /// <summary>
    /// In-memory column database with snapshot support.
    /// Each column is a separate SnapshotableMemDb instance.
    /// </summary>
    public class SnapshotableMemColumnsDb<TKey> : IColumnsDb<TKey> where TKey : struct, Enum
    {
        private readonly Dictionary<TKey, SnapshotableMemDb> _columnDbs = new();
        private readonly bool _neverPrune;

        private SnapshotableMemColumnsDb(TKey[] keys, bool neverPrune)
        {
            _neverPrune = neverPrune;
            foreach (TKey key in keys)
            {
                GetColumnDb(key);
            }
        }

        public SnapshotableMemColumnsDb(params TKey[] keys) : this(keys, false)
        {
        }

        public SnapshotableMemColumnsDb() : this(Enum.GetValues<TKey>(), false)
        {
        }

        public SnapshotableMemColumnsDb(string _) : this(Enum.GetValues<TKey>(), false)
        {
        }

        public SnapshotableMemColumnsDb(bool neverPrune) : this(Enum.GetValues<TKey>(), neverPrune)
        {
        }

        public IDb GetColumnDb(TKey key)
        {
            if (!_columnDbs.TryGetValue(key, out SnapshotableMemDb? db))
            {
                db = new SnapshotableMemDb($"Column_{key}", _neverPrune);
                _columnDbs[key] = db;
            }
            return db;
        }

        public IEnumerable<TKey> ColumnKeys => _columnDbs.Keys;

        public IReadOnlyColumnDb<TKey> CreateReadOnly(bool createInMemWriteStore)
        {
            return new ReadOnlyColumnsDb<TKey>(this, createInMemWriteStore);
        }

        public IColumnsWriteBatch<TKey> StartWriteBatch()
        {
            return new InMemoryColumnWriteBatch<TKey>(this);
        }

        public IColumnDbSnapshot<TKey> CreateSnapshot()
        {
            Dictionary<TKey, IKeyValueStoreSnapshot> snapshots = new();
            foreach (KeyValuePair<TKey, SnapshotableMemDb> kvp in _columnDbs)
            {
                snapshots[kvp.Key] = kvp.Value.CreateSnapshot();
            }
            return new ColumnSnapshot(snapshots);
        }

        public void Dispose()
        {
            foreach (SnapshotableMemDb db in _columnDbs.Values)
            {
                db.Dispose();
            }
        }

        public void Flush(bool onlyWal = false)
        {
            foreach (SnapshotableMemDb db in _columnDbs.Values)
            {
                db.Flush(onlyWal);
            }
        }

        /// <summary>
        /// Snapshot of column database at a specific point in time.
        /// </summary>
        private sealed class ColumnSnapshot(Dictionary<TKey, IKeyValueStoreSnapshot> snapshots) : IColumnDbSnapshot<TKey>
        {
            private readonly Dictionary<TKey, IKeyValueStoreSnapshot> _snapshots = snapshots;

            public IReadOnlyKeyValueStore GetColumn(TKey key)
            {
                return _snapshots[key];
            }

            public void Dispose()
            {
                foreach (IKeyValueStoreSnapshot snapshot in _snapshots.Values)
                {
                    snapshot.Dispose();
                }
            }
        }
    }
}
