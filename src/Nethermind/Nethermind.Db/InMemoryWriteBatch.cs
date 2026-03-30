// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Db
{
    public class InMemoryWriteBatch : IWriteBatch
    {
        private readonly IKeyValueStore _store;
        // Note: need to keep order of operation
        private readonly ArrayPoolList<(byte[] Key, byte[]? Value)> _writes = new(1);
        private WriteFlags _writeFlags = WriteFlags.None;

        public InMemoryWriteBatch(IKeyValueStore storeWithNoBatchSupport)
        {
            _store = storeWithNoBatchSupport;
        }

        public void Dispose()
        {
            foreach ((byte[] Key, byte[]? Value) item in _writes)
            {
                _store.Set(item.Key, item.Value, _writeFlags);
            }

            _writes.Dispose();
            GC.SuppressFinalize(this);
        }

        public void Clear()
        {
            _writes.Clear();
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _writes.Add((key.ToArray(), value));
            _writeFlags = flags;
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            throw new NotSupportedException("Merging is not supported by this implementation.");
        }
    }
}
