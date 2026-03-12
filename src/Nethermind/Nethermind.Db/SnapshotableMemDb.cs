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
    public class SnapshotableMemDb(string name = nameof(SnapshotableMemDb), bool neverPrune = false) : IFullDb, ISortedKeyValueStore, IKeyValueStoreWithSnapshot
    {
        private readonly SortedDictionary<(byte[] Key, int Version), byte[]?> _db = new(new KeyVersionComparer());
        private int _currentVersion = 0;
        private readonly HashSet<int> _activeSnapshotVersions = new();
        private readonly object _versionLock = new();
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
                _db[(keyArray, _currentVersion)] = value;
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
            lock (_versionLock)
            {
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    byte[]? value = GetValueAtVersion(key, _currentVersion);
                    if (value is not null)
                    {
                        yield return new KeyValuePair<byte[], byte[]?>(key, value);
                    }
                }
            }
        }

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
        {
            lock (_versionLock)
            {
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    if (GetValueAtVersion(key, _currentVersion) is not null)
                    {
                        yield return key;
                    }
                }
            }
        }

        public IEnumerable<byte[]> GetAllValues(bool ordered = false)
        {
            lock (_versionLock)
            {
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    byte[]? value = GetValueAtVersion(key, _currentVersion);
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
            lock (_versionLock)
            {
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    byte[]? value = GetValueAtVersion(key, version);
                    if (value is not null)
                    {
                        yield return new KeyValuePair<byte[], byte[]?>(key, value);
                    }
                }
            }
        }

        /// <summary>
        /// Gets the value for a key at a specific version (latest version <= requested version).
        /// Returns null if the key doesn't exist or was deleted (tombstone) at that version.
        /// </summary>
        private byte[]? GetValueAtVersion(byte[] key, int version)
        {
            byte[]? result = null;

            foreach (var kvp in _db)
            {
                (byte[] entryKey, int entryVersion) = kvp.Key;
                int cmp = entryKey.AsSpan().SequenceCompareTo(key);

                if (cmp > 0)
                    break;

                if (cmp == 0 && entryVersion <= version)
                {
                    result = kvp.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns all unique keys in sorted order.
        /// </summary>
        private IEnumerable<byte[]> GetAllUniqueKeys()
        {
            byte[]? lastKey = null;
            foreach (var kvp in _db)
            {
                byte[] currentKey = kvp.Key.Key;
                if (lastKey == null || lastKey.AsSpan().SequenceCompareTo(currentKey) != 0)
                {
                    lastKey = currentKey;
                    yield return currentKey;
                }
            }
        }

        private byte[]? FindFirstKeyAtVersion(int version)
        {
            byte[]? candidateKey = null;
            byte[]? candidateValue = null;

            foreach (var kvp in _db)
            {
                (byte[] entryKey, int entryVersion) = kvp.Key;

                if (candidateKey is not null && candidateKey.AsSpan().SequenceCompareTo(entryKey) != 0)
                {
                    if (candidateValue is not null)
                        return candidateKey;
                    candidateKey = null;
                    candidateValue = null;
                }

                if (entryVersion <= version)
                {
                    candidateKey = entryKey;
                    candidateValue = kvp.Value;
                }
            }

            return candidateValue is not null ? candidateKey : null;
        }

        private byte[]? FindLastKeyAtVersion(int version)
        {
            byte[]? lastValidKey = null;
            byte[]? candidateKey = null;
            byte[]? candidateValue = null;

            foreach (var kvp in _db)
            {
                (byte[] entryKey, int entryVersion) = kvp.Key;

                if (candidateKey is not null && candidateKey.AsSpan().SequenceCompareTo(entryKey) != 0)
                {
                    if (candidateValue is not null)
                        lastValidKey = candidateKey;
                    candidateKey = null;
                    candidateValue = null;
                }

                if (entryVersion <= version)
                {
                    candidateKey = entryKey;
                    candidateValue = kvp.Value;
                }
            }

            if (candidateValue is not null)
                lastValidKey = candidateKey;

            return lastValidKey;
        }

        /// <summary>
        /// Removes all versions except the latest for each key.
        /// Called when there are no active snapshots.
        /// </summary>
        private void KeepOnlyLatestVersions()
        {
            using ArrayPoolList<(byte[] Key, int Version)> keysToRemove = new(_db.Count);
            byte[]? prevKey = null;
            int prevVersion = 0;

            foreach (var kvp in _db)
            {
                byte[] currentKey = kvp.Key.Key;
                int currentVersion = kvp.Key.Version;

                if (prevKey is not null && prevKey.AsSpan().SequenceCompareTo(currentKey) == 0)
                {
                    keysToRemove.Add((prevKey, prevVersion));
                }

                prevKey = currentKey;
                prevVersion = currentVersion;
            }

            foreach (var key in keysToRemove)
            {
                _db.Remove(key);
            }
        }

        /// <summary>
        /// Removes old versions per key, keeping the latest version below minVersion
        /// so that snapshots at or after that version can still resolve the key.
        /// </summary>
        private void PruneVersionsOlderThan(int minVersion)
        {
            using ArrayPoolList<(byte[] Key, int Version)> keysToRemove = new(_db.Count);
            byte[]? prevKey = null;
            int prevVersion = 0;

            foreach (var kvp in _db)
            {
                byte[] currentKey = kvp.Key.Key;
                int currentVersion = kvp.Key.Version;

                if (currentVersion < minVersion &&
                    prevKey is not null &&
                    prevKey.AsSpan().SequenceCompareTo(currentKey) == 0)
                {
                    // Same key, older entry — the current entry supersedes the previous one
                    keysToRemove.Add((prevKey, prevVersion));
                }

                prevKey = currentKey;
                prevVersion = currentVersion;
            }

            foreach (var key in keysToRemove)
            {
                _db.Remove(key);
            }
        }

        /// <summary>
        /// Comparer for (byte[] Key, int Version) tuples.
        /// Compares by key first using byte array comparer, then by version.
        /// </summary>
        private sealed class KeyVersionComparer : IComparer<(byte[] Key, int Version)>
        {
            public int Compare((byte[] Key, int Version) x, (byte[] Key, int Version) y)
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
                byte[]? bestKey = null;
                byte[]? bestValue = null;

                lock (_db._versionLock)
                {
                    byte[]? candidateKey = null;
                    byte[]? candidateValue = null;

                    foreach (var kvp in _db._db)
                    {
                        (byte[] entryKey, int entryVersion) = kvp.Key;

                        if (entryKey.AsSpan().SequenceCompareTo(_firstKey) < 0) continue;
                        if (entryKey.AsSpan().SequenceCompareTo(_lastKey) >= 0) break;
                        if (entryKey.AsSpan().SequenceCompareTo(keyArray) >= 0) break;

                        if (candidateKey is not null && candidateKey.AsSpan().SequenceCompareTo(entryKey) != 0)
                        {
                            if (candidateValue is not null)
                            {
                                bestKey = candidateKey;
                                bestValue = candidateValue;
                            }
                            candidateKey = null;
                            candidateValue = null;
                        }

                        if (entryVersion <= _version)
                        {
                            candidateKey = entryKey;
                            candidateValue = kvp.Value;
                        }
                    }

                    if (candidateValue is not null)
                    {
                        bestKey = candidateKey;
                        bestValue = candidateValue;
                    }
                }

                _currentKey = bestKey;
                _currentValue = bestValue;
                return bestKey is not null;
            }

            public bool MoveNext()
            {
                lock (_db._versionLock)
                {
                    byte[]? candidateKey = null;
                    byte[]? candidateValue = null;

                    foreach (var kvp in _db._db)
                    {
                        (byte[] entryKey, int entryVersion) = kvp.Key;

                        if (entryKey.AsSpan().SequenceCompareTo(_firstKey) < 0) continue;
                        if (entryKey.AsSpan().SequenceCompareTo(_lastKey) >= 0) break;

                        // Skip entries at or before current position
                        if (_currentKey is not null && entryKey.AsSpan().SequenceCompareTo(_currentKey) <= 0)
                            continue;

                        if (candidateKey is not null && candidateKey.AsSpan().SequenceCompareTo(entryKey) != 0)
                        {
                            if (candidateValue is not null)
                            {
                                _currentKey = candidateKey;
                                _currentValue = candidateValue;
                                return true;
                            }
                            candidateKey = null;
                            candidateValue = null;
                        }

                        if (entryVersion <= _version)
                        {
                            candidateKey = entryKey;
                            candidateValue = kvp.Value;
                        }
                    }

                    if (candidateKey is not null && candidateValue is not null)
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
                            _db._db[(key, batchVersion)] = value;
                        }

                        _db.WritesCount += _operations.Count;
                    }
                }

                _operations.Dispose();
            }
        }
    }
}
