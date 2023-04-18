// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

        public InMemoryBatch(IKeyValueStore storeWithNoBatchSupport)
        {
            _store = storeWithNoBatchSupport;
        }

        public void Dispose()
        {
            foreach (KeyValuePair<byte[], byte[]?> keyValuePair in _currentItems)
            {
                _store[keyValuePair.Key] = keyValuePair.Value;
            }

            GC.SuppressFinalize(this);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _currentItems[key.ToArray()] = value;
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return _store.Get(key, flags);
        }
    }
}
