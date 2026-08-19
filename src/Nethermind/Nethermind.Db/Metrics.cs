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

        // The block-processing thread keeps its dedicated padded word; every other thread (RPC
        // workers, prewarm workers) previously shared ONE "other" word per counter, making each
        // per-read update a contended cross-core RMW under concurrent load — striped instead.
        [CounterMetric]
        [Description("Number of State Trie cache hits.")]
        public static long StateTreeCache => _mainStateTreeCacheHits.Value + _otherStateTreeCacheHits.Sum;
        private static CacheLinePaddedLong _mainStateTreeCacheHits;
        private static readonly StripedLong _otherStateTreeCacheHits = new();
        // Exposed so consumers (e.g. ProcessingStats) can compute block-level deltas that exclude
        // background prewarmer activity, which runs with IsBlockProcessingThread = false.
        internal static long MainThreadStateTreeCache => _mainStateTreeCacheHits.Value;
        internal static void AddStateTreeCacheHits(long count)
        {
            if (IsBlockProcessingThread) Interlocked.Add(ref _mainStateTreeCacheHits.Value, count);
            else _otherStateTreeCacheHits.Add(count);
        }

        [CounterMetric]
        [Description("Number of State Trie reads.")]
        public static long StateTreeReads => _mainStateTreeReads.Value + _otherStateTreeReads.Sum;
        private static CacheLinePaddedLong _mainStateTreeReads;
        private static readonly StripedLong _otherStateTreeReads = new();
        internal static long MainThreadStateTreeReads => _mainStateTreeReads.Value;
        internal static void AddStateTreeReads(long count)
        {
            if (IsBlockProcessingThread) Interlocked.Add(ref _mainStateTreeReads.Value, count);
            else _otherStateTreeReads.Add(count);
        }

        [CounterMetric]
        [Description("Number of State Reader reads.")]
        public static long StateReaderReads => _mainStateReaderReads.Value + _otherStateReaderReads.Sum;
        private static CacheLinePaddedLong _mainStateReaderReads;
        private static readonly StripedLong _otherStateReaderReads = new();
        internal static void IncrementStateReaderReads()
        {
            if (IsBlockProcessingThread) Interlocked.Increment(ref _mainStateReaderReads.Value);
            else _otherStateReaderReads.Increment();
        }

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
        public static long StorageTreeCache => _mainStorageTreeCache.Value + _otherStorageTreeCache.Sum;
        private static CacheLinePaddedLong _mainStorageTreeCache;
        private static readonly StripedLong _otherStorageTreeCache = new();
        internal static long MainThreadStorageTreeCache => _mainStorageTreeCache.Value;
        internal static void AddStorageTreeCache(long count)
        {
            if (IsBlockProcessingThread) Interlocked.Add(ref _mainStorageTreeCache.Value, count);
            else _otherStorageTreeCache.Add(count);
        }

        [CounterMetric]
        [Description("Number of storage trie reads.")]
        public static long StorageTreeReads => _mainStorageTreeReads.Value + _otherStorageTreeReads.Sum;
        private static CacheLinePaddedLong _mainStorageTreeReads;
        private static readonly StripedLong _otherStorageTreeReads = new();
        internal static long MainThreadStorageTreeReads => _mainStorageTreeReads.Value;
        internal static void AddStorageTreeReads(long count)
        {
            if (IsBlockProcessingThread) Interlocked.Add(ref _mainStorageTreeReads.Value, count);
            else _otherStorageTreeReads.Add(count);
        }

        [CounterMetric]
        [Description("Number of pre-block (prewarmer-shared) cache hits for accounts, counted on the consumer scope only (populator probes excluded); first-in-block touches, so hits/(hits+misses) = prewarm coverage.")]
        public static long PreBlockCacheAccountHits => _mainPreBlockAccountHits.Value + _otherPreBlockAccountHits.Sum;
        private static CacheLinePaddedLong _mainPreBlockAccountHits;
        private static readonly StripedLong _otherPreBlockAccountHits = new();
        internal static long MainThreadPreBlockAccountHits => _mainPreBlockAccountHits.Value;
        internal static void AddPreBlockAccountHits(long count)
        {
            if (IsBlockProcessingThread) Interlocked.Add(ref _mainPreBlockAccountHits.Value, count);
            else _otherPreBlockAccountHits.Add(count);
        }

        [CounterMetric]
        [Description("Number of pre-block (prewarmer-shared) cache misses for accounts, counted on the consumer scope only (populator probes excluded).")]
        public static long PreBlockCacheAccountMisses => _mainPreBlockAccountMisses.Value + _otherPreBlockAccountMisses.Sum;
        private static CacheLinePaddedLong _mainPreBlockAccountMisses;
        private static readonly StripedLong _otherPreBlockAccountMisses = new();
        internal static long MainThreadPreBlockAccountMisses => _mainPreBlockAccountMisses.Value;
        internal static void AddPreBlockAccountMisses(long count)
        {
            if (IsBlockProcessingThread) Interlocked.Add(ref _mainPreBlockAccountMisses.Value, count);
            else _otherPreBlockAccountMisses.Add(count);
        }

        [CounterMetric]
        [Description("Number of pre-block (prewarmer-shared) cache hits for storage slots, counted on the consumer scope only (populator probes excluded); first-in-block touches, so hits/(hits+misses) = prewarm coverage.")]
        public static long PreBlockCacheStorageHits => _mainPreBlockStorageHits.Value + _otherPreBlockStorageHits.Sum;
        private static CacheLinePaddedLong _mainPreBlockStorageHits;
        private static readonly StripedLong _otherPreBlockStorageHits = new();
        internal static long MainThreadPreBlockStorageHits => _mainPreBlockStorageHits.Value;
        internal static void AddPreBlockStorageHits(long count)
        {
            if (IsBlockProcessingThread) Interlocked.Add(ref _mainPreBlockStorageHits.Value, count);
            else _otherPreBlockStorageHits.Add(count);
        }

        [CounterMetric]
        [Description("Number of pre-block (prewarmer-shared) cache misses for storage slots, counted on the consumer scope only (populator probes excluded).")]
        public static long PreBlockCacheStorageMisses => _mainPreBlockStorageMisses.Value + _otherPreBlockStorageMisses.Sum;
        private static CacheLinePaddedLong _mainPreBlockStorageMisses;
        private static readonly StripedLong _otherPreBlockStorageMisses = new();
        internal static long MainThreadPreBlockStorageMisses => _mainPreBlockStorageMisses.Value;
        internal static void AddPreBlockStorageMisses(long count)
        {
            if (IsBlockProcessingThread) Interlocked.Add(ref _mainPreBlockStorageMisses.Value, count);
            else _otherPreBlockStorageMisses.Add(count);
        }

        [CounterMetric]
        [Description("Number of storage reader reads.")]
        public static long StorageReaderReads => _storageReaderReads.Sum;
        private static readonly StripedLong _storageReaderReads = new();
        internal static void IncrementStorageReaderReads() => _storageReaderReads.Increment();

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

        [GaugeMetric]
        [Description("Duration of the last full pruning's trie copy and commit (excludes waiting for a suitable state root), in seconds.")]
        public static long FullPruningLastDurationSeconds { get; set; }

        [CounterMetric]
        [Description("Number of full prunings completed since the node started.")]
        public static long FullPruningCount => _fullPruningCount;
        private static long _fullPruningCount;
        internal static void IncrementFullPruningCount() => Interlocked.Increment(ref _fullPruningCount);

#if ZK_EVM
        public static Dictionary<string, long> DbReads { get; } = [];
        public static Dictionary<string, long> DbWrites { get; } = [];
        public static Dictionary<string, long> DbSize { get; } = [];
        public static Dictionary<string, long> DbMemtableSize { get; } = [];
        public static Dictionary<string, long> DbBlockCacheSize { get; } = [];
        public static Dictionary<string, long> DbIndexFilterSize { get; } = [];
        // DbStats and DbCompactionStats omitted: double-valued and unread in the guest.
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
