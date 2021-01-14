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
// 

using System;
using System.Collections.Generic;

namespace Nethermind.Db
{
    public class MemoryMappedDb : IDbWithSpan
    {
        private readonly MemoryMappedKeyValueStore _store;
        private bool _isDisposed;
        private MemoryMappedKeyValueStore.IWriteBatch _batch;

        public MemoryMappedDb(string name, MemoryMappedKeyValueStore store)
        {
            _store = store;
            Name = name;
        }

        public byte[] this[byte[] key]
        {
            get
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
                }

                return Get(key);
            }
            set
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException($"Attempted to write to a disposed database {Name}");
                }

                if (_batch != null)
                {
                    if (value == null)
                    {
                        _batch.Delete(key);
                    }
                    else
                    {
                        _batch.Put(key, value);
                    }
                }
                else
                {
                    if (value == null)
                    {
                        Remove(key);
                    }
                    else
                    {
                        _store.Set(key, value);
                    }
                }
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
            _store.Dispose();
        }

        public string Name { get; }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys] => throw new NotImplementedException();

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => throw new NotImplementedException();

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => throw new NotImplementedException("");

        public void StartBatch()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to create a batch on a disposed database {Name}");
            }

            _batch = _store.StartBatch();
        }

        public void CommitBatch()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to commit a batch on a disposed database {Name}");
            }

            _batch.Commit();
            _batch = null;
        }

        public void Remove(byte[] key) => _store.Delete(key);

        private byte[] Get(byte[] key) => _store.TryGet(key, out MemoryMappedKeyValueStore.Slice value) ? value.ToArray() : null;

        public bool KeyExists(byte[] key) => _store.TryGet(key, out _);

        public IDb Innermost => this;

        public void Flush() { }

        public void Clear() { }

        public Span<byte> GetSpan(byte[] key) => _store.TryGet(key, out MemoryMappedKeyValueStore.Slice slice) ? slice.Span : Span<byte>.Empty;

        public void DangerousReleaseMemory(in Span<byte> span) { }
    }
}
