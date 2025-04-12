// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public class ShardBitPruningStrategy(IPruningStrategy baseStrategy, int shardCount) : IPruningStrategy
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

    public double PrunePersistedNodePortion => baseStrategy.PrunePersistedNodePortion;

    public long PrunePersistedNodeMinimumTarget => baseStrategy.PrunePersistedNodeMinimumTarget;

    public int TrackedPastKeyCount => baseStrategy.TrackedPastKeyCount;

    public int ShardBit => shardCount;
}
