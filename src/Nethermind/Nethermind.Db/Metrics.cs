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
        public static long CodeDbCache
        {
            get
            {
                long total = 0;
                foreach (var value in _codeDbCache.Values)
                {
                    total += value;
                }
                return total;
            }
        }
        private static ThreadLocal<long> _codeDbCache = new(trackAllValues: true);
        [Description("Number of Code DB cache reads on thread.")]
        public static long ThreadLocalCodeDbCache => _codeDbCache.Value;
        public static void IncrementCodeDbCache() => _codeDbCache.Value++;

        [CounterMetric]
        [Description("Number of State Trie cache hits.")]
        public static long StateTreeCache
        {
            get
            {
                long total = 0;
                foreach (var value in _stateTreeCacheHits.Values)
                {
                    total += value;
                }
                return total;
            }
        }

        private static ThreadLocal<long> _stateTreeCacheHits = new(trackAllValues: true);
        public static void IncrementStateTreeCacheHits() => _stateTreeCacheHits.Value++;

        [CounterMetric]
        [Description("Number of State Trie reads.")]
        public static long StateTreeReads
        {
            get
            {
                long total = 0;
                foreach (var value in _stateTreeReads.Values)
                {
                    total += value;
                }
                return total;
            }
        }
        private static ThreadLocal<long> _stateTreeReads = new(trackAllValues: true);
        [Description("Number of State Trie reads on thread.")]
        public static long ThreadLocalStateTreeReads => _stateTreeReads.Value;
        public static void IncrementStateTreeReads() => _stateTreeReads.Value++;

        [CounterMetric]
        [Description("Number of State Reader reads.")]
        public static long StateReaderReads
        {
            get
            {
                long total = 0;
                foreach (var value in _stateReaderReads.Values)
                {
                    total += value;
                }
                return total;
            }
        }
        private static ThreadLocal<long> _stateReaderReads = new(trackAllValues: true);
        public static void IncrementStateReaderReads() => _stateReaderReads.Value++;

        [CounterMetric]
        [Description("Number of Blocks Trie writes.")]
        public static long StateTreeWrites { get; set; }

        [CounterMetric]
        [Description("Number of State DB duplicate writes during full pruning.")]
        public static int StateDbInPruningWrites;

        [CounterMetric]
        [Description("Number of storage trie cache hits.")]
        public static long StorageTreeCache
        {
            get
            {
                long total = 0;
                foreach (var value in _storageTreeCache.Values)
                {
                    total += value;
                }
                return total;
            }
        }
        private static ThreadLocal<long> _storageTreeCache = new(trackAllValues: true);
        public static void IncrementStorageTreeCache() => _storageTreeCache.Value++;

        [CounterMetric]
        [Description("Number of storage trie reads.")]
        public static long StorageTreeReads
        {
            get
            {
                long total = 0;
                foreach (var value in _storageTreeReads.Values)
                {
                    total += value;
                }
                return total;
            }
        }
        private static ThreadLocal<long> _storageTreeReads = new(trackAllValues: true);
        [Description("Number of storage trie reads on thread.")]
        public static long ThreadLocalStorageTreeReads => _storageTreeReads.Value;
        public static void IncrementStorageTreeReads() => _storageTreeReads.Value++;

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
