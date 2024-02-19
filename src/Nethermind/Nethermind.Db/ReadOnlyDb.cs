// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public class ReadOnlyDb : IReadOnlyDb
    {
        private readonly MemDb _memDb = new();

        private readonly IDb _wrappedDb;
        private readonly bool _createInMemWriteStore;

        public ReadOnlyDb(IDb wrappedDb, bool createInMemWriteStore)
        {
            _wrappedDb = wrappedDb;
            _createInMemWriteStore = createInMemWriteStore;
        }

        public void Dispose()
        {
            _memDb.Dispose();
        }

        public string Name { get; } = "ReadOnlyDb";

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return _memDb.Get(key, flags) ?? _wrappedDb.Get(key, flags);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (!_createInMemWriteStore)
            {
                throw new InvalidOperationException($"This {nameof(ReadOnlyDb)} did not expect any writes.");
            }

            _memDb.Set(key, value, flags);
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys]
        {
            get
            {
                var result = _wrappedDb[keys];
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

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => _memDb.GetAll();

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => _memDb.GetAllKeys();

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => _memDb.GetAllValues();

        public IWriteBatch StartWriteBatch()
        {
            return this.LikeABatch();
        }

        public IDbMeta.DbMetric GatherMetric(bool includeSharedCache = false) => _wrappedDb.GatherMetric(includeSharedCache);

        public void Remove(ReadOnlySpan<byte> key) { }

        public bool KeyExists(ReadOnlySpan<byte> key)
        {
            return _memDb.KeyExists(key) || _wrappedDb.KeyExists(key);
        }

        public void Flush()
        {
            _wrappedDb.Flush();
            _memDb.Flush();
        }

        public void Clear() { throw new InvalidOperationException(); }

        public virtual void ClearTempChanges()
        {
            _memDb.Clear();
        }

        public Span<byte> GetSpan(ReadOnlySpan<byte> key) => _memDb.Get(key).AsSpan();
        public void PutSpan(ReadOnlySpan<byte> keyBytes, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None)
        {
            if (!_createInMemWriteStore)
            {
                throw new InvalidOperationException($"This {nameof(ReadOnlyDb)} did not expect any writes.");
            }

            _memDb.Set(keyBytes, value.ToArray(), writeFlags);
        }

        public void DangerousReleaseMemory(in ReadOnlySpan<byte> span) { }

        public bool PreferWriteByArray => true; // Because of memdb buffer
    }
}
