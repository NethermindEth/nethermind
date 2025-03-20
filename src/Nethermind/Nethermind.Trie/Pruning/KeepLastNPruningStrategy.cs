// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public class KeepLastNPruningStrategy(IPruningStrategy baseStrategy, int depth) : IPruningStrategy
{
    public bool PruningEnabled => baseStrategy.PruningEnabled;

    public int MaxDepth => depth;
    public bool ShouldPruneDirtyNode(in long dirtyNodeMemory) => baseStrategy.ShouldPruneDirtyNode(in dirtyNodeMemory);

    public bool ShouldPrunePersistedNode(in long persistedNodeMemory) => baseStrategy.ShouldPrunePersistedNode(in persistedNodeMemory);

    public double PrunePersistedNodePortion => baseStrategy.PrunePersistedNodePortion;

    public long PrunePersistedNodeMinimumTarget => baseStrategy.PrunePersistedNodeMinimumTarget;

    public int TrackedPastKeyCount => baseStrategy.TrackedPastKeyCount;
}
