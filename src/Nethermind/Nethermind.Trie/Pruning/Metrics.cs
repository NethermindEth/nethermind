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

namespace Nethermind.Trie.Pruning
{
    public static class Metrics
    {
        public static long CachedNodesCount { get; set; }
        
        public static long PersistedNodeCount { get; set; }
        
        public static long CommittedNodesCount { get; set; }
        
        public static long PrunedPersistedNodesCount { get; set; }
        
        public static long DeepPrunedPersistedNodesCount { get; set; }
        
        public static long PrunedTransientNodesCount { get; set; }
        
        public static long LoadedFromDbNodesCount { get; set; }
        
        public static long LoadedFromCacheNodesCount { get; set; }
        
        public static long LoadedFromRlpCacheNodesCount { get; set; }
        
        public static long ReplacedNodesCount { get; set; }
        
        public static long SnapshotPersistenceTime { get; set; }
        
        public static long PruningTime { get; set; }
        
        public static long DeepPruningTime { get; set; }
        
        public static long LastPersistedBlockNumber { get; set; }
        
        public static long MemoryUsedByCache { get; set; }
        
        public static long MemoryUsedByPersistedCache { get; set; }
        
        public static long MemoryUsedByDeepCache { get; set; }
    }
}