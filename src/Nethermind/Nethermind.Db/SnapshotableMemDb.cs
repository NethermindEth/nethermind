// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    /// <summary>
    /// In-memory database with MVCC-based snapshot support.
    /// Uses Multi-Version Concurrency Control to enable O(1) snapshot creation.
    /// </summary>
    public class SnapshotableMemDb(string name = nameof(SnapshotableMemDb), bool neverPrune = false) : IFullDb, ISortedKeyValueStore, IKeyValueStoreWithSnapshot, IRangeRemovableKeyValueStore
    {
        private SortedSet<(byte[] Key, int Version, byte[]? Value)> _db = [with(new EntryComparer())];
        private readonly EntryComparer _entryComparer = new();
        private int _currentVersion = 0;
        private readonly SortedList<int, int> _activeSnapshotVersions = [];
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

                PruneKeyHistory(keyArray, _currentVersion, value);
            }
        }

        public void Remove(ReadOnlySpan<byte> key) => Set(key, null);

        /// <summary>Half-open, matching the RocksDB range tombstone this stands in for in tests. Goes through
        /// <see cref="Set"/> so the versioning that snapshots rely on sees each removal.</summary>
        public void RemoveRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
        {
            foreach (byte[] key in Keys)
            {
                if (Bytes.BytesComparer.Compare(key, firstKeyInclusive) >= 0 && Bytes.BytesComparer.Compare(key, lastKeyExclusive) < 0)
                {
                    Set(key, null);
                }
            }
        }

        // Removing already returned the memory; there is no deferred storage to give back.
        public void ReclaimRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) { }

        public bool KeyExists(ReadOnlySpan<byte> key)
        {
            byte[] keyArray = key.ToArray();
            lock (_versionLock)
            {
                return GetValueAtVersion(keyArray, _currentVersion) is not null;
            }
        }

        public IWriteBatch StartWriteBatch() => new MemDbWriteBatch(this);

        /// <inheritdoc/>
        /// <remarks>Recovery after allocation or stack exhaustion during commit is unsupported.</remarks>
        public IAtomicWriteBatch StartAtomicWriteBatch() => new AtomicMemDbWriteBatch(this);

        internal sealed class AtomicMemDbWriteBatch(SnapshotableMemDb db) : IAtomicWriteBatch
        {
            private readonly List<(byte[] Key, byte[]? Value)> _operations = [];
            private bool _disposed;

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _operations.Add((key.ToArray(), value?.ToArray()));
            }

            public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _operations.Add((key.ToArray(), value.ToArray()));
            }
            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => PutSpan(key, value, flags);

            public void Clear()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _operations.Clear();
            }

            public void Dispose()
            {
                _disposed = true;
                _operations.Clear();
            }

            public void Commit()
            {
                try
                {
                    CommitAll([this]);
                }
                finally
                {
                    Dispose();
                }
            }

            internal static void CommitAll(IReadOnlyList<AtomicMemDbWriteBatch> batches)
            {
                LockAndCommit(0);

                void LockAndCommit(int index)
                {
                    if (index < batches.Count)
                    {
                        AtomicMemDbWriteBatch batch = batches[index];
                        ObjectDisposedException.ThrowIf(batch._disposed, batch);
                        lock (batch.Database._versionLock) LockAndCommit(index + 1);
                        return;
                    }

                    int[] versions = new int[batches.Count];
                    for (int i = 0; i < batches.Count; i++)
                    {
                        versions[i] = checked(batches[i].Database._currentVersion + 1);
                    }

                    PreparedWrites[] prepared = new PreparedWrites[batches.Count];
                    int entryCount = 0;
                    for (int i = 0; i < batches.Count; i++)
                    {
                        prepared[i] = batches[i].Prepare(versions[i]);
                        entryCount = checked(entryCount + prepared[i].Entries.Length);
                    }

                    (SnapshotableMemDb Database, (byte[] Key, int Version, byte[]? Value) Entry)[] inserted =
                        new (SnapshotableMemDb, (byte[], int, byte[]?))[entryCount];
                    int insertedCount = 0;
                    try
                    {
                        foreach (PreparedWrites writes in prepared)
                        {
                            foreach ((byte[] Key, int Version, byte[]? Value) entry in writes.Entries)
                            {
                                if (writes.Database._db.Add(entry)) inserted[insertedCount++] = (writes.Database, entry);
                            }
                        }
                    }
                    // Allocation failure can interrupt SortedSet rebalancing.
                    catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                    {
                        for (int i = insertedCount - 1; i >= 0; i--)
                        {
                            inserted[i].Database._db.Remove(inserted[i].Entry);
                        }
                        throw;
                    }

                    foreach (PreparedWrites writes in prepared)
                    {
                        foreach ((byte[] Key, int Version, byte[]? Value) entry in writes.Removals) writes.Database._db.Remove(entry);
                    }

                    foreach (PreparedWrites writes in prepared)
                    {
                        writes.Database._currentVersion = writes.Version;
                        writes.Database.WritesCount += writes.OperationCount;
                    }
                    foreach (AtomicMemDbWriteBatch batch in batches) batch.Dispose();
                }
            }

            private PreparedWrites Prepare(int version)
            {
                SnapshotableMemDb database = Database;
                SortedDictionary<byte[], byte[]?> values = [with(Bytes.Comparer)];
                foreach ((byte[] key, byte[]? value) in _operations) values[key] = value;
                (byte[] Key, int Version, byte[]? Value)[] entries = new (byte[], int, byte[]?)[values.Count];
                List<(byte[] Key, int Version, byte[]? Value)> removals = [];
                int[] snapshots = database._neverPrune ? [] : new int[database._activeSnapshotVersions.Count];
                if (!database._neverPrune)
                {
                    database._activeSnapshotVersions.Keys.CopyTo(snapshots, 0);
                    Array.Sort(snapshots);
                    Array.Reverse(snapshots);
                }
                int index = 0;
                foreach ((byte[] key, byte[]? value) in values)
                {
                    (byte[] Key, int Version, byte[]? Value) entry = (key, version, value);
                    entries[index++] = entry;
                    if (database._neverPrune) continue;

                    List<(byte[] Key, int Version, byte[]? Value)> history = [entry];
                    foreach ((byte[] Key, int Version, byte[]? Value) old in database._db.GetViewBetween((key, 0, null), (key, int.MaxValue, null)).Reverse())
                    {
                        history.Add(old);
                    }
                    List<(byte[] Key, int Version, byte[]? Value)> tombstones = [];
                    int snapshotIndex = 0;
                    bool hasValue = false;
                    for (int i = 0; i < history.Count; i++)
                    {
                        (byte[] Key, int Version, byte[]? Value) candidate = history[i];
                        bool keep = RetainVersion(i == 0, candidate.Version, snapshots, ref snapshotIndex);
                        if (!keep) removals.Add(candidate);
                        else if (candidate.Value is null) tombstones.Add(candidate);
                        else hasValue = true;
                    }
                    if (!hasValue) removals.AddRange(tombstones);
                }
                return new PreparedWrites(database, version, _operations.Count, entries, removals.ToArray());
            }

            private readonly record struct PreparedWrites(
                SnapshotableMemDb Database,
                int Version,
                int OperationCount,
                (byte[] Key, int Version, byte[]? Value)[] Entries,
                (byte[] Key, int Version, byte[]? Value)[] Removals);

            private SnapshotableMemDb Database => db;
        }

        public void Flush(bool onlyWal = false) { }

        public void Clear()
        {
            lock (_versionLock)
            {
                _db.Clear();
            }
        }

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
        {
            List<KeyValuePair<byte[], byte[]>> result;
            lock (_versionLock)
            {
                result = [];
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    byte[]? value = GetValueAtVersion(key, _currentVersion);
                    if (value is not null)
                    {
                        result.Add(new KeyValuePair<byte[], byte[]>(key, value));
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
                result = [];
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
                result = [];
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
                    return GetAllValues().ToArray();
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

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
        {
            int version;
            lock (_versionLock)
            {
                version = _currentVersion;
                AddActiveVersion(version);
            }
            return new MemDbSortedView(this, version, firstKeyInclusive.ToArray(), lastKeyExclusive.ToArray());
        }

        // IKeyValueStoreWithSnapshot implementation
        public IKeyValueStoreSnapshot CreateSnapshot()
        {
            lock (_versionLock)
            {
                int snapshotVersion = _currentVersion;
                AddActiveVersion(snapshotVersion);
                return new MemDbSnapshot(this, snapshotVersion);
            }
        }

        private void AddActiveVersion(int version) =>
            _activeSnapshotVersions[version] = _activeSnapshotVersions.GetValueOrDefault(version) + 1;

        internal void OnSnapshotDisposed(int version)
        {
            lock (_versionLock)
            {
                if (--_activeSnapshotVersions[version] == 0)
                {
                    _activeSnapshotVersions.Remove(version);
                }

                // Skip pruning if disabled
                if (_neverPrune)
                {
                    return;
                }

                PruneObsoleteVersions(_db);
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

        internal IEnumerable<KeyValuePair<byte[], byte[]>> GetAllAtVersion(int version)
        {
            List<KeyValuePair<byte[], byte[]>> result;
            lock (_versionLock)
            {
                result = [];
                foreach (byte[] key in GetAllUniqueKeys())
                {
                    byte[]? value = GetValueAtVersion(key, version);
                    if (value is not null)
                    {
                        result.Add(new KeyValuePair<byte[], byte[]>(key, value));
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
            (byte[] key, int, byte[]?) lower = (key, 0, null);
            (byte[] key, int version, byte[]?) upper = (key, version, null);

            if (_entryComparer.Compare(lower, upper) > 0)
                return null;

            SortedSet<(byte[] Key, int Version, byte[]? Value)> view = _db.GetViewBetween(lower, upper);
            (byte[] Key, int Version, byte[]? Value) max = view.Max;
            return max.Key is not null ? max.Value : null;
        }

        /// <summary>
        /// Returns all unique keys in sorted order.
        /// </summary>
        private IEnumerable<byte[]> GetAllUniqueKeys()
        {
            byte[]? lastKey = null;
            foreach ((byte[] Key, int Version, byte[]? Value) entry in _db)
            {
                if (lastKey is null || lastKey.AsSpan().SequenceCompareTo(entry.Key) != 0)
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
            foreach ((byte[] Key, int Version, byte[]? Value) entry in _db.Reverse())
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

        private void PruneKeyHistory(byte[] key, int version, byte[]? value)
        {
            if (_neverPrune) return;
            if (_activeSnapshotVersions.Count == 0)
            {
                RemovePreviousVersions(key, version);
                if (value is null) _db.Remove((key, version, null));
                return;
            }
            PruneObsoleteVersions(_db.GetViewBetween((key, 0, null), (key, int.MaxValue, null)));
        }

        private void PruneObsoleteVersions(SortedSet<(byte[] Key, int Version, byte[]? Value)> entries)
        {
            if (_neverPrune) return;
            int[] snapshots = _activeSnapshotVersions.Keys.OrderDescending().ToArray();
            using ArrayPoolList<(byte[] Key, int Version, byte[]? Value)> toRemove = new(entries.Count);
            using ArrayPoolList<(byte[] Key, int Version, byte[]? Value)> tombstones = new(16);
            byte[]? previousKey = null;
            int snapshotIndex = 0;
            bool hasValue = false;
            foreach ((byte[] Key, int Version, byte[]? Value) entry in entries.Reverse())
            {
                bool latest = previousKey is null || !previousKey.AsSpan().SequenceEqual(entry.Key);
                if (latest)
                {
                    if (!hasValue) foreach ((byte[] Key, int Version, byte[]? Value) tombstone in tombstones) toRemove.Add(tombstone);
                    tombstones.Clear();
                    hasValue = false;
                    previousKey = entry.Key;
                    snapshotIndex = 0;
                }
                bool keep = RetainVersion(latest, entry.Version, snapshots, ref snapshotIndex);
                if (!keep) toRemove.Add(entry);
                else if (entry.Value is null) tombstones.Add(entry);
                else hasValue = true;
            }
            if (!hasValue) foreach ((byte[] Key, int Version, byte[]? Value) tombstone in tombstones) toRemove.Add(tombstone);
            foreach ((byte[] Key, int Version, byte[]? Value) entry in toRemove) entries.Remove(entry);
        }

        private static bool RetainVersion(bool latest, int version, int[] snapshots, ref int snapshotIndex)
        {
            bool keep = latest;
            while (snapshotIndex < snapshots.Length && version <= snapshots[snapshotIndex])
            {
                keep = true;
                snapshotIndex++;
            }
            return keep;
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
            (byte[] key, int, byte[]?) lower = (key, 0, null);
            (byte[] key, int, byte[]?) upper = (key, currentVersion - 1, null);

            if (_entryComparer.Compare(lower, upper) > 0)
                return;

            SortedSet<(byte[] Key, int Version, byte[]? Value)> view = _db.GetViewBetween(lower, upper);
            // Materialize before removing to avoid modifying during enumeration
            List<(byte[] Key, int Version, byte[]? Value)> toRemove = [.. view];
            foreach ((byte[] Key, int Version, byte[]? Value) entry in toRemove)
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
            private bool _disposed;

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => _db.GetAtVersion(key, _snapshotVersion);

            public unsafe Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
                => Get(key, flags).AsSpan();

            public bool KeyExists(ReadOnlySpan<byte> key) => Get(key) is not null;

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

            public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
            {
                lock (_db._versionLock)
                {
                    _db.AddActiveVersion(_snapshotVersion);
                }
                return new MemDbSortedView(_db, _snapshotVersion, firstKeyInclusive.ToArray(), lastKeyExclusive.ToArray());
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, true)) return;
                _db.OnSnapshotDisposed(_snapshotVersion);
            }
        }

        /// <summary>
        /// Sorted view iterator for a range of keys.
        /// Uses GetViewBetween for efficient range queries instead of scanning from the beginning.
        /// </summary>
        private sealed class MemDbSortedView(SnapshotableMemDb db, int version, byte[] firstKey, byte[] lastKey) : ISeekableSortedView
        {
            private readonly SnapshotableMemDb _db = db;
            private readonly int _version = version;
            private readonly byte[] _firstKey = firstKey;
            private readonly byte[] _lastKey = lastKey;
            private byte[]? _currentKey;
            private byte[]? _currentValue;
            private bool _disposed;

            public bool StartBefore(ReadOnlySpan<byte> key)
            {
                byte[] keyArray = key.ToArray();
                lock (_db._versionLock)
                {
                    (byte[] _firstKey, int, byte[]?) lower = (_firstKey, 0, null);
                    (byte[] keyArray, int, byte[]?) upper = (keyArray, 0, null);

                    if (_db._entryComparer.Compare(lower, upper) > 0)
                    {
                        // key is before _firstKey: position before start so MoveNext yields first element
                        _currentKey = null;
                        _currentValue = null;
                        return true;
                    }

                    SortedSet<(byte[] Key, int Version, byte[]? Value)> view = _db._db.GetViewBetween(lower, upper);

                    byte[]? bestKey = null;
                    byte[]? bestValue = null;
                    byte[]? candidateKey = null;
                    byte[]? candidateValue = null;

                    foreach ((byte[] Key, int Version, byte[]? Value) entry in view)
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

            public bool MoveNext() => MoveToFirstFrom(_currentKey is not null
                ? (_currentKey, int.MaxValue, null)
                : (_firstKey, 0, null));

            public bool SeekTo(ReadOnlySpan<byte> key) => MoveToFirstFrom(key.SequenceCompareTo(_firstKey) < 0
                ? (_firstKey, 0, null)
                : (key.ToArray(), 0, null));

            private bool MoveToFirstFrom((byte[], int, byte[]?) lower)
            {
                lock (_db._versionLock)
                {
                    (byte[] _lastKey, int, byte[]?) upper = (_lastKey, 0, null);

                    if (_db._entryComparer.Compare(lower, upper) > 0)
                        return false;

                    SortedSet<(byte[] Key, int Version, byte[]? Value)> view = _db._db.GetViewBetween(lower, upper);

                    byte[]? candidateKey = null;
                    byte[]? candidateValue = null;

                    foreach ((byte[] Key, int Version, byte[]? Value) entry in view)
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
                if (Interlocked.Exchange(ref _disposed, true)) return;
                _currentKey = null;
                _currentValue = null;
                _db.OnSnapshotDisposed(_version);
            }
        }

        /// <summary>
        /// Write batch that collects all operations and commits them atomically with a single lock.
        /// </summary>
        private sealed class MemDbWriteBatch(SnapshotableMemDb db) : IWriteBatch
        {
            private readonly SnapshotableMemDb _db = db;
            private readonly ArrayPoolList<(byte[] Key, byte[]? Value, WriteFlags Flags)> _operations = [with(16)];
            private bool _disposed;

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(MemDbWriteBatch));
                _operations.Add((key.ToArray(), value, flags));
            }

            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
                // For in-memory database, merge is the same as set
                Set(key, value.ToArray(), flags);

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
                            _db.PruneKeyHistory(key, batchVersion, value);
                        }

                        _db.WritesCount += _operations.Count;
                    }
                }

                _operations.Dispose();
            }
        }
    }
}
