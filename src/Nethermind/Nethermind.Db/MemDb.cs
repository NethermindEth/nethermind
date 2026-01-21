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
    public class MemDb : IFullDb
    {
        private readonly int _writeDelay; // for testing scenarios
        private readonly int _readDelay; // for testing scenarios
        public long ReadsCount { get; private set; }
        public long WritesCount { get; private set; }

#if ZKVM
        private readonly Dictionary<byte[], byte[]?> _db;
        private readonly Dictionary<byte[], byte[]?>.AlternateLookup<ReadOnlySpan<byte>> _spanDb;
#else
        private readonly ConcurrentDictionary<byte[], byte[]?> _db;
        private readonly ConcurrentDictionary<byte[], byte[]?>.AlternateLookup<ReadOnlySpan<byte>> _spanDb;
#endif


        public MemDb(string name)
            : this(0, 0)
        {
            Name = name;
        }

        public static MemDb CopyFrom(IDb anotherDb)
        {
            MemDb newDb = new MemDb();
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
        {
            _writeDelay = writeDelay;
            _readDelay = readDelay;
#if ZKVM
            _db = new Dictionary<byte[], byte[]>(Bytes.EqualityComparer);
#else
            _db = new ConcurrentDictionary<byte[], byte[]>(Bytes.EqualityComparer);
#endif
            _spanDb = _db.GetAlternateLookup<ReadOnlySpan<byte>>();
        }

        public string Name { get; } = nameof(MemDb);

        public virtual byte[]? this[ReadOnlySpan<byte> key]
        {
            get
            {
                return Get(key);
            }
            set
            {
                Set(key, value);
            }
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
                return keys.Select(k => new KeyValuePair<byte[], byte[]>(k, _db.TryGetValue(k, out var value) ? value : null)).ToArray();
            }
        }

        public virtual void Remove(ReadOnlySpan<byte> key)
        {
#if ZKVM
            _spanDb.Remove(key);
#else
            _spanDb.TryRemove(key, out _);
#endif
        }

        public bool KeyExists(ReadOnlySpan<byte> key) => _spanDb.ContainsKey(key);

        public virtual void Flush(bool onlyWal = false) { }

        public void Clear() => _db.Clear();

        public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) => ordered ? OrderedDb : _db;

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => ordered ? OrderedDb.Select(kvp => kvp.Key) : Keys;

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => ordered ? OrderedDb.Select(kvp => kvp.Value) : Values;

        public virtual IWriteBatch StartWriteBatch() => this.LikeABatch();

        public ICollection<byte[]> Keys => _db.Keys;
        public ICollection<byte[]> Values => _db.Values;

        public int Count => _db.Count;

        public void Dispose() { }

        public bool PreferWriteByArray => true;

        public virtual Span<byte> GetSpan(ReadOnlySpan<byte> key) => Get(key).AsSpan();

        public void DangerousReleaseMemory(in ReadOnlySpan<byte> span) { }

        public virtual byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            if (_readDelay > 0)
            {
                Thread.Sleep(_readDelay);
            }

            ReadsCount++;
            return _spanDb.TryGetValue(key, out byte[] value) ? value : null;
        }

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

        private IEnumerable<KeyValuePair<byte[], byte[]?>> OrderedDb => _db.OrderBy(kvp => kvp.Key, Bytes.Comparer);
    }
}
