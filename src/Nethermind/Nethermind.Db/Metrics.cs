// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Db
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Number of Bloom DB reads.")]
        public static long BloomDbReads { get; set; }

        [CounterMetric]
        [Description("Number of Bloom DB writes.")]
        public static long BloomDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of CHT DB reads.")]
        public static long CHTDbReads { get; set; }

        [CounterMetric]
        [Description("Number of CHT DB writes.")]
        public static long CHTDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of Blocks DB reads.")]
        public static long BlocksDbReads { get; set; }

        [CounterMetric]
        [Description("Number of Blocks DB writes.")]
        public static long BlocksDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of Code DB reads.")]
        public static long CodeDbReads { get; set; }

        [CounterMetric]
        [Description("Number of Code DB writes.")]
        public static long CodeDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of Receipts DB reads.")]
        public static long ReceiptsDbReads { get; set; }

        [CounterMetric]
        [Description("Number of Receipts DB writes.")]
        public static long ReceiptsDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of Block Infos DB reads.")]
        public static long BlockInfosDbReads { get; set; }

        [CounterMetric]
        [Description("Number of Block Infos DB writes.")]
        public static long BlockInfosDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of State Trie reads.")]
        public static long StateTreeReads { get; set; }

        [CounterMetric]
        [Description("Number of Blocks Trie writes.")]
        public static long StateTreeWrites { get; set; }

        [CounterMetric]
        [Description("Number of State DB reads.")]
        public static long StateDbReads { get; set; }

        [CounterMetric]
        [Description("Number of State DB writes.")]
        public static long StateDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of State DB duplicate writes during full pruning.")]
        public static int StateDbInPruningWrites;

        [CounterMetric]
        [Description("Number of storge trie reads.")]
        public static long StorageTreeReads { get; set; }

        [CounterMetric]
        [Description("Number of storage trie writes.")]
        public static long StorageTreeWrites { get; set; }

        [CounterMetric]
        [Description("Number of other DB reads.")]
        public static long OtherDbReads { get; set; }

        [CounterMetric]
        [Description("Number of other DB writes.")]
        public static long OtherDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of Headers DB reads.")]
        public static long HeaderDbReads { get; set; }

        [CounterMetric]
        [Description("Number of Headers DB writes.")]
        public static long HeaderDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of BlockNumbers DB reads.")]
        public static long BlockNumberDbReads { get; set; }

        [CounterMetric]
        [Description("Number of BlockNumbers DB writes.")]
        public static long BlockNumberDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of Witness DB reads.")]
        public static long WitnessDbReads { get; set; }

        [CounterMetric]
        [Description("Number of Witness DB writes.")]
        public static long WitnessDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of Metadata DB reads.")]
        public static long MetadataDbReads { get; set; }

        [CounterMetric]
        [Description("Number of Metadata DB writes.")]
        public static long MetadataDbWrites { get; set; }

        [GaugeMetric]
        [Description("Indicator if StadeDb is being pruned.")]
        public static int StateDbPruning { get; set; }

        [GaugeMetric]
        [Description("Size of state DB in bytes")]
        public static long StateDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of receipts DB in bytes")]
        public static long ReceiptsDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of headers DB in bytes")]
        public static long HeadersDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of blocks DB in bytes")]
        public static long BlocksDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of bloom DB in bytes")]
        public static long BloomDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of code DB in bytes")]
        public static long CodeDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of blockInfos DB in bytes")]
        public static long BlockInfosDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of cht DB in bytes")]
        public static long ChtDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of metadata DB in bytes")]
        public static long MetadataDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of witness DB in bytes")]
        public static long WitnessDbSize { get; set; }

        [GaugeMetric]
        [Description("Size of unmanaged memory for DB block caches in bytes")]
        public static long DbBlockCacheMemorySize { get; set; }

        [GaugeMetric]
        [Description("Size of unmanaged memory for DB indexes and filters in bytes")]
        public static long DbIndexFilterMemorySize { get; set; }

        [GaugeMetric]
        [Description("Size of unmanaged memory for DB memtables in bytes")]
        public static long DbMemtableMemorySize { get; set; }

        [GaugeMetric]
        [Description("Size of total unmanaged memory for DB in bytes")]
        public static long DbTotalMemorySize { get; set; }

        [Description("Metrics extracted from RocksDB Compaction Stats and DB Statistics")]
        public static IDictionary<string, long> DbStats { get; set; } = new ConcurrentDictionary<string, long>();
    }
}
