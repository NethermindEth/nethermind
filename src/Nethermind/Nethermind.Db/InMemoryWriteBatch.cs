// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    public class InMemoryWriteBatch : IWriteBatch
    {
        private readonly IKeyValueStore _store;
        private readonly ConcurrentDictionary<byte[], byte[]?> _currentItems = new(Bytes.EqualityComparer);
        private WriteFlags _writeFlags = WriteFlags.None;

        public InMemoryWriteBatch(IKeyValueStore storeWithNoBatchSupport)
        {
            _store = storeWithNoBatchSupport;
        }

        public void Dispose()
        {
            foreach (KeyValuePair<byte[], byte[]?> keyValuePair in _currentItems)
            {
                _store.Set(keyValuePair.Key, keyValuePair.Value, _writeFlags);
            }

            GC.SuppressFinalize(this);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _currentItems[key.ToArray()] = value;
            _writeFlags = flags;
        }

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey)
        {
            _store.DeleteByRange(startKey, endKey);
        }
    }
}
