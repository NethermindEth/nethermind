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
        IIterator<byte[], byte[]> GetIterator(bool isOrdered = false);
        IIterator<byte[], byte[]> GetIterator(ref IteratorOptions options);
        public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore) => new ReadOnlyDb(this, createInMemWriteStore);
    }

    public ref struct IteratorOptions
    {
        public byte[]? LowerBound { get; init; }
        public byte[]? UpperBound { get; init; }
        public bool IsOrdered { get; init; }
    }

    // Some metadata options
    public interface IDbMeta
    {
        DbMetric GatherMetric(bool includeSharedCache = false) => new DbMetric();

        void Flush(bool onlyWal = false);
        void Clear() { }
        void Compact() { }

        readonly struct DbMetric
        {
            public long Size { get; init; }
            public long CacheSize { get; init; }
            public long IndexSize { get; init; }
            public long MemtableSize { get; init; }
            public long TotalReads { get; init; }
            public long TotalWrites { get; init; }
        }
    }
}
