/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    public class MemDb : IDb
    {
        private readonly int _writeDelay; // for testing scenarios
        private readonly int _readDelay; // for testing scenarios
        public long ReadsCount { get; private set; }
        public long WritesCount { get; private set; }
        
        internal readonly ConcurrentDictionary<byte[], byte[]> _db;

        public string Name { get; } = "MemDb";

        public byte[] this[byte[] key]
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

        public void Remove(byte[] key)
        {
            _db.TryRemove(key, out _);
        }

        public bool KeyExists(byte[] key)
        {
            return _db.ContainsKey(key);
        }

        public byte[][] GetAll() => Values.Select(v => v).ToArray();

        public void StartBatch()
        {
        }

        public void CommitBatch()
        {
        }

        public ICollection<byte[]> Keys => _db.Keys;
        public ICollection<byte[]> Values => _db.Values;

        public MemDb()
        {
            _db = new ConcurrentDictionary<byte[], byte[]>(Bytes.EqualityComparer);
        }
        
        public MemDb(int writeDelay, int readDelay)
        {
            _writeDelay = writeDelay;
            _readDelay = readDelay;
            _db = new ConcurrentDictionary<byte[], byte[]>(Bytes.EqualityComparer);
        }

        public void Clear()
        {
            _db.Clear();
        }
        
        public void Dispose()
        {
        }
    }
}