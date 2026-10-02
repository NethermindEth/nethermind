// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

namespace Nethermind.Trie.Pruning
{
    public static class Metrics
    {
        private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

        [GaugeMetric]
        [Description("Nodes that are currently kept in cache (either persisted or not)")]
        public static long DirtyNodesCount { get; set; }

        [GaugeMetric]
        [Description("Nodes that are currently kept in cache (either persisted or not)")]
        public static long CachedNodesCount { get; set; }

        [GaugeMetric]
        [Description("Nodes that have been persisted since the session start.")]
        public static long PersistedNodeCount { get; set; }

        [GaugeMetric]
        [Description("Nodes that was removed via live pruning.")]
        public static long RemovedNodeCount { get; set; }

        [GaugeMetric]
        [Description("Nodes that have been committed since the session start. These nodes may have been pruned, persisted or replaced.")]
        public static long CommittedNodesCount { get; set; }

        [CounterMetric]
        [Description("Nodes that have been removed from the cache during pruning because they have been persisted before.")]
        public static long PrunedPersistedNodesCount;

        [CounterMetric]
        [Description("Nodes that have been removed from the cache during deep pruning because they have been persisted before.")]
        public static long DeepPrunedPersistedNodesCount;

        [CounterMetric]
        [Description("Nodes that have been removed from the cache during pruning because they were no longer needed.")]
        public static long PrunedTransientNodesCount { get; set; }

        // The block-processing thread keeps its dedicated padded word; every other thread (RPC
        // workers, prewarm workers) previously shared ONE "other" word per counter, making each
        // per-node increment a contended cross-core RMW under concurrent load — striped instead.
        [CounterMetric]
        [Description("Number of DB reads.")]
        public static long LoadedFromDbNodesCount => _mainLoadedFromDbNodesCount.Value + _otherLoadedFromDbNodesCount.Sum;
        private static CacheLinePaddedLong _mainLoadedFromDbNodesCount;
        private static readonly StripedLong _otherLoadedFromDbNodesCount = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void IncrementLoadedFromDbNodesCount()
        {
            if (IsBlockProcessingThread) Interlocked.Increment(ref _mainLoadedFromDbNodesCount.Value);
            else _otherLoadedFromDbNodesCount.Increment();
        }

        [CounterMetric]
        [Description("Number of reads from the node cache.")]
        public static long LoadedFromCacheNodesCount => _mainLoadedFromCacheNodesCount.Value + _otherLoadedFromCacheNodesCount.Sum;
        private static CacheLinePaddedLong _mainLoadedFromCacheNodesCount;
        private static readonly StripedLong _otherLoadedFromCacheNodesCount = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void IncrementLoadedFromCacheNodesCount()
        {
            if (IsBlockProcessingThread) Interlocked.Increment(ref _mainLoadedFromCacheNodesCount.Value);
            else _otherLoadedFromCacheNodesCount.Increment();
        }

        [CounterMetric]
        [Description("Number of reads from the RLP cache.")]
        public static long LoadedFromRlpCacheNodesCount { get; set; }

        [CounterMetric]
        [Description("Number of nodes that have been exactly the same as other nodes in the cache when committing.")]
        public static long ReplacedNodesCount { get; set; }

        [GaugeMetric]
        [Description("Time taken by the last snapshot persistence, in milliseconds.")]
        public static long SnapshotPersistenceTimeMs { get; set; }

        [GaugeMetric]
        [Description("Time taken by the last pruning, in milliseconds.")]
        public static long PruningTimeMs { get; set; }

        [GaugeMetric]
        [Description("Time taken by the last persisted node pruning, in milliseconds.")]
        public static long PersistedNodePruningTimeMs { get; set; }

        [GaugeMetric]
        [Description("Time taken by the last deep pruning, in milliseconds.")]
        public static long DeepPruningTimeMs { get; set; }

        [GaugeMetric]
        [Description("Last persisted block number (snapshot).")]
        public static ulong LastPersistedBlockNumber { get; set; }

        [GaugeMetric]
        [Description("Estimated memory used by cache.")]
        public static long DirtyMemoryUsedByCache { get; set; }

        [GaugeMetric]
        [Description("Estimated memory used by cache.")]
        public static long MemoryUsedByCache { get; set; }
    }
}
