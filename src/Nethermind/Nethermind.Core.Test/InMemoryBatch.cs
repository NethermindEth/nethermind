// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nethermind.Core
{
    public class InMemoryBatch : IBatch
    {
        private readonly IKeyValueStore _store;
        private readonly ConcurrentDictionary<byte[], byte[]?> _currentItems = new();
        private WriteFlags _writeFlags = WriteFlags.None;

        public InMemoryBatch(IKeyValueStore storeWithNoBatchSupport)
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

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return _store.Get(key, flags);
        }
    }
}
