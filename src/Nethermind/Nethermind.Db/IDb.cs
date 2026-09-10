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
        IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false);
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

        /// <summary>Compacts only when the store's files carry at least <paramref name="deadRatio"/> tombstones per
        /// surviving put - the shape mass deletion leaves behind - so a caller can offer the space back without
        /// forcing a rewrite of healthy data. Blocks until done. Returns whether it compacted; the default
        /// declines. A decorator must forward this together with <see cref="InterruptCompactions"/>, or a shutdown
        /// during the rewrite loses its ability to abort it.</summary>
        bool CompactIfDeadWeightExceeds(double deadRatio) => false;

        /// <summary>Aborts any manual compaction in flight and refuses new ones, so a shutdown joining the thread
        /// that called <see cref="CompactIfDeadWeightExceeds"/> is not held for the rewrite's duration. Terminal
        /// and store-wide: there is no re-enable, a column store applies it to every column sharing the database,
        /// and <see cref="Compact"/> never works again for the process's life - shutdown only, never a pause.</summary>
        void InterruptCompactions() { }
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
