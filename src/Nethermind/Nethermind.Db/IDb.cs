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
        DbMetric GatherMetric() => new();

        long EstimatedCount => 0;

        void Flush(bool onlyWal = false);

        /// <summary>Syncs the write-ahead log to durable storage, throwing on failure (<see cref="Flush"/> swallows).</summary>
        void SyncWal() => Flush(onlyWal: true);
        void Clear() { }

        /// <summary>
        /// Empties the store through a bulk backend path rather than one delete per key. Returns <c>true</c> only
        /// when the store is left empty; <c>false</c> means keys may remain - the backend has no bulk path, or the
        /// path it has could not take everything - and the caller has to delete the rest itself.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="Clear"/> the store stays open and usable. Space is reclaimed as the keys go, so
        /// readers holding an older view of the store - snapshots, iterators - are left behind: only call this
        /// when nothing else is reading it.
        /// </remarks>
        bool TryDeleteAll() => false;

        void Compact() { }
        void SetWriteBuffer(long sizeBytes) { }

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
