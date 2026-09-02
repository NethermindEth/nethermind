// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    public partial class MemDb
    {
        // Single-threaded guest: a plain dictionary avoids ConcurrentDictionary's per-access overhead.
        private readonly Dictionary<byte[], byte[]?> _db;
        private readonly Dictionary<byte[], byte[]?>.AlternateLookup<ReadOnlySpan<byte>> _spanDb;

        public virtual void Remove(ReadOnlySpan<byte> key) => _spanDb.Remove(key);

        private MemDb(int writeDelay, int readDelay, int capacity)
        {
            _writeDelay = writeDelay;
            _readDelay = readDelay;
            _db = new Dictionary<byte[], byte[]?>(capacity, Bytes.EqualityComparer);
            _spanDb = _db.GetAlternateLookup<ReadOnlySpan<byte>>();
        }
    }
}
