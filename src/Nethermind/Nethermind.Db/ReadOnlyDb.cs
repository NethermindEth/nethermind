//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

        public byte[]? this[byte[] key]
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
                    if (memValue.Value != null)
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

        public void Remove(byte[] key) { }

        public bool KeyExists(byte[] key)
        {
            return _memDb.KeyExists(key) || _wrappedDb.KeyExists(key);
        }

        public IDb Innermost => _wrappedDb.Innermost;
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
        
        public Span<byte> GetSpan(byte[] key) => this[key].AsSpan();

        public void DangerousReleaseMemory(in Span<byte> span) { }
    }
}
