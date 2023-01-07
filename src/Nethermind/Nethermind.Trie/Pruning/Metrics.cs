// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Trie.Pruning
{
    public static class Metrics
    {
        [Description("Nodes that are currently kept in cache (either persisted or not)")]
        public static long CachedNodesCount { get; set; }

        [Description("Nodes that have been persisted since the session start.")]
        public static long PersistedNodeCount { get; set; }

        [Description("Nodes that have been committed since the session start. These nodes may have been pruned, persisted or replaced.")]
        public static long CommittedNodesCount { get; set; }

        [Description("Nodes that have been removed from the cache during pruning because they have been persisted before.")]
        public static long PrunedPersistedNodesCount { get; set; }

        [Description("Nodes that have been removed from the cache during deep pruning because they have been persisted before.")]
        public static long DeepPrunedPersistedNodesCount { get; set; }

        [Description("Nodes that have been removed from the cache during pruning because they were no longer needed.")]
        public static long PrunedTransientNodesCount { get; set; }

        [Description("Number of DB reads.")]
        public static long LoadedFromDbNodesCount { get; set; }

        [Description("Number of reads from the node cache.")]
        public static long LoadedFromCacheNodesCount { get; set; }

        [Description("Number of redas from the RLP cache.")]
        public static long LoadedFromRlpCacheNodesCount { get; set; }

        [Description("Number of nodes that have been exactly the same as other nodes in the cache when committing.")]
        public static long ReplacedNodesCount { get; set; }

        [Description("Time taken by the last snapshot persistence.")]
        public static long SnapshotPersistenceTime { get; set; }

        [Description("Time taken by the last pruning.")]
        public static long PruningTime { get; set; }

        [Description("Time taken by the last deep pruning.")]
        public static long DeepPruningTime { get; set; }

        [Description("Last persisted block number (snapshot).")]
        public static long LastPersistedBlockNumber { get; set; }

        [Description("Estimated memory used by cache.")]
        public static long MemoryUsedByCache { get; set; }
    }
}
