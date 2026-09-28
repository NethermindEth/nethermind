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
            // The capacity overload also opts out of lock-array growth, so it is worth taking only
            // when there is a presize to gain: otherwise every MemDb would be pinned to
            // ProcessorCount locks instead of growing them with the table.
            _db = capacity > 0
                ? new ConcurrentDictionary<byte[], byte[]>(Environment.ProcessorCount, capacity, Bytes.EqualityComparer)
                : new ConcurrentDictionary<byte[], byte[]>(Bytes.EqualityComparer);
            _spanDb = _db.GetAlternateLookup<ReadOnlySpan<byte>>();
        }
    }
}
