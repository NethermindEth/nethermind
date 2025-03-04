// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public class KeepLastNPruningStrategy(IPruningStrategy baseStrategy, int depth) : IPruningStrategy
{
    public bool PruningEnabled => baseStrategy.PruningEnabled;

    public int MaxDepth => depth;
    public bool ShouldPruneDirtyNode(in long dirtyNodeMemory)
    {
        return baseStrategy.ShouldPruneDirtyNode(in dirtyNodeMemory);
    }

    public bool ShouldPrunePersistedNode(in long persistedNodeMemory)
    {
        return baseStrategy.ShouldPrunePersistedNode(in persistedNodeMemory);
    }

    public int TrackedPastKeyCount => baseStrategy.TrackedPastKeyCount;
}
