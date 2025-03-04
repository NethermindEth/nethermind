// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public class TrackedPastKeyCountStrategy(IPruningStrategy baseStrategy, int trackedPastKeyCount) : IPruningStrategy
{
    public bool PruningEnabled => baseStrategy.PruningEnabled;
    public int MaxDepth => baseStrategy.MaxDepth;
    public bool ShouldPruneDirtyNode(in long dirtyNodeMemory)
    {
        return baseStrategy.ShouldPruneDirtyNode(in dirtyNodeMemory);
    }

    public bool ShouldPrunePersistedNode(in long persistedNodeMemory)
    {
        return baseStrategy.ShouldPrunePersistedNode(in persistedNodeMemory);
    }

    public int TrackedPastKeyCount => trackedPastKeyCount;
}
