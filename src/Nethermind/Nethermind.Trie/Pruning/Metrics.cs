// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Trie.Pruning
{
    public static class Metrics
    {
        [GaugeMetric]
        [Description("Nodes that are currently kept in cache (either persisted or not)")]
        public static long CachedNodesCount { get; set; }

        [GaugeMetric]
        [Description("Nodes that have been persisted since the session start.")]
        public static long PersistedNodeCount { get; set; }

        [GaugeMetric]
        [Description("Nodes that have been committed since the session start. These nodes may have been pruned, persisted or replaced.")]
        public static long CommittedNodesCount { get; set; }

        [CounterMetric]
        [Description("Nodes that have been removed from the cache during pruning because they have been persisted before.")]
        public static long PrunedPersistedNodesCount { get; set; }

        [CounterMetric]
        [Description("Nodes that have been removed from the cache during deep pruning because they have been persisted before.")]
        public static long DeepPrunedPersistedNodesCount { get; set; }

        [CounterMetric]
        [Description("Nodes that have been removed from the cache during pruning because they were no longer needed.")]
        public static long PrunedTransientNodesCount { get; set; }

        [CounterMetric]
        [Description("Number of DB reads.")]
        public static long LoadedFromDbNodesCount { get; set; }

        [CounterMetric]
        [Description("Number of reads from the node cache.")]
        public static long LoadedFromCacheNodesCount { get; set; }

        [CounterMetric]
        [Description("Number of reads from the RLP cache.")]
        public static long LoadedFromRlpCacheNodesCount { get; set; }

        [CounterMetric]
        [Description("Number of nodes that have been exactly the same as other nodes in the cache when committing.")]
        public static long ReplacedNodesCount { get; set; }

        [GaugeMetric]
        [Description("Time taken by the last snapshot persistence.")]
        public static long SnapshotPersistenceTime { get; set; }

        [GaugeMetric]
        [Description("Time taken by the last pruning.")]
        public static long PruningTime { get; set; }

        [GaugeMetric]
        [Description("Time taken by the last deep pruning.")]
        public static long DeepPruningTime { get; set; }

        [GaugeMetric]
        [Description("Last persisted block number (snapshot).")]
        public static long LastPersistedBlockNumber { get; set; }

        [GaugeMetric]
        [Description("Estimated memory used by cache.")]
        public static long MemoryUsedByCache { get; set; }

        [GaugeMetric]
        [Description("Time taken by the last pruning mark phase.")]
        public static long MarkPruningTime { get; set; }

        [GaugeMetric]
        [Description("Number of nodes that have been marked for pruning in last mark phase.")]
        public static int MarkedNodesCount { get; set; }

        [GaugeMetric]
        [Description("Number of bytes that have been pruned during last sweep phase.")]
        public static long LatPrunedMemory { get; set; }
    }
}
