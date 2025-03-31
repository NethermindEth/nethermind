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

        private readonly ConcurrentDictionary<byte[], byte[]?> _db;
        private readonly ConcurrentDictionary<byte[], byte[]?>.AlternateLookup<ReadOnlySpan<byte>> _spanDb;

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
            _db = new ConcurrentDictionary<byte[], byte[]>(Bytes.EqualityComparer);
            _spanDb = _db.GetAlternateLookup<ReadOnlySpan<byte>>();
        }

        public string Name { get; }

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
            _spanDb.TryRemove(key, out _);
        }

        public bool KeyExists(ReadOnlySpan<byte> key)
        {
            return _spanDb.ContainsKey(key);
        }

        public IDb Innermost => this;

        public virtual void Flush(bool onlyWal = false) { }

        public void Clear()
        {
            _db.Clear();
        }

        public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) => ordered ? OrderedDb : _db;

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => ordered ? OrderedDb.Select(kvp => kvp.Key) : Keys;

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => ordered ? OrderedDb.Select(kvp => kvp.Value) : Values;

        public virtual IWriteBatch StartWriteBatch()
        {
            return this.LikeABatch();
        }

        public ICollection<byte[]> Keys => _db.Keys;
        public ICollection<byte[]> Values => _db.Values;

        public int Count => _db.Count;

        public static long GetSize() => 0;
        public static long GetCacheSize(bool includeCacheSize) => 0;
        public static long GetIndexSize() => 0;
        public static long GetMemtableSize() => 0;

        public void Dispose()
        {
        }

        public bool PreferWriteByArray => true;

        public virtual Span<byte> GetSpan(ReadOnlySpan<byte> key)
        {
            return Get(key).AsSpan();
        }

        public void DangerousReleaseMemory(in ReadOnlySpan<byte> span)
        {
        }

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
                _spanDb.TryRemove(key, out _);
                return;
            }
            _spanDb[key] = value;
        }

        public IDbMeta.DbMetric GatherMetric(bool includeSharedCache = false)
        {
            return new IDbMeta.DbMetric()
            {
                Size = Count
            };
        }

        private IEnumerable<KeyValuePair<byte[], byte[]?>> OrderedDb => _db.OrderBy(kvp => kvp.Key, Bytes.Comparer);
    }
}
