// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using static Nethermind.Core.SortedKeyValueStoreExtensions;

namespace Nethermind.Db
{
    public class MemDb : IFullDb, ISortedKeyValueStore
    {
        private readonly int _writeDelay; // for testing scenarios
        private readonly int _readDelay; // for testing scenarios
        public long ReadsCount { get; private set; }
        public long WritesCount { get; private set; }

#if ZK_EVM
        private readonly Dictionary<byte[], byte[]?> _db = new(Bytes.EqualityComparer);
        private readonly Dictionary<byte[], byte[]?>.AlternateLookup<ReadOnlySpan<byte>> _spanDb;
#else
        private readonly ConcurrentDictionary<byte[], byte[]?> _db = new(Bytes.EqualityComparer);
        private readonly ConcurrentDictionary<byte[], byte[]?>.AlternateLookup<ReadOnlySpan<byte>> _spanDb;
#endif

        public MemDb(string name)
            : this(0, 0)
        {
            Name = name;
        }

        public static MemDb CopyFrom(IDb anotherDb)
        {
            MemDb newDb = new();
            if (anotherDb is ISortedKeyValueStore sorted)
            {
                foreach (KeyValuePair<byte[], byte[]?> kv in sorted.GetAll())
                {
                    if (kv.Value is not null)
                    {
                        newDb[kv.Key] = kv.Value;
                    }
                }
            }
            else
            {
                throw new ArgumentException("Database must implement ISortedKeyValueStore", nameof(anotherDb));
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
            _spanDb = _db.GetAlternateLookup<ReadOnlySpan<byte>>();
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

        public virtual void Remove(ReadOnlySpan<byte> key) => _spanDb.TryRemove(key, out _);

        public bool KeyExists(ReadOnlySpan<byte> key) => _spanDb.ContainsKey(key);

        public virtual void Flush(bool onlyWal = false) { }

        public void Clear() => _db.Clear();

        public virtual IWriteBatch StartWriteBatch() => this.LikeABatch();

        public ICollection<byte[]> Keys => _db.Keys;
        public ICollection<byte[]> Values => _db.Values;

        public int Count => _db.Count;

        public void Dispose() { }

        // ISortedKeyValueStore implementation
        public byte[]? FirstKey
        {
            get
            {
                byte[]? min = null;
                foreach (byte[] key in Keys)
                {
                    if (min is null || Bytes.BytesComparer.Compare(key, min) < 0)
                    {
                        min = key;
                    }
                }
                return min;
            }
        }

        public byte[]? LastKey
        {
            get
            {
                byte[]? max = null;
                foreach (byte[] key in Keys)
                {
                    if (max is null || Bytes.BytesComparer.Compare(key, max) > 0)
                    {
                        max = key;
                    }
                }
                return max;
            }
        }

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
        {
            ArrayPoolList<(byte[], byte[]?)> sortedValue = new(1);

            foreach (KeyValuePair<byte[], byte[]?> keyValuePair in _db)
            {
                if (Bytes.BytesComparer.Compare(keyValuePair.Key, firstKeyInclusive) < 0)
                {
                    continue;
                }

                if (lastKeyExclusive.Length > 0 && Bytes.BytesComparer.Compare(keyValuePair.Key, lastKeyExclusive) >= 0)
                {
                    continue;
                }
                sortedValue.Add((keyValuePair.Key, keyValuePair.Value));
            }

            sortedValue.AsSpan().Sort((it1, it2) => Bytes.BytesComparer.Compare(it1.Item1, it2.Item1));
            return new MemDbSortedView(sortedValue);
        }

        private class MemDbSortedView(ArrayPoolList<(byte[], byte[]?)> list) : ISortedView
        {
            private int idx = -1;

            public void Dispose() => list.Dispose();

            public bool StartBefore(ReadOnlySpan<byte> value)
            {
                if (list.Count == 0) return false;

                idx = 0;
                while (idx < list.Count)
                {
                    if (Bytes.BytesComparer.Compare(list[idx].Item1, value) >= 0)
                    {
                        idx--;
                        return true;
                    }
                    idx++;
                }
                idx = list.Count - 1;
                return true;
            }

            public bool MoveNext()
            {
                idx++;
                return idx < list.Count;
            }

            public ReadOnlySpan<byte> CurrentKey => list[idx].Item1;
            public ReadOnlySpan<byte> CurrentValue => list[idx].Item2 ?? ReadOnlySpan<byte>.Empty;
        }

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

        private IEnumerable<KeyValuePair<byte[], byte[]?>> OrderedDb => _db.OrderBy(kvp => kvp.Key, Bytes.Comparer);
    }
}
