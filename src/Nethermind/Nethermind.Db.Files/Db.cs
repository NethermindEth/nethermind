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
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db.Files
{
    public class Db : IDbWithSpan
    {
        private readonly PersistentKeccakMap _map;
        private readonly PersistentLog _log;
        private readonly PersistentLog _mapLog;
        private readonly ConcurrentQueue<InMemoryKeccakMap> _batches = new();

        public Db(string basePath, string name, int buckets = 4 * 1024 * 1024)
        {
            Name = name;

            string? fullPath = name.GetApplicationResourcePath(basePath);

            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }

            _log = new PersistentLog((int)256.MiB(), fullPath);
            _mapLog = new PersistentLog((int)128.MiB(), fullPath, ".keys");

            _log.Write(new byte[] { 1 }); // write one to make only positive positions
            _mapLog.Write(new byte[] { 1 }); // write one to make only positive positions

            _map = new PersistentKeccakMap(buckets, _mapLog);
        }

        public byte[]? this[byte[] key]
        {
            get => !_map.TryGet(key, out long value) ? null : GetAt(value).ToArray();
            set
            {
                _map.Put(key, Log(value));
            }
        }

        private long Log(byte[] value) => _log.Write(value);

        public IBatch StartBatch() => new Batch(this);

        // TODO: make faster!
        public Span<byte> GetSpan(byte[] key) => !_map.TryGet(key, out long value) ? Span<byte>.Empty : GetAt(value);

        private Span<byte> GetAt(long value) => _log.Read(value);

        public void DangerousReleaseMemory(in Span<byte> span) { }

        public string Name { get; }

        public void Dispose()
        {
            _log.Dispose();
            _mapLog.Dispose();
        }

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
            private InMemoryKeccakMap _map;

            public Batch(Db db)
            {
                _db = db;
                _map = _db._batches.TryDequeue(out InMemoryKeccakMap? map) ? map : new InMemoryKeccakMap(1024 * 4, 512 * 1024);
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
                    InMemoryKeccakMap.GuardKey(key);

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
