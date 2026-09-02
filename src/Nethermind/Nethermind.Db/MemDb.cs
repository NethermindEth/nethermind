// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    public partial class MemDb : IFullDb, IRangeRemovableKeyValueStore
    {
        private readonly int _writeDelay; // for testing scenarios
        private readonly int _readDelay; // for testing scenarios
        public long ReadsCount { get; private set; }
        public long WritesCount { get; private set; }

        public MemDb(string name)
            : this(0, 0) => Name = name;

        /// <param name="capacity">The expected number of entries; presizing avoids rehashing during bulk loads.</param>
        public MemDb(int capacity)
            : this(0, 0, capacity)
        {
        }

        public static MemDb CopyFrom(IDb anotherDb)
        {
            MemDb newDb = new();
            foreach (KeyValuePair<byte[], byte[]> kv in anotherDb.GetAll())
            {
                newDb[kv.Key] = kv.Value;
            }

            return newDb;
        }

        public MemDb() : this(0, 0)
        {
        }

        public MemDb(int writeDelay, int readDelay)
            : this(writeDelay, readDelay, capacity: 0)
        {
        }

        public string Name { get; } = nameof(MemDb);

        public virtual byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Get(key);
            set => Set(key, value);
        }

        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
        {
            get
            {
                if (_readDelay > 0)
                {
                    Thread.Sleep(_readDelay);
                }

                ReadsCount += keys.Length;
                return keys.Select(k => new KeyValuePair<byte[], byte[]>(k, _db.GetValueOrDefault(k))).ToArray();
            }
        }

        public bool KeyExists(ReadOnlySpan<byte> key) => _spanDb.ContainsKey(key);

        public virtual void Flush(bool onlyWal = false) { }

        public void Clear() => _db.Clear();

        /// <summary>Half-open, matching the RocksDB range tombstone this stands in for in tests.</summary>
        public void RemoveRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
        {
            foreach (byte[] key in Keys)
            {
                if (Bytes.BytesComparer.Compare(key, firstKeyInclusive) >= 0 && Bytes.BytesComparer.Compare(key, lastKeyExclusive) < 0)
                {
                    Remove(key);
                }
            }
        }

        // Removing already returned the memory; there is no deferred storage to give back.
        public void ReclaimRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) { }

        public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) => ordered ? OrderedDb : _db;

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => ordered ? OrderedDb.Select(kvp => kvp.Key) : Keys;

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => ordered ? OrderedDb.Select(kvp => kvp.Value) : Values;

        public virtual IWriteBatch StartWriteBatch() => this.LikeABatch();

        public ICollection<byte[]> Keys => _db.Select(static kvp => kvp.Key).ToArray();
        public ICollection<byte[]> Values => _db.Select(static kvp => kvp.Value).ToArray()!;

        public int Count => _db.Count;

        public void Dispose() { }

        public bool PreferWriteByArray => true;

        public unsafe void DangerousReleaseMemory(in ReadOnlySpan<byte> span) { }

        public virtual byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            if (_readDelay > 0)
            {
                Thread.Sleep(_readDelay);
            }

            ReadsCount++;
            return _spanDb.TryGetValue(key, out byte[] value) ? value : null;
        }

        public unsafe Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
            => Get(key).AsSpan();

        public virtual void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (_writeDelay > 0)
            {
                Thread.Sleep(_writeDelay);
            }

            WritesCount++;
            if (value is null)
            {
                Remove(key);
                return;
            }
            _spanDb[key] = value;
        }

        public virtual IDbMeta.DbMetric GatherMetric() => new() { Size = Count };

        public long EstimatedCount => Count;

        private IEnumerable<KeyValuePair<byte[], byte[]?>> OrderedDb => _db.OrderBy(kvp => kvp.Key, Bytes.Comparer);
    }
}
