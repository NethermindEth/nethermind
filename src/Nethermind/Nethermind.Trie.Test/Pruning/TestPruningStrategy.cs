// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Test.Pruning
{
    public class TestPruningStrategy(
        bool shouldPrune = false,
        bool deleteObsoleteKeys = false,
        int? pruneInterval = null)
        : IPruningStrategy
    {
        public bool DeleteObsoleteKeys => deleteObsoleteKeys;
        public bool ShouldPruneDirtyNode(TrieStoreState state)
        {
            if (pruneInterval is not null && state.LatestCommittedBlock % pruneInterval == 0)
            {
                return true;
            }
            return (ShouldPruneEnabled || WithMemoryLimit is not null && state.DirtyCacheMemory > WithMemoryLimit);
        }

        public bool ShouldPrunePersistedNode(TrieStoreState state) => (ShouldPrunePersistedEnabled || WithPersistedMemoryLimit is not null && state.PersistedCacheMemory > WithPersistedMemoryLimit);

        public bool ShouldPruneEnabled { get; set; } = shouldPrune;
        public bool ShouldPrunePersistedEnabled { get; set; } = shouldPrune;

        public long? WithMemoryLimit { get; set; }
        public long? WithPersistedMemoryLimit { get; set; }

    }
}
