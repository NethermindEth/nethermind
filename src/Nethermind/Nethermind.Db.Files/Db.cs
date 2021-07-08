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
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db.Files
{
    public class Db : IDbWithSpan
    {
        private readonly KeccakMap _map;
        private readonly IntPtr _log;
        private static readonly int _size = (int)1.GiB();
        private int _position = 1 ; // positive values required
        private readonly ConcurrentQueue<KeccakMap> _batches = new (); 
        
        public Db(string name, int buckets = 4 * 1024 * 1024 , int allocate = 1024 * 1024)
        {
            Name = name;
            _map = new KeccakMap(buckets, allocate);
            _log = Marshal.AllocHGlobal(_size);
        }

        public byte[]? this[byte[] key]
        {
            get => !_map.TryGet(key, out long value) ? null : GetAt(value).ToArray();
            set
            {
                _map.Put(key, Log(value));
            }
        }

        private unsafe long Log(byte[] value)
        {
            int length = value.Length;
            int position;

            lock (_map)
            {
                position = _position;
                _position += length;

                if (_position > _size)
                {
                    throw new OutOfMemoryException("Buffer breached!");
                }

                value.CopyTo(new Span<byte>((byte*) _log.ToPointer() + position, length));
            }

            return Write(position, length);
        }

        public IBatch StartBatch() => new Batch(this);

        public Span<byte> GetSpan(byte[] key) => !_map.TryGet(key, out long value) ? Span<byte>.Empty : GetAt(value);

        private unsafe Span<byte> GetAt(long value)
        {
            (int position, int length) = Parse(value);
            return new Span<byte>((byte*) _log.ToPointer() + position, length);
        }

        public void DangerousReleaseMemory(in Span<byte> span) { }

        static (int position, int length) Parse(long value) => ((int)(value >> 32), (int)(value & 0xffff));

        static long Write(int position, int length) => (((long)position) << 32) | (uint)length;

        public string Name { get; }

        public void Dispose() { }

        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => throw new NotImplementedException();

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => throw new NotImplementedException();

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => throw new NotImplementedException();

        public void Remove(byte[] key) => throw new NotImplementedException();

        public bool KeyExists(byte[] key) => _map.TryGet(key, out _);

        public IDb Innermost => this;

        public void Flush() { }

        public void Clear() => throw new NotImplementedException();

        class Batch : IBatch
        {
            private readonly Db _db;
            private KeccakMap _map;

            public Batch(Db db)
            {
                _db = db;
                _map = _db._batches.TryDequeue(out KeccakMap? map) ? map : new KeccakMap(1024 * 4, 4 * 1024);
            }

            public byte[]? this[byte[] key]
            {
                get
                {
                    if (!_map.TryGet(key, out long position) && !_db._map.TryGet(key, out position))
                    {
                        return null;
                    }

                    return _db.GetAt(position).ToArray();
                }
                set
                {
                    KeccakMap.GuardKey(key);

                    // write directly to log file,
                    // if batch fails, log will contains some garbage,
                    // if batch succeeds, there's less items to copy
                    _map.Put(key, _db.Log(value));
                }
            }

            public void Dispose()
            {
                _map.CopyTo(_db._map);
                _map.Clear();
                _db._batches.Enqueue(_map);
                _map = null!;
            }
        }
    }
}
