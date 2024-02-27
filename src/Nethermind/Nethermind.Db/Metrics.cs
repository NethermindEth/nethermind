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
        [Description("_Deprecated._ Number of Bloom DB reads.")]
        public static long BloomDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Bloom DB writes.")]
        public static long BloomDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of CHT DB reads.")]
        public static long CHTDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of CHT DB writes.")]
        public static long CHTDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Blocks DB reads.")]
        public static long BlocksDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Blocks DB writes.")]
        public static long BlocksDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Code DB cache reads.")]
        public static long CodeDbCache { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Code DB reads.")]
        public static long CodeDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Code DB writes.")]
        public static long CodeDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Receipts DB reads.")]
        public static long ReceiptsDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Receipts DB writes.")]
        public static long ReceiptsDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Block Infos DB reads.")]
        public static long BlockInfosDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Block Infos DB writes.")]
        public static long BlockInfosDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of State Trie reads.")]
        public static long StateTreeReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Blocks Trie writes.")]
        public static long StateTreeWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of State DB reads.")]
        public static long StateDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of State DB writes.")]
        public static long StateDbWrites { get; set; }

        [CounterMetric]
        [Description("Number of State DB duplicate writes during full pruning.")]
        public static int StateDbInPruningWrites;

        [CounterMetric]
        [Description("_Deprecated._ Number of storage trie reads.")]
        public static long StorageTreeReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of storage trie writes.")]
        public static long StorageTreeWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of other DB reads.")]
        public static long OtherDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of other DB writes.")]
        public static long OtherDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Headers DB reads.")]
        public static long HeaderDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Headers DB writes.")]
        public static long HeaderDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of BlockNumbers DB reads.")]
        public static long BlockNumberDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of BlockNumbers DB writes.")]
        public static long BlockNumberDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Witness DB reads.")]
        public static long WitnessDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Witness DB writes.")]
        public static long WitnessDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Metadata DB reads.")]
        public static long MetadataDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of Metadata DB writes.")]
        public static long MetadataDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of BadBlocks DB writes.")]
        public static long BadBlocksDbWrites { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of BadBlocks DB reads.")]
        public static long BadBlocksDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of BlobTransactions DB reads.")]
        public static long BlobTransactionsDbReads { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of BlobTransactions DB writes.")]
        public static long BlobTransactionsDbWrites { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Indicator if StadeDb is being pruned.")]
        public static int StateDbPruning { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of state DB in bytes")]
        public static long StateDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of receipts DB in bytes")]
        public static long ReceiptsDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of headers DB in bytes")]
        public static long HeadersDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of blocks DB in bytes")]
        public static long BlocksDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of bloom DB in bytes")]
        public static long BloomDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of code DB in bytes")]
        public static long CodeDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of blockInfos DB in bytes")]
        public static long BlockInfosDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of cht DB in bytes")]
        public static long ChtDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of metadata DB in bytes")]
        public static long MetadataDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of witness DB in bytes")]
        public static long WitnessDbSize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of unmanaged memory for DB block caches in bytes")]
        public static long DbBlockCacheMemorySize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of unmanaged memory for DB indexes and filters in bytes")]
        public static long DbIndexFilterMemorySize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of unmanaged memory for DB memtables in bytes")]
        public static long DbMemtableMemorySize { get; set; }

        [GaugeMetric]
        [Description("_Deprecated._ Size of total unmanaged memory for DB in bytes")]
        public static long DbTotalMemorySize { get; set; }

        [GaugeMetric]
        [Description("Database reads per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbReads { get; } = new();

        [GaugeMetric]
        [Description("Database writes per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbWrites { get; } = new();

        [GaugeMetric]
        [Description("Database size per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbSize { get; } = new();

        [GaugeMetric]
        [Description("Database memtable per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbMemtableSize { get; } = new();

        [GaugeMetric]
        [Description("Database block cache size per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbBlockCacheSize { get; } = new();

        [GaugeMetric]
        [Description("Database index and filter size per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbIndexFilterSize { get; } = new();

        [Description("Metrics extracted from RocksDB Compaction Stats and DB Statistics")]
        [KeyIsLabel("db", "metric")]
        public static NonBlocking.ConcurrentDictionary<(string, string), double> DbStats { get; } = new();

        [Description("Metrics extracted from RocksDB Compaction Stats")]
        [KeyIsLabel("db", "level", "metric")]
        public static NonBlocking.ConcurrentDictionary<(string, int, string), double> DbCompactionStats { get; } = new();
    }
}
