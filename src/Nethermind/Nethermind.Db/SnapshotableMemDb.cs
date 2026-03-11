// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    /// <summary>
    /// In-memory database with MVCC-based snapshot support.
    /// Uses Multi-Version Concurrency Control to enable O(1) snapshot creation.
    /// </summary>
    public class SnapshotableMemDb(string name = nameof(SnapshotableMemDb)) : IFullDb, ISortedKeyValueStore, IKeyValueStoreWithSnapshot
    {
        private readonly SortedDictionary<byte[], VersionedEntry> _db = new(Bytes.Comparer);
        private long _currentVersion = 0;
        private readonly HashSet<long> _activeSnapshotVersions = new();
        private readonly object _versionLock = new();

        public long ReadsCount { get; private set; }
        public long WritesCount { get; private set; }

        public string Name { get; } = name;

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Get(key);
            set => Set(key, value);
        }

        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
        {
            get
            {
                ReadsCount += keys.Length;
                KeyValuePair<byte[], byte[]?>[] result = new KeyValuePair<byte[], byte[]?>[keys.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    result[i] = new KeyValuePair<byte[], byte[]?>(keys[i], Get(keys[i]));
                }
                return result;
            }
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            ReadsCount++;
            byte[] keyArray = key.ToArray();
            lock (_versionLock)
            {
                if (!_db.TryGetValue(keyArray, out VersionedEntry? entry))
                {
                    return null;
                }
                return entry.GetLatestValue();
            }
        }

        public unsafe Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
            => Get(key, flags).AsSpan();

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            WritesCount++;
            byte[] keyArray = key.ToArray();

            lock (_versionLock)
            {
                _currentVersion++;

                if (value is null)
                {
                    // Removing: add a tombstone version
                    if (_db.TryGetValue(keyArray, out VersionedEntry? entry))
                    {
                        entry.AddVersion(_currentVersion, null);
                    }
                    else
                    {
                        // Key never existed, create entry with null value
                        VersionedEntry newEntry = new();
                        newEntry.AddVersion(_currentVersion, null);
                        _db[keyArray] = newEntry;
                    }
                }
                else
                {
                    if (!_db.TryGetValue(keyArray, out VersionedEntry? entry))
                    {
                        entry = new VersionedEntry();
                        _db[keyArray] = entry;
                    }
                    entry.AddVersion(_currentVersion, value);
                }
            }
        }

        public void Remove(ReadOnlySpan<byte> key)
        {
            Set(key, null);
        }

        public bool KeyExists(ReadOnlySpan<byte> key)
        {
            byte[] keyArray = key.ToArray();
            lock (_versionLock)
            {
                if (!_db.TryGetValue(keyArray, out VersionedEntry? entry))
                {
                    return false;
                }
                return entry.GetLatestValue() is not null;
            }
        }

        public IWriteBatch StartWriteBatch() => new MemDbWriteBatch(this);

        public void Flush(bool onlyWal = false) { }

        public void Clear()
        {
            lock (_versionLock)
            {
                _db.Clear();
                _currentVersion = 0;
                _activeSnapshotVersions.Clear();
            }
        }

        public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false)
        {
            lock (_versionLock)
            {
                foreach (KeyValuePair<byte[], VersionedEntry> kvp in _db)
                {
                    byte[]? value = kvp.Value.GetLatestValue();
                    if (value is not null)
                    {
                        yield return new KeyValuePair<byte[], byte[]?>(kvp.Key, value);
                    }
                }
            }
        }

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
        {
            lock (_versionLock)
            {
                foreach (KeyValuePair<byte[], VersionedEntry> kvp in _db)
                {
                    if (kvp.Value.GetLatestValue() is not null)
                    {
                        yield return kvp.Key;
                    }
                }
            }
        }

        public IEnumerable<byte[]> GetAllValues(bool ordered = false)
        {
            lock (_versionLock)
            {
                foreach (VersionedEntry entry in _db.Values)
                {
                    byte[]? value = entry.GetLatestValue();
                    if (value is not null)
                    {
                        yield return value;
                    }
                }
            }
        }

        public ICollection<byte[]> Keys
        {
            get
            {
                lock (_versionLock)
                {
                    return _db.Keys.Where(k => _db[k].GetLatestValue() is not null).ToArray();
                }
            }
        }

        public ICollection<byte[]> Values
        {
            get
            {
                lock (_versionLock)
                {
                    return _db.Values.Select(e => e.GetLatestValue()).Where(v => v is not null).ToArray();
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_versionLock)
                {
                    return _db.Values.Count(e => e.GetLatestValue() is not null);
                }
            }
        }

        public void Dispose() { }

        public bool PreferWriteByArray => true;

        public unsafe void DangerousReleaseMemory(in ReadOnlySpan<byte> span) { }

        public IDbMeta.DbMetric GatherMetric() => new() { Size = Count };

        // ISortedKeyValueStore implementation
        public byte[]? FirstKey
        {
            get
            {
                lock (_versionLock)
                {
                    foreach (KeyValuePair<byte[], VersionedEntry> kvp in _db)
                    {
                        if (kvp.Value.GetLatestValue() is not null)
                        {
                            return kvp.Key;
                        }
                    }
                    return null;
                }
            }
        }

        public byte[]? LastKey
        {
            get
            {
                lock (_versionLock)
                {
                    foreach (KeyValuePair<byte[], VersionedEntry> kvp in _db.Reverse())
                    {
                        if (kvp.Value.GetLatestValue() is not null)
                        {
                            return kvp.Key;
                        }
                    }
                    return null;
                }
            }
        }

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
        {
            return new MemDbSortedView(this, _currentVersion, firstKeyInclusive.ToArray(), lastKeyExclusive.ToArray());
        }

        // IKeyValueStoreWithSnapshot implementation
        public IKeyValueStoreSnapshot CreateSnapshot()
        {
            lock (_versionLock)
            {
                long snapshotVersion = _currentVersion;
                _activeSnapshotVersions.Add(snapshotVersion);
                return new MemDbSnapshot(this, snapshotVersion);
            }
        }

        internal void OnSnapshotDisposed(long version)
        {
            lock (_versionLock)
            {
                _activeSnapshotVersions.Remove(version);

                // Fast path: no active snapshots - keep only latest version per key
                if (_activeSnapshotVersions.Count == 0)
                {
                    foreach (VersionedEntry entry in _db.Values)
                    {
                        entry.KeepOnlyLatest();
                    }
                    return;
                }

                // Slow path: prune versions older than oldest active snapshot
                long minVersion = _activeSnapshotVersions.Min();
                foreach (VersionedEntry entry in _db.Values)
                {
                    entry.PruneVersionsOlderThan(minVersion);
                }
            }
        }

        internal byte[]? GetAtVersion(ReadOnlySpan<byte> key, long version)
        {
            byte[] keyArray = key.ToArray();
            lock (_versionLock)
            {
                if (!_db.TryGetValue(keyArray, out VersionedEntry? entry))
                {
                    return null;
                }
                return entry.GetValueAtVersion(version);
            }
        }

        internal IEnumerable<KeyValuePair<byte[], byte[]?>> GetAllAtVersion(long version)
        {
            lock (_versionLock)
            {
                foreach (KeyValuePair<byte[], VersionedEntry> kvp in _db)
                {
                    byte[]? value = kvp.Value.GetValueAtVersion(version);
                    if (value is not null)
                    {
                        yield return new KeyValuePair<byte[], byte[]?>(kvp.Key, value);
                    }
                }
            }
        }

        /// <summary>
        /// Holds multiple versions of a value for a single key.
        /// Versions are stored newest-first for optimal read performance.
        /// </summary>
        private sealed class VersionedEntry
        {
            public List<(long Version, byte[]? Value)> Versions { get; } = new();

            public void AddVersion(long version, byte[]? value)
            {
                // Insert at front (newest first)
                Versions.Insert(0, (version, value));
            }

            public byte[]? GetLatestValue()
            {
                if (Versions.Count == 0) return null;
                return Versions[0].Value;
            }

            public byte[]? GetValueAtVersion(long version)
            {
                // Find latest version <= requested version
                for (int i = 0; i < Versions.Count; i++)
                {
                    (long ver, byte[]? val) = Versions[i];
                    if (ver <= version)
                    {
                        return val;
                    }
                }
                return null;
            }

            public void KeepOnlyLatest()
            {
                if (Versions.Count > 1)
                {
                    Versions.RemoveRange(1, Versions.Count - 1);
                }
            }

            public void PruneVersionsOlderThan(long minVersion)
            {
                // Find first version older than minVersion and remove all after it
                for (int i = 0; i < Versions.Count; i++)
                {
                    if (Versions[i].Version < minVersion)
                    {
                        if (i < Versions.Count - 1)
                        {
                            Versions.RemoveRange(i + 1, Versions.Count - i - 1);
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Read-only snapshot of SnapshotableMemDb at a specific version.
        /// </summary>
        private sealed class MemDbSnapshot(SnapshotableMemDb db, long snapshotVersion) : IKeyValueStoreSnapshot, ISortedKeyValueStore
        {
            private readonly SnapshotableMemDb _db = db;
            private readonly long _snapshotVersion = snapshotVersion;

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
            {
                return _db.GetAtVersion(key, _snapshotVersion);
            }

            public unsafe Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
                => Get(key, flags).AsSpan();

            public bool KeyExists(ReadOnlySpan<byte> key)
            {
                return Get(key) is not null;
            }

            public unsafe void DangerousReleaseMemory(in ReadOnlySpan<byte> span) { }

            public bool PreferWriteByArray => true;

            public byte[]? FirstKey
            {
                get
                {
                    lock (_db._versionLock)
                    {
                        foreach (KeyValuePair<byte[], VersionedEntry> kvp in _db._db)
                        {
                            if (kvp.Value.GetValueAtVersion(_snapshotVersion) is not null)
                            {
                                return kvp.Key;
                            }
                        }
                        return null;
                    }
                }
            }

            public byte[]? LastKey
            {
                get
                {
                    lock (_db._versionLock)
                    {
                        foreach (KeyValuePair<byte[], VersionedEntry> kvp in _db._db.Reverse())
                        {
                            if (kvp.Value.GetValueAtVersion(_snapshotVersion) is not null)
                            {
                                return kvp.Key;
                            }
                        }
                        return null;
                    }
                }
            }

            public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
            {
                return new MemDbSortedView(_db, _snapshotVersion, firstKeyInclusive.ToArray(), lastKeyExclusive.ToArray());
            }

            public void Dispose()
            {
                _db.OnSnapshotDisposed(_snapshotVersion);
            }
        }

        /// <summary>
        /// Sorted view iterator for a range of keys.
        /// </summary>
        private sealed class MemDbSortedView(SnapshotableMemDb db, long version, byte[] firstKey, byte[] lastKey) : ISortedView
        {
            private readonly SnapshotableMemDb _db = db;
            private readonly long _version = version;
            private readonly byte[] _firstKey = firstKey;
            private readonly byte[] _lastKey = lastKey;
            private IEnumerator<KeyValuePair<byte[], VersionedEntry>>? _enumerator;
            private byte[]? _currentKey;
            private byte[]? _currentValue;

            public bool StartBefore(ReadOnlySpan<byte> key)
            {
                byte[] keyArray = key.ToArray();
                lock (_db._versionLock)
                {
                    _enumerator = _db._db
                        .Where(kvp => Bytes.BytesComparer.Compare(kvp.Key, _firstKey) >= 0 &&
                                     Bytes.BytesComparer.Compare(kvp.Key, _lastKey) < 0 &&
                                     Bytes.BytesComparer.Compare(kvp.Key, keyArray) < 0)
                        .GetEnumerator();
                    return true;
                }
            }

            public bool MoveNext()
            {
                if (_enumerator is null)
                {
                    lock (_db._versionLock)
                    {
                        _enumerator = _db._db
                            .Where(kvp => Bytes.BytesComparer.Compare(kvp.Key, _firstKey) >= 0 &&
                                         Bytes.BytesComparer.Compare(kvp.Key, _lastKey) < 0)
                            .GetEnumerator();
                    }
                }

                while (_enumerator.MoveNext())
                {
                    KeyValuePair<byte[], VersionedEntry> current = _enumerator.Current;
                    byte[]? value = current.Value.GetValueAtVersion(_version);
                    if (value is not null)
                    {
                        _currentKey = current.Key;
                        _currentValue = value;
                        return true;
                    }
                }

                return false;
            }

            public ReadOnlySpan<byte> CurrentKey => _currentKey ?? ReadOnlySpan<byte>.Empty;
            public ReadOnlySpan<byte> CurrentValue => _currentValue ?? ReadOnlySpan<byte>.Empty;

            public void Dispose()
            {
                _enumerator?.Dispose();
            }
        }

        /// <summary>
        /// Write batch that collects all operations and commits them atomically with a single lock.
        /// </summary>
        private sealed class MemDbWriteBatch(SnapshotableMemDb db) : IWriteBatch
        {
            private readonly SnapshotableMemDb _db = db;
            private readonly ArrayPoolList<(byte[] Key, byte[]? Value, WriteFlags Flags)> _operations = new(16);
            private bool _disposed;

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(MemDbWriteBatch));
                _operations.Add((key.ToArray(), value, flags));
            }

            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
            {
                // For in-memory database, merge is the same as set
                Set(key, value.ToArray(), flags);
            }

            public void Clear()
            {
                if (_disposed) throw new ObjectDisposedException(nameof(MemDbWriteBatch));
                _operations.Clear();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (_operations.Count > 0)
                {
                    lock (_db._versionLock)
                    {
                        // Increment version once for the entire batch
                        _db._currentVersion++;
                        long batchVersion = _db._currentVersion;

                        foreach ((byte[] key, byte[]? value, WriteFlags _) in _operations)
                        {
                            if (value is null)
                            {
                                // Removing: add a tombstone version
                                if (_db._db.TryGetValue(key, out VersionedEntry? entry))
                                {
                                    entry.AddVersion(batchVersion, null);
                                }
                                else
                                {
                                    VersionedEntry newEntry = new();
                                    newEntry.AddVersion(batchVersion, null);
                                    _db._db[key] = newEntry;
                                }
                            }
                            else
                            {
                                // Adding or updating
                                if (_db._db.TryGetValue(key, out VersionedEntry? entry))
                                {
                                    entry.AddVersion(batchVersion, value);
                                }
                                else
                                {
                                    VersionedEntry newEntry = new();
                                    newEntry.AddVersion(batchVersion, value);
                                    _db._db[key] = newEntry;
                                }
                            }
                        }

                        _db.WritesCount += _operations.Count;
                    }
                }

                _operations.Dispose();
            }
        }
    }
}
