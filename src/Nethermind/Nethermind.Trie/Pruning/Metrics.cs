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

        [GaugeMetric]
        [Description("Estimated memory used by cache.")]
        public static long MemoryUsedByCache { get; set; }
    }
}
