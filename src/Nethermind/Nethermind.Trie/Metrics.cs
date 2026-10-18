// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

namespace Nethermind.Trie
{
    public static class Metrics
    {
        private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

        // The block-processing thread keeps its dedicated padded word; every other thread (RPC
        // workers, prewarm workers) previously shared ONE "other" word per counter, making each
        // increment a contended cross-core RMW under concurrent load — striped instead.
        [CounterMetric]
        [Description("Number of trie node hash calculations.")]
        public static long TreeNodeHashCalculations => _mainTreeNodeHashCalculations.Value + _otherTreeNodeHashCalculations.Sum;
        private static CacheLinePaddedLong _mainTreeNodeHashCalculations;
        private static readonly StripedLong _otherTreeNodeHashCalculations = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeHashCalculations()
        {
            if (IsBlockProcessingThread) Interlocked.Increment(ref _mainTreeNodeHashCalculations.Value);
            else _otherTreeNodeHashCalculations.Increment();
        }

        [CounterMetric]
        [Description("Number of trie node RLP encodings.")]
        public static long TreeNodeRlpEncodings => _mainTreeNodeRlpEncodings.Value + _otherTreeNodeRlpEncodings.Sum;
        private static CacheLinePaddedLong _mainTreeNodeRlpEncodings;
        private static readonly StripedLong _otherTreeNodeRlpEncodings = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpEncodings()
        {
            if (IsBlockProcessingThread) Interlocked.Increment(ref _mainTreeNodeRlpEncodings.Value);
            else _otherTreeNodeRlpEncodings.Increment();
        }

        [CounterMetric]
        [Description("Number of trie node RLP decodings.")]
        public static long TreeNodeRlpDecodings => _mainTreeNodeRlpDecodings.Value + _otherTreeNodeRlpDecodings.Sum;
        private static CacheLinePaddedLong _mainTreeNodeRlpDecodings;
        private static readonly StripedLong _otherTreeNodeRlpDecodings = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpDecodings()
        {
            if (IsBlockProcessingThread) Interlocked.Increment(ref _mainTreeNodeRlpDecodings.Value);
            else _otherTreeNodeRlpDecodings.Increment();
        }

        [GaugeMetric]
        [Description("Bytes of trie node log generations readable in place.")]
        public static long TrieNodeLogMemory { get; set; }

        [GaugeMetric]
        [Description("Number of trie node log generations readable in place.")]
        public static long TrieNodeLogGenerations { get; set; }

        [GaugeMetric]
        [Description("Bytes of the records and tables kept by compacted trie node log generations.")]
        public static long TrieNodeLogCompressedMemory { get; set; }

        [GaugeMetric]
        [Description("Number of compacted trie node log generations held.")]
        public static long TrieNodeLogCompressedGenerations { get; set; }

        [CounterMetric]
        [Description("Number of trie root lookups resolved by the trie node log.")]
        public static long TrieNodeLogRootHits => _trieNodeLogRootHits.Sum;
        private static readonly StripedLong _trieNodeLogRootHits = new();
        internal static void IncrementTrieNodeLogRootHits() => _trieNodeLogRootHits.Increment();

        [CounterMetric]
        [Description("Number of trie root lookups not resolved by the trie node log.")]
        public static long TrieNodeLogRootMisses => _trieNodeLogRootMisses.Sum;
        private static readonly StripedLong _trieNodeLogRootMisses = new();
        internal static void IncrementTrieNodeLogRootMisses() => _trieNodeLogRootMisses.Increment();

        [CounterMetric]
        [Description("Number of node lookups served by the normal per-block warmed-node cache, including warm-up reads.")]
        public static long WarmedNodeCacheHits => _warmedNodeCacheHits.Sum;
        private static readonly StripedLong _warmedNodeCacheHits = new();
        internal static void IncrementWarmedNodeCacheHits() => _warmedNodeCacheHits.Increment();

        [CounterMetric]
        [Description("Number of node lookups missing from the normal per-block warmed-node cache, including warm-up reads.")]
        public static long WarmedNodeCacheMisses => _warmedNodeCacheMisses.Sum;
        private static readonly StripedLong _warmedNodeCacheMisses = new();
        internal static void IncrementWarmedNodeCacheMisses() => _warmedNodeCacheMisses.Increment();

        [CounterMetric]
        [Description("Number of node lookups served by the idle storage worker's private warmed-node cache.")]
        public static long IdleWarmedNodeCacheHits => _idleWarmedNodeCacheHits.Sum;
        private static readonly StripedLong _idleWarmedNodeCacheHits = new();
        internal static void IncrementIdleWarmedNodeCacheHits() => _idleWarmedNodeCacheHits.Increment();

        [CounterMetric]
        [Description("Number of node lookups missing from the idle storage worker's private warmed-node cache.")]
        public static long IdleWarmedNodeCacheMisses => _idleWarmedNodeCacheMisses.Sum;
        private static readonly StripedLong _idleWarmedNodeCacheMisses = new();
        internal static void IncrementIdleWarmedNodeCacheMisses() => _idleWarmedNodeCacheMisses.Increment();

        [CounterMetric]
        [Description("Number of trie nodes a bulk set read in place from the trie node log.")]
        public static long BulkSetNodesFromLog => _bulkSetNodesFromLog.Sum;
        private static readonly StripedLong _bulkSetNodesFromLog = new();
        internal static void AddBulkSetNodesFromLog(long count) => _bulkSetNodesFromLog.Add(count);

        [CounterMetric]
        [Description("Number of trie nodes a bulk set loaded from its node source because the trie node log did not hold them.")]
        public static long BulkSetNodesFromSource => _bulkSetNodesFromSource.Sum;
        private static readonly StripedLong _bulkSetNodesFromSource = new();
        internal static void AddBulkSetNodesFromSource(long count) => _bulkSetNodesFromSource.Add(count);
    }
}
