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
        void Compact() { }

        /// <summary>Compacts only when live data is a small fraction of the store's files - the shape mass deletion
        /// leaves behind - so a caller can offer the space back without forcing a rewrite of healthy data. Blocks
        /// until done. Returns whether it compacted; the default declines.</summary>
        bool CompactIfDeadWeightExceeds(double deadRatio) => false;
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
