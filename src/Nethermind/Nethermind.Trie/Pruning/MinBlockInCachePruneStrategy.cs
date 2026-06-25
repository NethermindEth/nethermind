// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Trie.Pruning;

public class MinBlockInCachePruneStrategy(IPruningStrategy baseStrategy, ulong minBlockFromPersisted, ulong pruneBoundary) : IPruningStrategy
{
    public bool DeleteObsoleteKeys => baseStrategy.DeleteObsoleteKeys;

    public bool ShouldPruneDirtyNode(TrieStoreState state)
    {
        ulong reorgBoundary = state.LatestCommittedBlock.SaturatingSub(pruneBoundary);
        // Never persist snapshot if too little block in cache. Prevent taking snapshot too often.
        if (reorgBoundary.SaturatingSub(state.LastPersistedBlock) < minBlockFromPersisted) return false;
        return baseStrategy.ShouldPruneDirtyNode(state);
    }

    public bool ShouldPrunePersistedNode(TrieStoreState state) => baseStrategy.ShouldPrunePersistedNode(state);
}
