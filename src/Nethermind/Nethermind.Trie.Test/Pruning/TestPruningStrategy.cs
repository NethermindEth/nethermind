// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Test.Pruning
{
    public class TestPruningStrategy(
        bool pruningEnabled,
        bool shouldPrune = false)
        : IPruningStrategy
    {
        public bool PruningEnabled => pruningEnabled;
        public bool ShouldPruneDirtyNode(TrieStoreState state) => pruningEnabled && (ShouldPruneEnabled || WithMemoryLimit is not null && state.DirtyCacheMemory > WithMemoryLimit);
        public bool ShouldPrunePersistedNode(TrieStoreState state) => pruningEnabled && (ShouldPrunePersistedEnabled || WithPersistedMemoryLimit is not null && state.PersistedCacheMemory > WithPersistedMemoryLimit);

        public bool ShouldPruneEnabled { get; set; } = shouldPrune;
        public bool ShouldPrunePersistedEnabled { get; set; } = shouldPrune;

        public long? WithMemoryLimit { get; set; }
        public long? WithPersistedMemoryLimit { get; set; }

    }
}
