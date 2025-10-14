// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Db
{
    public class ReadOnlyDb(IDb wrappedDb, bool createInMemWriteStore) : IReadOnlyDb
    {
        private readonly MemDb _memDb = new();

        public void Dispose()
        {
            _memDb.Dispose();
        }

        public string Name { get => wrappedDb.Name; }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return _memDb.Get(key, flags) ?? wrappedDb.Get(key, flags);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (!createInMemWriteStore)
            {
                throw new InvalidOperationException($"This {nameof(ReadOnlyDb)} did not expect any writes.");
            }

            _memDb.Set(key, value, flags);
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys]
        {
            get
            {
                var result = wrappedDb[keys];
                var memResult = _memDb[keys];
                for (int i = 0; i < memResult.Length; i++)
                {
                    var memValue = memResult[i];
                    if (memValue.Value is not null)
                    {
                        result[i] = memValue;
                    }
                }

                return result;
            }
        }

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => _memDb.GetAll().Union(wrappedDb.GetAll());

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => _memDb.GetAllKeys().Union(wrappedDb.GetAllKeys());

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => _memDb.GetAllValues().Union(wrappedDb.GetAllValues());

        public IWriteBatch StartWriteBatch() => this.LikeABatch();

        public IDbMeta.DbMetric GatherMetric(bool includeSharedCache = false) => wrappedDb.GatherMetric(includeSharedCache);

        public void Remove(ReadOnlySpan<byte> key) { }

        public bool KeyExists(ReadOnlySpan<byte> key) => _memDb.KeyExists(key) || wrappedDb.KeyExists(key);

        public void Flush(bool onlyWal) { }

        public void Clear() => throw new InvalidOperationException();

        public virtual void ClearTempChanges() => _memDb.Clear();

        public Span<byte> GetSpan(ReadOnlySpan<byte> key) => Get(key).AsSpan();
        public void PutSpan(ReadOnlySpan<byte> keyBytes, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None)
        {
            if (!createInMemWriteStore)
            {
                throw new InvalidOperationException($"This {nameof(ReadOnlyDb)} did not expect any writes.");
            }

            _memDb.Set(keyBytes, value.ToArray(), writeFlags);
        }

        public void DangerousReleaseMemory(in ReadOnlySpan<byte> span) { }

        public bool PreferWriteByArray => true; // Because of memdb buffer

        public IIterator GetIterator(bool isTailing = false)
        {
            throw new NotSupportedException("Iteration is not supported by this implementation.");
        }

        public IIterator GetIterator(ref IteratorOptions options)
        {
            throw new NotSupportedException("Iteration is not supported by this implementation.");
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            _memDb.Merge(key, value, flags);
        }
    }
}
