// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Threading;

using Nethermind.Core.Attributes;

namespace Nethermind.Db
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Number of Code DB cache reads.")]
        public static long CodeDbCache { get; set; }

        [CounterMetric]
        [Description("Number of State Trie cache hits.")]
        public static long StateTreeCache { get; set; }

        [CounterMetric]
        [Description("Number of State Trie reads.")]
        public static long StateTreeReads { get; set; }

        [CounterMetric]
        [Description("Number of State Reader cache hits.")]
        public static long StateReaderCache => _stateReaderCacheHits;
        private static long _stateReaderCacheHits;
        public static void IncrementStateReaderCacheHits() => Interlocked.Increment(ref _stateReaderCacheHits);

        [CounterMetric]
        [Description("Number of State Reader reads.")]
        public static long StateReaderReads => _stateReaderReads;
        private static long _stateReaderReads;
        public static void IncrementStateReaderReads() => Interlocked.Increment(ref _stateReaderReads);

        [CounterMetric]
        [Description("Number of Blocks Trie writes.")]
        public static long StateTreeWrites { get; set; }

        [CounterMetric]
        [Description("Number of State DB duplicate writes during full pruning.")]
        public static int StateDbInPruningWrites;

        [CounterMetric]
        [Description("Number of storage trie cache hits.")]
        public static long StorageTreeCache { get; set; }

        [CounterMetric]
        [Description("Number of storage trie reads.")]
        public static long StorageTreeReads { get; set; }

        [CounterMetric]
        [Description("Number of storage reader reads.")]
        public static long StorageReaderReads { get; set; }

        [CounterMetric]
        [Description("Number of storage trie writes.")]
        public static long StorageTreeWrites { get; set; }

        [GaugeMetric]
        [Description("Indicator if StadeDb is being pruned.")]
        public static int StateDbPruning { get; set; }

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
