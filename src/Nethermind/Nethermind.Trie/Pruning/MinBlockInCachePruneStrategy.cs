// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public class MinBlockInCachePruneStrategy(IPruningStrategy baseStrategy, long minBlockFromPersisted, long pruneBoundary) : IPruningStrategy
{
    public bool DeleteObsoleteKeys => baseStrategy.DeleteObsoleteKeys;

    public bool ShouldPruneDirtyNode(TrieStoreState state)
    {
        long reorgBoundary = state.LatestCommittedBlock - pruneBoundary;
        // Never persist snapshot if too little block in cache. Prevent taking snapshot too often.
        if (reorgBoundary - state.LastPersistedBlock < minBlockFromPersisted) return false;
        return baseStrategy.ShouldPruneDirtyNode(state);
    }

    public bool ShouldPrunePersistedNode(TrieStoreState state) => baseStrategy.ShouldPrunePersistedNode(state);
}
