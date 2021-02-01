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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    public class MemDb : IFullDb, IDbWithSpan
    {
        private readonly int _writeDelay; // for testing scenarios
        private readonly int _readDelay; // for testing scenarios
        public long ReadsCount { get; private set; }
        public long WritesCount { get; private set; }

        private readonly ConcurrentDictionary<byte[], byte[]?> _db;

        public MemDb(string name)
            : this(0, 0)
        {
            Name = name;
        }

        public MemDb() : this(0, 0)
        {
        }

        public MemDb(int writeDelay, int readDelay)
        {
            _writeDelay = writeDelay;
            _readDelay = readDelay;
            _db = new ConcurrentDictionary<byte[], byte[]>(Bytes.EqualityComparer);
        }

        public string Name { get; }

        public byte[]? this[byte[] key]
        {
            get
            {
                if (_readDelay > 0)
                {
                    Thread.Sleep(_readDelay);
                }

                ReadsCount++;
                return _db.ContainsKey(key) ? _db[key] : null;
            }
            set
            {
                if (_writeDelay > 0)
                {
                    Thread.Sleep(_writeDelay);
                }

                WritesCount++;
                _db[key] = value;
            }
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys]
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

        public void Remove(byte[] key)
        {
            _db.TryRemove(key, out _);
        }

        public bool KeyExists(byte[] key)
        {
            return _db.ContainsKey(key);
        }

        public IDb Innermost => this;

        public void Flush()
        {
        }

        public void Clear()
        {
            _db.Clear();
        }

        public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) => _db;

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => Values;

        public IBatch StartBatch()
        {
            return this.LikeABatch();
        }

        public ICollection<byte[]> Keys => _db.Keys;
        public ICollection<byte[]> Values => _db.Values;

        public int Count => _db.Count;

        public void Dispose()
        {
        }

        public Span<byte> GetSpan(byte[] key)
        {
            return this[key].AsSpan();
        }

        public void DangerousReleaseMemory(in Span<byte> span)
        {
        }
    }
}
