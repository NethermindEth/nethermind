// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Db
{
    /// <summary>
    /// In-memory database with MVCC-based snapshot support.
    /// Uses Multi-Version Concurrency Control to enable O(1) snapshot creation.
    /// </summary>
    public class SnapshotableMemDb(string name = nameof(SnapshotableMemDb), bool neverPrune = false) : IFullDb, ISortedKeyValueStore, IKeyValueStoreWithSnapshot
    {
        private readonly SortedSet<(byte[] Key, int Version, byte[]? Value)> _db = new(new EntryComparer());
        private readonly EntryComparer _entryComparer = new();
        private int _currentVersion = 0;
        private readonly HashSet<int> _activeSnapshotVersions = new();
        private readonly Lock _versionLock = new();
        private readonly bool _neverPrune = neverPrune;

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
                KeyValuePair<byte[], byte[]?>[] result = new KeyValuePair<byte[], byte[]?>[keys.Length];
                lock (_versionLock)
                {
                    ReadsCount += keys.Length;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        byte[] key = keys[i];
                        result[i] = new KeyValuePair<byte[], byte[]?>(key, GetValueAtVersion(key, _currentVersion));
                    }
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
                return GetValueAtVersion(keyArray, _currentVersion);
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
                AddOrReplace((keyArray, _currentVersion, value));

                if (!_neverPrune && _activeSnapshotVersions.Count == 0)
                {
                    RemovePreviousVersions(keyArray, _currentVersion);
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
                return GetValueAtVersion(keyArray, _currentVersion) is not null;
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
            List<KeyValuePair<byte[], byte[]?>> result;
            lock (_versionLock)
            {
                result = new List<KeyValuePair<byte[], byte[]?>>();
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    byte[]? value = GetValueAtVersion(key, _currentVersion);
                    if (value is not null)
                    {
                        result.Add(new KeyValuePair<byte[], byte[]?>(key, value));
                    }
                }
            }
            return result;
        }

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
        {
            List<byte[]> result;
            lock (_versionLock)
            {
                result = new List<byte[]>();
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    if (GetValueAtVersion(key, _currentVersion) is not null)
                    {
                        result.Add(key);
                    }
                }
            }
            return result;
        }

        public IEnumerable<byte[]> GetAllValues(bool ordered = false)
        {
            List<byte[]> result;
            lock (_versionLock)
            {
                result = new List<byte[]>();
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    byte[]? value = GetValueAtVersion(key, _currentVersion);
                    if (value is not null)
                    {
                        result.Add(value);
                    }
                }
            }
            return result;
        }

        public ICollection<byte[]> Keys
        {
            get
            {
                lock (_versionLock)
                {
                    return GetAllUniqueKeys()
                        .Where(k => GetValueAtVersion(k, _currentVersion) is not null)
                        .ToArray();
                }
            }
        }

        public ICollection<byte[]> Values
        {
            get
            {
                lock (_versionLock)
                {
                    return GetAllUniqueKeys()
                        .Select(k => GetValueAtVersion(k, _currentVersion))
                        .Where(v => v is not null)
                        .ToArray()!;
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_versionLock)
                {
                    return GetAllUniqueKeys()
                        .Count(k => GetValueAtVersion(k, _currentVersion) is not null);
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
                    return FindFirstKeyAtVersion(_currentVersion);
                }
            }
        }

        public byte[]? LastKey
        {
            get
            {
                lock (_versionLock)
                {
                    return FindLastKeyAtVersion(_currentVersion);
                }
            }
        }

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
        {
            int version;
            lock (_versionLock)
            {
                version = _currentVersion;
            }
            return new MemDbSortedView(this, version, firstKeyInclusive.ToArray(), lastKeyExclusive.ToArray());
        }

        // IKeyValueStoreWithSnapshot implementation
        public IKeyValueStoreSnapshot CreateSnapshot()
        {
            lock (_versionLock)
            {
                int snapshotVersion = _currentVersion;
                _activeSnapshotVersions.Add(snapshotVersion);
                return new MemDbSnapshot(this, snapshotVersion);
            }
        }

        internal void OnSnapshotDisposed(int version)
        {
            lock (_versionLock)
            {
                _activeSnapshotVersions.Remove(version);

                // Skip pruning if disabled
                if (_neverPrune)
                {
                    return;
                }

                // Fast path: no active snapshots - keep only latest version per key
                if (_activeSnapshotVersions.Count == 0)
                {
                    KeepOnlyLatestVersions();
                    return;
                }

                // Slow path: prune versions older than oldest active snapshot
                int minVersion = _activeSnapshotVersions.Min();
                PruneVersionsOlderThan(minVersion);
            }
        }

        internal byte[]? GetAtVersion(ReadOnlySpan<byte> key, int version)
        {
            byte[] keyArray = key.ToArray();
            lock (_versionLock)
            {
                return GetValueAtVersion(keyArray, version);
            }
        }

        internal IEnumerable<KeyValuePair<byte[], byte[]?>> GetAllAtVersion(int version)
        {
            List<KeyValuePair<byte[], byte[]?>> result;
            lock (_versionLock)
            {
                result = new List<KeyValuePair<byte[], byte[]?>>();
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    byte[]? value = GetValueAtVersion(key, version);
                    if (value is not null)
                    {
                        result.Add(new KeyValuePair<byte[], byte[]?>(key, value));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Gets the value for a key at a specific version (latest version &lt;= requested version).
        /// Returns null if the key doesn't exist or was deleted (tombstone) at that version.
        /// Uses GetViewBetween for O(log n) lookup.
        /// </summary>
        private byte[]? GetValueAtVersion(byte[] key, int version)
        {
            var lower = (key, 0, (byte[]?)null);
            var upper = (key, version, (byte[]?)null);

            if (_entryComparer.Compare(lower, upper) > 0)
                return null;

            var view = _db.GetViewBetween(lower, upper);
            var max = view.Max;
            return max.Key is not null ? max.Value : null;
        }

        /// <summary>
        /// Returns all unique keys in sorted order.
        /// </summary>
        private IEnumerable<byte[]> GetAllUniqueKeys()
        {
            byte[]? lastKey = null;
            foreach (var entry in _db)
            {
                if (lastKey == null || lastKey.AsSpan().SequenceCompareTo(entry.Key) != 0)
                {
                    lastKey = entry.Key;
                    yield return entry.Key;
                }
            }
        }

        private byte[]? FindFirstKeyAtVersion(int version)
        {
            foreach (byte[] key in GetAllUniqueKeys())
            {
                if (GetValueAtVersion(key, version) is not null)
                    return key;
            }
            return null;
        }

        private byte[]? FindLastKeyAtVersion(int version)
        {
            byte[]? lastKey = null;
            foreach (var entry in _db.Reverse())
            {
                if (lastKey is null || lastKey.AsSpan().SequenceCompareTo(entry.Key) != 0)
                {
                    lastKey = entry.Key;
                    if (GetValueAtVersion(entry.Key, version) is not null)
                        return entry.Key;
                }
            }
            return null;
        }

        /// <summary>
        /// Removes all versions except the latest for each key.
        /// Called when there are no active snapshots.
        /// </summary>
        private void KeepOnlyLatestVersions()
        {
            using ArrayPoolList<(byte[] Key, int Version, byte[]? Value)> toRemove = new(_db.Count);
            (byte[] Key, int Version, byte[]? Value) prev = default;

            foreach (var entry in _db)
            {
                if (prev.Key is not null && prev.Key.AsSpan().SequenceCompareTo(entry.Key) == 0)
                {
                    toRemove.Add(prev);
                }

                prev = entry;
            }

            foreach (var entry in toRemove)
            {
                _db.Remove(entry);
            }
        }

        /// <summary>
        /// Removes old versions per key, keeping the latest version below minVersion
        /// so that snapshots at or after that version can still resolve the key.
        /// </summary>
        private void PruneVersionsOlderThan(int minVersion)
        {
            using ArrayPoolList<(byte[] Key, int Version, byte[]? Value)> toRemove = new(_db.Count);
            byte[]? prevKey = null;
            int prevVersion = 0;
            byte[]? prevValue = null;

            foreach (var entry in _db)
            {
                if (entry.Version < minVersion &&
                    prevKey is not null &&
                    prevKey.AsSpan().SequenceCompareTo(entry.Key) == 0)
                {
                    // Same key, older entry — the current entry supersedes the previous one
                    toRemove.Add((prevKey, prevVersion, prevValue));
                }

                prevKey = entry.Key;
                prevVersion = entry.Version;
                prevValue = entry.Value;
            }

            foreach (var entry in toRemove)
            {
                _db.Remove(entry);
            }
        }

        /// <summary>
        /// Adds an entry to the set, replacing any existing entry with the same (Key, Version).
        /// Since the comparer ignores Value, we must remove-then-add to update values.
        /// </summary>
        private void AddOrReplace((byte[] Key, int Version, byte[]? Value) entry)
        {
            if (!_db.Add(entry))
            {
                _db.Remove(entry);
                _db.Add(entry);
            }
        }

        /// <summary>
        /// Removes all versions of a key older than the specified version.
        /// Used during writes when no snapshots are active to prevent unbounded memory growth.
        /// </summary>
        private void RemovePreviousVersions(byte[] key, int currentVersion)
        {
            var lower = (key, 0, (byte[]?)null);
            var upper = (key, currentVersion - 1, (byte[]?)null);

            if (_entryComparer.Compare(lower, upper) > 0)
                return;

            var view = _db.GetViewBetween(lower, upper);
            // Materialize before removing to avoid modifying during enumeration
            var toRemove = new List<(byte[] Key, int Version, byte[]? Value)>(view);
            foreach (var entry in toRemove)
            {
                _db.Remove(entry);
            }
        }

        /// <summary>
        /// Comparer for (byte[] Key, int Version, byte[]? Value) tuples.
        /// Compares by key first using byte array comparer, then by version. Ignores Value.
        /// </summary>
        private sealed class EntryComparer : IComparer<(byte[] Key, int Version, byte[]? Value)>
        {
            public int Compare((byte[] Key, int Version, byte[]? Value) x, (byte[] Key, int Version, byte[]? Value) y)
            {
                int keyComparison = x.Key.AsSpan().SequenceCompareTo(y.Key);
                if (keyComparison != 0)
                {
                    return keyComparison;
                }
                return x.Version.CompareTo(y.Version);
            }
        }

        /// <summary>
        /// Read-only snapshot of SnapshotableMemDb at a specific version.
        /// </summary>
        private sealed class MemDbSnapshot(SnapshotableMemDb db, int snapshotVersion) : IKeyValueStoreSnapshot, ISortedKeyValueStore
        {
            private readonly SnapshotableMemDb _db = db;
            private readonly int _snapshotVersion = snapshotVersion;

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
                        return _db.FindFirstKeyAtVersion(_snapshotVersion);
                    }
                }
            }

            public byte[]? LastKey
            {
                get
                {
                    lock (_db._versionLock)
                    {
                        return _db.FindLastKeyAtVersion(_snapshotVersion);
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
        /// Uses GetViewBetween for efficient range queries instead of scanning from the beginning.
        /// </summary>
        private sealed class MemDbSortedView(SnapshotableMemDb db, int version, byte[] firstKey, byte[] lastKey) : ISortedView
        {
            private readonly SnapshotableMemDb _db = db;
            private readonly int _version = version;
            private readonly byte[] _firstKey = firstKey;
            private readonly byte[] _lastKey = lastKey;
            private byte[]? _currentKey;
            private byte[]? _currentValue;

            public bool StartBefore(ReadOnlySpan<byte> key)
            {
                byte[] keyArray = key.ToArray();
                lock (_db._versionLock)
                {
                    var lower = (_firstKey, 0, (byte[]?)null);
                    var upper = (keyArray, 0, (byte[]?)null);

                    if (_db._entryComparer.Compare(lower, upper) > 0)
                    {
                        // key is before _firstKey: position before start so MoveNext yields first element
                        _currentKey = null;
                        _currentValue = null;
                        return true;
                    }

                    var view = _db._db.GetViewBetween(lower, upper);

                    byte[]? bestKey = null;
                    byte[]? bestValue = null;
                    byte[]? candidateKey = null;
                    byte[]? candidateValue = null;

                    foreach (var entry in view)
                    {
                        if (candidateKey is not null && candidateKey.AsSpan().SequenceCompareTo(entry.Key) != 0)
                        {
                            if (candidateValue is not null)
                            {
                                bestKey = candidateKey;
                                bestValue = candidateValue;
                            }
                            candidateKey = null;
                            candidateValue = null;
                        }

                        if (entry.Version <= _version)
                        {
                            candidateKey = entry.Key;
                            candidateValue = entry.Value;
                        }
                    }

                    if (candidateValue is not null)
                    {
                        bestKey = candidateKey;
                        bestValue = candidateValue;
                    }

                    _currentKey = bestKey;
                    _currentValue = bestValue;
                    return bestKey is not null;
                }
            }

            public bool MoveNext()
            {
                lock (_db._versionLock)
                {
                    var lower = _currentKey is not null
                        ? (_currentKey, int.MaxValue, (byte[]?)null)
                        : (_firstKey, 0, (byte[]?)null);
                    var upper = (_lastKey, 0, (byte[]?)null);

                    if (_db._entryComparer.Compare(lower, upper) > 0)
                        return false;

                    var view = _db._db.GetViewBetween(lower, upper);

                    byte[]? candidateKey = null;
                    byte[]? candidateValue = null;

                    foreach (var entry in view)
                    {
                        if (candidateKey is not null && candidateKey.AsSpan().SequenceCompareTo(entry.Key) != 0)
                        {
                            // Finished a key group
                            if (candidateValue is not null)
                            {
                                _currentKey = candidateKey;
                                _currentValue = candidateValue;
                                return true;
                            }
                            candidateKey = null;
                            candidateValue = null;
                        }

                        if (entry.Version <= _version)
                        {
                            candidateKey = entry.Key;
                            candidateValue = entry.Value;
                        }
                    }

                    if (candidateValue is not null)
                    {
                        _currentKey = candidateKey;
                        _currentValue = candidateValue;
                        return true;
                    }

                    return false;
                }
            }

            public ReadOnlySpan<byte> CurrentKey => _currentKey ?? ReadOnlySpan<byte>.Empty;
            public ReadOnlySpan<byte> CurrentValue => _currentValue ?? ReadOnlySpan<byte>.Empty;

            public void Dispose()
            {
                _currentKey = null;
                _currentValue = null;
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
                        int batchVersion = _db._currentVersion;

                        foreach ((byte[] key, byte[]? value, WriteFlags _) in _operations)
                        {
                            _db.AddOrReplace((key, batchVersion, value));
                        }

                        _db.WritesCount += _operations.Count;
                    }
                }

                _operations.Dispose();
            }
        }
    }
}
