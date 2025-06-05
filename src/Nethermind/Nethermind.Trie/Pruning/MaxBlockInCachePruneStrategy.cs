// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie.Pruning;

public class MaxBlockInCachePruneStrategy(IPruningStrategy baseStrategy, long maxBlockFromPersisted, long pruneBoundary) : IPruningStrategy
{
    public bool DeleteObsoleteKeys => baseStrategy.DeleteObsoleteKeys;

    public bool ShouldPruneDirtyNode(TrieStoreState state)
    {
        long reorgBoundary = state.LatestCommittedBlock - pruneBoundary;
        // Persist snapshot if the last persisted block is too old. Prevent very long memory prune
        if (reorgBoundary - state.LastPersistedBlock >= maxBlockFromPersisted) return true;
        return baseStrategy.ShouldPruneDirtyNode(state);
    }

    public bool ShouldPrunePersistedNode(TrieStoreState state) => baseStrategy.ShouldPrunePersistedNode(state);
}
