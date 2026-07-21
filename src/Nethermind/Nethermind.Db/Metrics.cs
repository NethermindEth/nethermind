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
        public static long StateTreeCache => _mainStateTreeCacheHits.Value + _otherStateTreeCacheHits.Value;
        private static CacheLinePaddedLong _mainStateTreeCacheHits;
        private static CacheLinePaddedLong _otherStateTreeCacheHits;
        // Exposed so consumers (e.g. ProcessingStats) can compute block-level deltas that exclude
        // background prewarmer activity, which runs with IsBlockProcessingThread = false.
        internal static long MainThreadStateTreeCache => _mainStateTreeCacheHits.Value;
        internal static void AddStateTreeCacheHits(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStateTreeCacheHits.Value : ref _otherStateTreeCacheHits.Value, count);

        [CounterMetric]
        [Description("Number of State Trie reads.")]
        public static long StateTreeReads => _mainStateTreeReads.Value + _otherStateTreeReads.Value;
        private static CacheLinePaddedLong _mainStateTreeReads;
        private static CacheLinePaddedLong _otherStateTreeReads;
        internal static long MainThreadStateTreeReads => _mainStateTreeReads.Value;
        internal static void AddStateTreeReads(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStateTreeReads.Value : ref _otherStateTreeReads.Value, count);

        [CounterMetric]
        [Description("Number of State Reader reads.")]
        public static long StateReaderReads => _mainStateReaderReads.Value + _otherStateReaderReads.Value;
        private static CacheLinePaddedLong _mainStateReaderReads;
        private static CacheLinePaddedLong _otherStateReaderReads;
        internal static void IncrementStateReaderReads() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStateReaderReads.Value : ref _otherStateReaderReads.Value);

        [CounterMetric]
        [Description("Number of state trie writes.")]
        public static long StateTreeWrites => _stateTreeWrites.Value;
        private static CacheLinePaddedLong _stateTreeWrites;
        internal static void IncrementStateTreeWrites(long value) => Interlocked.Add(ref _stateTreeWrites.Value, value);

        [CounterMetric]
        [Description("Number of state trie writes skipped in net.")]
        public static long StateSkippedWrites => _stateSkippedWrites.Value;
        private static CacheLinePaddedLong _stateSkippedWrites;
        internal static void IncrementStateSkippedWrites(long value) => Interlocked.Add(ref _stateSkippedWrites.Value, value);

        [CounterMetric]
        [Description("Number of State DB duplicate writes during full pruning.")]
        public static int StateDbInPruningWrites;

        [CounterMetric]
        [Description("Number of storage trie cache hits.")]
        public static long StorageTreeCache => _mainStorageTreeCache.Value + _otherStorageTreeCache.Value;
        private static CacheLinePaddedLong _mainStorageTreeCache;
        private static CacheLinePaddedLong _otherStorageTreeCache;
        internal static long MainThreadStorageTreeCache => _mainStorageTreeCache.Value;
        internal static void AddStorageTreeCache(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStorageTreeCache.Value : ref _otherStorageTreeCache.Value, count);

        [CounterMetric]
        [Description("Number of storage trie reads.")]
        public static long StorageTreeReads => _mainStorageTreeReads.Value + _otherStorageTreeReads.Value;
        private static CacheLinePaddedLong _mainStorageTreeReads;
        private static CacheLinePaddedLong _otherStorageTreeReads;
        internal static long MainThreadStorageTreeReads => _mainStorageTreeReads.Value;
        internal static void AddStorageTreeReads(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStorageTreeReads.Value : ref _otherStorageTreeReads.Value, count);

        [CounterMetric]
        [Description("Number of pre-block (prewarmer-shared) cache hits for accounts, counted on the consumer scope only (populator probes excluded); first-in-block touches, so hits/(hits+misses) = prewarm coverage.")]
        public static long PreBlockCacheAccountHits => _mainPreBlockAccountHits.Value + _otherPreBlockAccountHits.Value;
        private static CacheLinePaddedLong _mainPreBlockAccountHits;
        private static CacheLinePaddedLong _otherPreBlockAccountHits;
        internal static long MainThreadPreBlockAccountHits => _mainPreBlockAccountHits.Value;
        internal static void AddPreBlockAccountHits(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainPreBlockAccountHits.Value : ref _otherPreBlockAccountHits.Value, count);

        [CounterMetric]
        [Description("Number of pre-block (prewarmer-shared) cache misses for accounts, counted on the consumer scope only (populator probes excluded).")]
        public static long PreBlockCacheAccountMisses => _mainPreBlockAccountMisses.Value + _otherPreBlockAccountMisses.Value;
        private static CacheLinePaddedLong _mainPreBlockAccountMisses;
        private static CacheLinePaddedLong _otherPreBlockAccountMisses;
        internal static long MainThreadPreBlockAccountMisses => _mainPreBlockAccountMisses.Value;
        internal static void AddPreBlockAccountMisses(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainPreBlockAccountMisses.Value : ref _otherPreBlockAccountMisses.Value, count);

        [CounterMetric]
        [Description("Number of pre-block (prewarmer-shared) cache hits for storage slots, counted on the consumer scope only (populator probes excluded); first-in-block touches, so hits/(hits+misses) = prewarm coverage.")]
        public static long PreBlockCacheStorageHits => _mainPreBlockStorageHits.Value + _otherPreBlockStorageHits.Value;
        private static CacheLinePaddedLong _mainPreBlockStorageHits;
        private static CacheLinePaddedLong _otherPreBlockStorageHits;
        internal static long MainThreadPreBlockStorageHits => _mainPreBlockStorageHits.Value;
        internal static void AddPreBlockStorageHits(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainPreBlockStorageHits.Value : ref _otherPreBlockStorageHits.Value, count);

        [CounterMetric]
        [Description("Number of pre-block (prewarmer-shared) cache misses for storage slots, counted on the consumer scope only (populator probes excluded).")]
        public static long PreBlockCacheStorageMisses => _mainPreBlockStorageMisses.Value + _otherPreBlockStorageMisses.Value;
        private static CacheLinePaddedLong _mainPreBlockStorageMisses;
        private static CacheLinePaddedLong _otherPreBlockStorageMisses;
        internal static long MainThreadPreBlockStorageMisses => _mainPreBlockStorageMisses.Value;
        internal static void AddPreBlockStorageMisses(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainPreBlockStorageMisses.Value : ref _otherPreBlockStorageMisses.Value, count);

        [CounterMetric]
        [Description("Number of storage reader reads.")]
        public static long StorageReaderReads => _storageReaderReads.Value;
        private static CacheLinePaddedLong _storageReaderReads;
        internal static void IncrementStorageReaderReads() => Interlocked.Increment(ref _storageReaderReads.Value);

        [CounterMetric]
        [Description("Number of storage trie writes.")]
        public static long StorageTreeWrites => _storageTreeWrites.Value;
        private static CacheLinePaddedLong _storageTreeWrites;
        internal static void IncrementStorageTreeWrites(long value) => Interlocked.Add(ref _storageTreeWrites.Value, value);

        [CounterMetric]
        [Description("Number of storage trie writes skipped in net.")]
        public static long StorageSkippedWrites => _storageSkippedWrites.Value;
        private static CacheLinePaddedLong _storageSkippedWrites;
        internal static void IncrementStorageSkippedWrites(long value) => Interlocked.Add(ref _storageSkippedWrites.Value, value);

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
