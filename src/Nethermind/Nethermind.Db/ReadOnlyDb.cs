// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public class ReadOnlyDb : IReadOnlyDb, IDbWithSpan
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

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get => _memDb[key] ?? _wrappedDb[key];
            set
            {
                if (!_createInMemWriteStore)
                {
                    throw new InvalidOperationException($"This {nameof(ReadOnlyDb)} did not expect any writes.");
                }

                _memDb[key] = value;
            }
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

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => _memDb.GetAllValues();

        public IBatch StartBatch()
        {
            return this.LikeABatch();
        }

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

        public Span<byte> GetSpan(ReadOnlySpan<byte> key) => this[key].AsSpan();
        public void PutSpan(ReadOnlySpan<byte> keyBytes, ReadOnlySpan<byte> value)
        {
            if (!_createInMemWriteStore)
            {
                throw new InvalidOperationException($"This {nameof(ReadOnlyDb)} did not expect any writes.");
            }

            _memDb[keyBytes] = value.ToArray();
        }

        public void DangerousReleaseMemory(in Span<byte> span) { }

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey) { }
    }
}
