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
    public class MemDb : IFullDb
    {
        private readonly int _writeDelay; // for testing scenarios
        private readonly int _readDelay; // for testing scenarios
        public long ReadsCount { get; private set; }
        public long WritesCount { get; private set; }

        private readonly SortedSet<byte[]>? _sortedKeys;
        private readonly SpanConcurrentDictionary<byte, byte[]?> _db;

        public MemDb(string name, bool sorted = false)
            : this(0, 0, sorted)
        {
            Name = name;
        }

        public MemDb(bool sorted = false) : this(0, 0, sorted)
        {
            Name = "";
        }

        public MemDb(int writeDelay, int readDelay, bool sorted = false)
        {
            Name = "";
            _writeDelay = writeDelay;
            _readDelay = readDelay;
            _db = new SpanConcurrentDictionary<byte, byte[]>(Bytes.SpanEqualityComparer);
            if (sorted) _sortedKeys = new SortedSet<byte[]>(Bytes.Comparer);
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
            _db.TryRemove(key, out _);
            _sortedKeys?.Remove(key.ToArray());
        }

        public bool KeyExists(ReadOnlySpan<byte> key)
        {
            return _db.ContainsKey(key);
        }

        public IDb Innermost => this;

        public virtual void Flush()
        {
        }

        public void Clear()
        {
            _db.Clear();
            _sortedKeys?.Clear();
        }

        public virtual IEnumerable<KeyValuePair<byte[], byte[]?>> GetIterator()
        {
            if (_sortedKeys is null) throw new ArgumentException($"cannot get ordered data");
            using SortedSet<byte[]>.Enumerator keyEnumerator = _sortedKeys.GetEnumerator();
            while (keyEnumerator.MoveNext())
                yield return new KeyValuePair<byte[], byte[]>(keyEnumerator.Current, Get(keyEnumerator.Current));
        }

        public virtual IEnumerable<KeyValuePair<byte[], byte[]?>> GetIterator(byte[] start)
        {
            if (_sortedKeys is null) throw new ArgumentException($"cannot get ordered data");
            return GetIterator(start, _sortedKeys.Max);
        }

        public virtual IEnumerable<KeyValuePair<byte[], byte[]?>> GetIterator(byte[] start, byte[] end)
        {
            if (_sortedKeys is null) throw new ArgumentException($"cannot get ordered data");

            if (Bytes.BytesComparer.Compare(start, end) > 0) yield break;

            using SortedSet<byte[]>.Enumerator keyEnumerator = _sortedKeys
                .GetViewBetween(start, end)
                .GetEnumerator();

            while (keyEnumerator.MoveNext())
                yield return new KeyValuePair<byte[], byte[]>(keyEnumerator.Current, Get(keyEnumerator.Current));
        }

        public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) =>
            ordered ? GetAllSortedIterator() : _db;

        private IEnumerable<KeyValuePair<byte[], byte[]?>> GetAllSortedIterator()
        {
            if (_sortedKeys is null) throw new ArgumentException($"cannot get ordered data");
            using SortedSet<byte[]>.Enumerator iterator = _sortedKeys.GetEnumerator();
            while (iterator.MoveNext())
            {
                yield return new KeyValuePair<byte[], byte[]?>(iterator.Current, _db[iterator.Current!]);
            }
        }

        private IEnumerable<byte[]> GetAllValuesSortedIterator()
        {
            if (_sortedKeys is null) throw new ArgumentException($"cannot get ordered data");
            using SortedSet<byte[]>.Enumerator iterator = _sortedKeys.GetEnumerator();
            while (iterator.MoveNext())
            {
                yield return _db[iterator.Current!];
            }
        }

        public IEnumerable<byte[]> GetAllValues(bool ordered = false)
        {
            return ordered ? GetAllValuesSortedIterator() : Values;
        }


        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => Keys;

        public virtual IWriteBatch StartWriteBatch()
        {
            return this.LikeABatch();
        }

        public ICollection<byte[]> Keys => _db.Keys;
        public ICollection<byte[]> Values => _db.Values;

        public int Count => _db.Count;

        public long GetSize() => 0;
        public long GetCacheSize(bool includeCacheSize) => 0;
        public long GetIndexSize() => 0;
        public long GetMemtableSize() => 0;

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
            return _db.TryGetValue(key, out byte[] value) ? value : null;
        }

        public virtual void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (_writeDelay > 0)
            {
                Thread.Sleep(_writeDelay);
            }

            WritesCount++;
            _db[key] = value;
            _sortedKeys?.Add(key.ToArray());
        }
    }
}
