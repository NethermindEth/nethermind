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
        public bool ShouldPruneDirtyNode(in long currentMemory) => pruningEnabled && (ShouldPruneEnabled || WithMemoryLimit is not null && currentMemory > WithMemoryLimit);
        public bool ShouldPrunePersistedNode(in long persistedNodeMemory) => pruningEnabled && (ShouldPrunePersistedEnabled || WithPersistedMemoryLimit is not null && persistedNodeMemory > WithPersistedMemoryLimit);

        public bool ShouldPruneEnabled { get; set; } = shouldPrune;
        public bool ShouldPrunePersistedEnabled { get; set; } = shouldPrune;

        public long? WithMemoryLimit { get; set; }
        public long? WithPersistedMemoryLimit { get; set; }

    }
}
