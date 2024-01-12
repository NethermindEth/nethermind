// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public interface IDb : IKeyValueStoreWithBatching, IDbMeta, IDisposable
    {
        string Name { get; }
        KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] { get; }
        IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false);
        IEnumerable<byte[]> GetAllKeys(bool ordered = false);
        IEnumerable<byte[]> GetAllValues(bool ordered = false);

        public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore) => new ReadOnlyDb(this, createInMemWriteStore);
    }

    // Some metadata options
    public interface IDbMeta
    {
        long GetSize() => 0;
        long GetCacheSize() => 0;
        long GetIndexSize() => 0;
        long GetMemtableSize() => 0;

        void Flush() { }
        void Clear() { }
        void Compact() { }
    }
}
