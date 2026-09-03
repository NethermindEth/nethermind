// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    public partial class MemDb
    {
        private readonly ConcurrentDictionary<byte[], byte[]> _db;
        private readonly ConcurrentDictionary<byte[], byte[]>.AlternateLookup<ReadOnlySpan<byte>> _spanDb;

        public virtual void Remove(ReadOnlySpan<byte> key) => _spanDb.TryRemove(key, out _);

        private MemDb(int writeDelay, int readDelay, int capacity)
        {
            _writeDelay = writeDelay;
            _readDelay = readDelay;
            _db = new ConcurrentDictionary<byte[], byte[]>(Environment.ProcessorCount, capacity, Bytes.EqualityComparer);
            _spanDb = _db.GetAlternateLookup<ReadOnlySpan<byte>>();
        }
    }
}
