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
using System.Linq;
using Nethermind.Core;
using RocksDbSharp;

namespace Nethermind.Db.Rocks
{
    public class ColumnDb : IDbWithSpan
    {
        private readonly RocksDb _rocksDb;
        private readonly DbOnTheRocks _mainDb;
        private readonly ColumnFamilyHandle _columnFamily;

        public ColumnDb(RocksDb rocksDb, DbOnTheRocks mainDb, string name)
        {
            _rocksDb = rocksDb;
            _mainDb = mainDb;
            _columnFamily = _rocksDb.GetColumnFamily(name);
            Name = name;
        }

        public void Dispose() { GC.SuppressFinalize(this); }

        public string Name { get; }

        public byte[]? this[byte[] key]
        {
            get
            {
                UpdateReadMetrics();
                return _rocksDb.Get(key, _columnFamily);
            }
            set
            {
                UpdateWriteMetrics();
                if (value is null)
                {
                    _rocksDb.Remove(key, _columnFamily, _mainDb.WriteOptions);
                }
                else
                {
                    _rocksDb.Put(key, value, _columnFamily, _mainDb.WriteOptions);
                }
            }
        }

        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] =>
            _rocksDb.MultiGet(keys, keys.Select(k => _columnFamily).ToArray());

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
        {
            using Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
            return _mainDb.GetAllCore(iterator);
        }

        public IEnumerable<byte[]> GetAllValues(bool ordered = false)
        {
            Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
            return _mainDb.GetAllValuesCore(iterator);
        }

        public IBatch StartBatch()
        {
            return new ColumnsDbBatch(this, (DbOnTheRocks.RocksDbBatch)_mainDb.StartBatch());
        }

        private class ColumnsDbBatch : IBatch
        {
            private readonly ColumnDb _columnDb;
            private readonly DbOnTheRocks.RocksDbBatch _underlyingBatch;

            public ColumnsDbBatch(ColumnDb columnDb, DbOnTheRocks.RocksDbBatch underlyingBatch)
            {
                _columnDb = columnDb;
                _underlyingBatch = underlyingBatch;
            }

            public void Dispose()
            {
                _underlyingBatch.Dispose();
            }

            public byte[]? this[byte[] key]
            {
                get => _underlyingBatch[key];
                set
                {
                    if (value is null)
                    {
                        _underlyingBatch._rocksBatch.Delete(key, _columnDb._columnFamily);
                    }
                    else
                    {
                        _underlyingBatch._rocksBatch.Put(key, value, _columnDb._columnFamily);
                    }
                }
            }
        }

        public void Remove(byte[] key)
        {
            // TODO: this does not participate in batching?
            _rocksDb.Remove(key, _columnFamily, _mainDb.WriteOptions);
        }

        public bool KeyExists(byte[] key) => _rocksDb.Get(key, _columnFamily) != null;

        public IDb Innermost => _mainDb.Innermost;

        public void Flush()
        {
            _mainDb.Flush();
        }

        /// <summary>
        /// Not sure how to handle delete of the columns DB
        /// </summary>
        /// <exception cref="NotSupportedException"></exception>
        public void Clear() { throw new NotSupportedException(); }

        private void UpdateWriteMetrics() => _mainDb.UpdateWriteMetrics();

        private void UpdateReadMetrics() => _mainDb.UpdateReadMetrics();

        public Span<byte> GetSpan(byte[] key) => _rocksDb.GetSpan(key, _columnFamily);

        public void DangerousReleaseMemory(in Span<byte> span)
        {
            _rocksDb.DangerousReleaseMemory(span);
        }
    }
}
