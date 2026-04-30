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
        private readonly Dictionary<TKey, SnapshotableMemDb> _columnDbs = new();
        private readonly bool _neverPrune;

        // Cross-column atomicity guard. Each per-column SnapshotableMemDb has its own version
        // counter and lock, so per-column reads/writes are individually consistent. But a
        // multi-column writeBatch dispose applies columns one-by-one, and CreateSnapshot
        // captures column snapshots one-by-one. Without this lock a snapshot taken concurrently
        // with an in-flight writeBatch dispose can capture some columns AFTER the new writes
        // and others BEFORE, producing a cross-column-inconsistent reader view. RocksDB does
        // not have this problem (its snapshots are atomic across CFs); this lock makes the
        // in-memory test backend match.
        private readonly Lock _atomicityLock = new();

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
            using Lock.Scope _ = _atomicityLock.EnterScope();
            Dictionary<TKey, IKeyValueStoreSnapshot> snapshots = new();
            foreach (KeyValuePair<TKey, SnapshotableMemDb> kvp in _columnDbs)
            {
                snapshots[kvp.Key] = kvp.Value.CreateSnapshot();
            }
            return new ColumnSnapshot(snapshots);
        }

        /// <summary>
        /// Wraps <see cref="InMemoryColumnWriteBatch{TKey}"/> so the per-column commit phase
        /// happens under the columns DB's write lock, making the multi-column commit atomic
        /// w.r.t. <see cref="CreateSnapshot"/>.
        /// </summary>
        private sealed class AtomicColumnsWriteBatch(SnapshotableMemColumnsDb<TKey> db) : IColumnsWriteBatch<TKey>
        {
            private readonly InMemoryColumnWriteBatch<TKey> _inner = new(db);

            public IWriteBatch GetColumnBatch(TKey key) => _inner.GetColumnBatch(key);

            public void Clear() => _inner.Clear();

            public void Dispose()
            {
                using Lock.Scope _ = db._atomicityLock.EnterScope();
                _inner.Dispose();
            }
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
