//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using Nethermind.Store;
using RocksDbSharp;

namespace Nethermind.Db.Databases
{
    public class ColumnDb : IDb
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
        
        public void Dispose() { }

        public string Name { get; }

        public byte[] this[byte[] key]
        {
            get
            {
                UpdateReadMetrics();
                return _rocksDb.Get(key, _columnFamily);
            }
            set
            {
                UpdateWriteMetrics();
                if (_mainDb.CurrentBatch != null)
                {
                    if (value == null)
                    {
                        _mainDb.CurrentBatch.Delete(key, _columnFamily);
                    }
                    else
                    {
                        _mainDb.CurrentBatch.Put(key, value, _columnFamily);
                    }
                }
                else
                {
                    if (value == null)
                    {
                        _rocksDb.Remove(key, _columnFamily, _mainDb.WriteOptions);
                    }
                    else
                    {
                        _rocksDb.Put(key, value, _columnFamily, _mainDb.WriteOptions);
                    }
                }
            }
        }

        public byte[][] GetAll()
        {
            Iterator iterator = _rocksDb.NewIterator(_columnFamily);
            iterator = iterator.SeekToFirst();
            var values = new List<byte[]>();
            while (iterator.Valid())
            {
                values.Add(iterator.Value());
                iterator = iterator.Next();
            }

            iterator.Dispose();

            return values.ToArray();
        }

        public void StartBatch()
        {
            _mainDb.StartBatch();
        }

        public void CommitBatch()
        {
            _mainDb.CommitBatch();
        }

        public void Remove(byte[] key)
        {
            _rocksDb.Remove(key, _columnFamily, _mainDb.WriteOptions);
        }

        public bool KeyExists(byte[] key) => _rocksDb.Get(key, _columnFamily) != null;
        
        public IDb Innermost => _mainDb.Innermost;

        private void UpdateWriteMetrics() => _mainDb.UpdateWriteMetrics();

        private void UpdateReadMetrics() => _mainDb.UpdateReadMetrics();
    }
}