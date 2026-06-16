// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if ZK_EVM
using System.Collections.Generic;
#endif
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
[assembly: InternalsVisibleTo("Nethermind.Core.Test")]
[assembly: InternalsVisibleTo("Nethermind.Consensus.Test")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]
namespace Nethermind.Db
{
    public static class Metrics
    {
        [DetailedMetricOnFlag]
        public static bool DetailedMetricsEnabled { get; set; }

        private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

        [CounterMetric]
        [Description("Number of State Trie cache hits.")]
        public static long StateTreeCache => _mainStateTreeCacheHits + _otherStateTreeCacheHits.Value;
        private static long _mainStateTreeCacheHits;
        private static readonly StripedLongCounter _otherStateTreeCacheHits = new();
        // Exposed so consumers (e.g. ProcessingStats) can compute block-level deltas that exclude
        // background prewarmer activity, which runs with IsBlockProcessingThread = false.
        internal static long MainThreadStateTreeCache => _mainStateTreeCacheHits;
        internal static void IncrementStateTreeCacheHits() { if (IsBlockProcessingThread) Interlocked.Increment(ref _mainStateTreeCacheHits); else _otherStateTreeCacheHits.Increment(); }

        [CounterMetric]
        [Description("Number of State Trie reads.")]
        public static long StateTreeReads => _mainStateTreeReads + _otherStateTreeReads.Value;
        private static long _mainStateTreeReads;
        private static readonly StripedLongCounter _otherStateTreeReads = new();
        internal static long MainThreadStateTreeReads => _mainStateTreeReads;
        internal static void IncrementStateTreeReads() { if (IsBlockProcessingThread) Interlocked.Increment(ref _mainStateTreeReads); else _otherStateTreeReads.Increment(); }

        [CounterMetric]
        [Description("Number of State Reader reads.")]
        public static long StateReaderReads => _mainStateReaderReads + _otherStateReaderReads.Value;
        private static long _mainStateReaderReads;
        private static readonly StripedLongCounter _otherStateReaderReads = new();
        internal static void IncrementStateReaderReads() { if (IsBlockProcessingThread) Interlocked.Increment(ref _mainStateReaderReads); else _otherStateReaderReads.Increment(); }

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
        public static long StorageTreeCache => _mainStorageTreeCache + _otherStorageTreeCache.Value;
        private static long _mainStorageTreeCache;
        private static readonly StripedLongCounter _otherStorageTreeCache = new();
        internal static long MainThreadStorageTreeCache => _mainStorageTreeCache;
        internal static void IncrementStorageTreeCache() { if (IsBlockProcessingThread) Interlocked.Increment(ref _mainStorageTreeCache); else _otherStorageTreeCache.Increment(); }

        [CounterMetric]
        [Description("Number of storage trie reads.")]
        public static long StorageTreeReads => _mainStorageTreeReads + _otherStorageTreeReads.Value;
        private static long _mainStorageTreeReads;
        private static readonly StripedLongCounter _otherStorageTreeReads = new();
        internal static long MainThreadStorageTreeReads => _mainStorageTreeReads;
        internal static void IncrementStorageTreeReads() { if (IsBlockProcessingThread) Interlocked.Increment(ref _mainStorageTreeReads); else _otherStorageTreeReads.Increment(); }

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
        public static Dictionary<string, long> DbReads { get; } = [];
        public static Dictionary<string, long> DbWrites { get; } = [];
        public static Dictionary<string, long> DbSize { get; } = [];
        public static Dictionary<string, long> DbMemtableSize { get; } = [];
        public static Dictionary<string, long> DbBlockCacheSize { get; } = [];
        public static Dictionary<string, long> DbIndexFilterSize { get; } = [];
        public static Dictionary<(string, string), double> DbStats { get; } = [];
        public static Dictionary<(string, int, string), double> DbCompactionStats { get; } = [];
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
