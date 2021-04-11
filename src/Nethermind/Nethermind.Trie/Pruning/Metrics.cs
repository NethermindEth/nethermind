//  Copyright (c) 2020 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
