// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Db
{
    /// <summary>
    /// In-memory column database with snapshot support.
    /// Each column is a separate SnapshotableMemDb instance.
    /// </summary>
    public class SnapshotableMemColumnsDb<TKey> : IColumnsDb<TKey> where TKey : struct, Enum
    {
        private readonly Dictionary<TKey, SnapshotableMemDb> _columnDbs = [];
        private readonly bool _neverPrune;

        // RocksDB commits a write batch and takes a snapshot atomically across column families; without this
        // lock a snapshot could see part of a batch (e.g. flat's current-state marker ahead of its data).
        private readonly Lock _commitLock = new();

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

        public IReadOnlyColumnDb<TKey> CreateReadOnly(bool createInMemWriteStore) => new ReadOnlyColumnsDb<TKey>(this, createInMemWriteStore);

        public IColumnsWriteBatch<TKey> StartWriteBatch() => new AtomicColumnsWriteBatch(this);

        public IColumnDbSnapshot<TKey> CreateSnapshot()
        {
            Dictionary<TKey, IKeyValueStoreSnapshot> snapshots = [];
            using Lock.Scope _ = _commitLock.EnterScope();
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

        private sealed class AtomicColumnsWriteBatch(SnapshotableMemColumnsDb<TKey> columnsDb) : IColumnsWriteBatch<TKey>
        {
            private readonly InMemoryColumnWriteBatch<TKey> _batch = new(columnsDb);

            public IWriteBatch GetColumnBatch(TKey key) => _batch.GetColumnBatch(key);

            public void Clear() => _batch.Clear();

            public void Dispose()
            {
                using Lock.Scope _ = columnsDb._commitLock.EnterScope();
                _batch.Dispose();
            }
        }

        /// <summary>
        /// Snapshot of column database at a specific point in time.
        /// </summary>
        private sealed class ColumnSnapshot(Dictionary<TKey, IKeyValueStoreSnapshot> snapshots) : IColumnDbSnapshot<TKey>
        {
            private readonly Dictionary<TKey, IKeyValueStoreSnapshot> _snapshots = snapshots;

            public IReadOnlyKeyValueStore GetColumn(TKey key) => _snapshots[key];

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
