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

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get => _store[key];
            set
            {
                _currentItems[key.ToArray()] = value;
            }
        }
    }
}
