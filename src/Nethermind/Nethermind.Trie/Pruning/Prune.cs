// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public static class Prune
    {
        public static IPruningStrategy WhenCacheReaches(long dirtySizeInBytes)
            => new MemoryLimit(dirtySizeInBytes);

        public static IPruningStrategy WhenPersistedCacheReaches(this IPruningStrategy baseStrategy, long persistedMemoryLimit)
            => new PersistedMemoryLimit(baseStrategy, persistedMemoryLimit);

        public static IPruningStrategy DontDeleteObsoleteNode(this IPruningStrategy baseStrategy)
            => new DontDeleteObsoleteNodeStrategy(baseStrategy);
    }

    public class DontDeleteObsoleteNodeStrategy(IPruningStrategy baseStrategy) : IPruningStrategy
    {
        // Its this thing
        public bool PruningEnabled => false;

        public bool ShouldPruneDirtyNode(TrieStoreState state) => baseStrategy.ShouldPruneDirtyNode(state);

        public bool ShouldPrunePersistedNode(TrieStoreState state) => baseStrategy.ShouldPrunePersistedNode(state);
    }
}
