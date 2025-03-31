// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public class TrackedPastKeyCountStrategy(IPruningStrategy baseStrategy, int trackedPastKeyCount) : IPruningStrategy
{
    public bool PruningEnabled => baseStrategy.PruningEnabled;
    public int MaxDepth => baseStrategy.MaxDepth;
    public bool ShouldPruneDirtyNode(in long dirtyNodeMemory) => baseStrategy.ShouldPruneDirtyNode(in dirtyNodeMemory);

    public bool ShouldPrunePersistedNode(in long persistedNodeMemory) => baseStrategy.ShouldPrunePersistedNode(in persistedNodeMemory);

    public double PrunePersistedNodePortion => baseStrategy.PrunePersistedNodePortion;

    public long PrunePersistedNodeMinimumTarget => baseStrategy.PrunePersistedNodeMinimumTarget;

    public int TrackedPastKeyCount => trackedPastKeyCount;

    public int ShardBit => baseStrategy.ShardBit;
}
