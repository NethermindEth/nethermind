// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public class InMemoryBatch : IBatch
    {
        private readonly IKeyValueStore _store;
        private readonly ConcurrentDictionary<ValueKeccak, byte[]?> _currentItems = new();
        private WriteFlags _writeFlags = WriteFlags.None;

        public InMemoryBatch(IKeyValueStore storeWithNoBatchSupport)
        {
            _store = storeWithNoBatchSupport;
        }

        public void Dispose()
        {
            foreach (KeyValuePair<ValueKeccak, byte[]?> keyValuePair in _currentItems)
            {
                _store.Set(keyValuePair.Key.Bytes, keyValuePair.Value, _writeFlags);
            }

            GC.SuppressFinalize(this);
        }

        public void Set(in ValueKeccak key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _currentItems[key] = value;
            _writeFlags = flags;
        }

        public byte[]? Get(in ValueKeccak key, ReadFlags flags = ReadFlags.None)
        {
            return _store.Get(key.Bytes, flags);
        }
    }
}
