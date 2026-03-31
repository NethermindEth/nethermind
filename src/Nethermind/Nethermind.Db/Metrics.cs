// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using Nethermind.Core.Threading;

[assembly: InternalsVisibleTo("Nethermind.Consensus")]
[assembly: InternalsVisibleTo("Nethermind.State")]
[assembly: InternalsVisibleTo("Nethermind.Evm")]
[assembly: InternalsVisibleTo("Nethermind.TxPool")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain")]
namespace Nethermind.Db
{
    public static class Metrics
    {
        [DetailedMetricOnFlag]
        public static bool DetailedMetricsEnabled { get; set; }

        private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

        [CounterMetric]
        [Description("Number of State Trie cache hits.")]
        public static long StateTreeCache => _mainStateTreeCacheHits + _otherStateTreeCacheHits;
        private static long _mainStateTreeCacheHits;
        private static long _otherStateTreeCacheHits;
        internal static void IncrementStateTreeCacheHits() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStateTreeCacheHits : ref _otherStateTreeCacheHits);

        [CounterMetric]
        [Description("Number of State Trie reads.")]
        public static long StateTreeReads => _mainStateTreeReads + _otherStateTreeReads;
        private static long _mainStateTreeReads;
        private static long _otherStateTreeReads;
        internal static void IncrementStateTreeReads() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStateTreeReads : ref _otherStateTreeReads);

        [CounterMetric]
        [Description("Number of State Reader reads.")]
        public static long StateReaderReads => _mainStateReaderReads + _otherStateReaderReads;
        private static long _mainStateReaderReads;
        private static long _otherStateReaderReads;
        internal static void IncrementStateReaderReads() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStateReaderReads : ref _otherStateReaderReads);

        [CounterMetric]
        [Description("Number of state trie writes.")]
        public static long StateTreeWrites => _stateTreeWrites;
        private static long _stateTreeWrites;
        internal static void IncrementStateTreeWrites(long value) => Interlocked.Add(ref _stateTreeWrites, value);

        [CounterMetric]
        [Description("Number of state trie writes skipped in net.")]
        public static long StateSkippedWrites => _stateSkippedWrites;
        private static long _stateSkippedWrites;
        internal static void IncrementStateSkippedWrites(long value) => Interlocked.Add(ref _stateSkippedWrites, value);

        [CounterMetric]
        [Description("Number of State DB duplicate writes during full pruning.")]
        public static int StateDbInPruningWrites;

        [CounterMetric]
        [Description("Number of storage trie cache hits.")]
        public static long StorageTreeCache => _mainStorageTreeCache + _otherStorageTreeCache;
        private static long _mainStorageTreeCache;
        private static long _otherStorageTreeCache;
        internal static void IncrementStorageTreeCache() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStorageTreeCache : ref _otherStorageTreeCache);

        [CounterMetric]
        [Description("Number of storage trie reads.")]
        public static long StorageTreeReads => _mainStorageTreeReads + _otherStorageTreeReads;
        private static long _mainStorageTreeReads;
        private static long _otherStorageTreeReads;
        internal static void IncrementStorageTreeReads() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStorageTreeReads : ref _otherStorageTreeReads);

        [CounterMetric]
        [Description("Number of storage reader reads.")]
        public static long StorageReaderReads { get; set; }

        [CounterMetric]
        [Description("Number of storage trie writes.")]
        public static long StorageTreeWrites => _storageTreeWrites;
        private static long _storageTreeWrites;
        internal static void IncrementStorageTreeWrites(long value) => Interlocked.Add(ref _storageTreeWrites, value);

        [CounterMetric]
        [Description("Number of storage trie writes skipped in net.")]
        public static long StorageSkippedWrites => _storageSkippedWrites;
        private static long _storageSkippedWrites;
        internal static void IncrementStorageSkippedWrites(long value) => Interlocked.Add(ref _storageSkippedWrites, value);

        [GaugeMetric]
        [Description("Indicator if StateDb is being pruned.")]
        public static int StateDbPruning { get; set; }

#if ZK_EVM
        public static Dictionary<string, long> DbReads { get; } = new Dictionary<string, long>();
        public static Dictionary<string, long> DbWrites { get; } = new Dictionary<string, long>();
        public static Dictionary<string, long> DbSize { get; } = new Dictionary<string, long>();
        public static Dictionary<string, long> DbMemtableSize { get; } = new Dictionary<string, long>();
        public static Dictionary<string, long> DbBlockCacheSize { get; } = new Dictionary<string, long>();
        public static Dictionary<string, long> DbIndexFilterSize { get; } = new Dictionary<string, long>();
        public static Dictionary<(string, string), double> DbStats { get; } = new Dictionary<(string, string), double>();
        public static Dictionary<(string, int, string), double> DbCompactionStats { get; } = new Dictionary<(string, int, string), double>();
#else
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
#endif
        [DetailedMetric]
        [Description("Prewarmer get operation times")]
        [ExponentialPowerHistogramMetric(Start = 10, Factor = 1.5, Count = 30, LabelNames = ["part", "is_prewarmer"])]
        public static IMetricObserver PrewarmerGetTime { get; set; } = NoopMetricObserver.Instance;
    }

    public readonly struct PrewarmerGetTimeLabel(string part, bool isPrewarmer) : IMetricLabels
    {
        public string[] Labels { get; } = [part, isPrewarmer ? "true" : "false"];
    }
}
