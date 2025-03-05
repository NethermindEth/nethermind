// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Test.Pruning
{
    public class TestPruningStrategy(
        bool pruningEnabled,
        bool shouldPrune = false,
        int? maxDepth = null,
        int trackedPastKeyCount = 0)
        : IPruningStrategy
    {
        public bool PruningEnabled => pruningEnabled;
        public int MaxDepth { get; set; } = maxDepth ?? (int)Reorganization.MaxDepth;
        public bool ShouldPruneDirtyNode(in long currentMemory) => pruningEnabled && (ShouldPruneEnabled || WithMemoryLimit is not null && currentMemory > WithMemoryLimit);
        public bool ShouldPrunePersistedNode(in long persistedNodeMemory) => pruningEnabled && (ShouldPruneEnabled || WithMemoryLimit is not null && persistedNodeMemory > WithPersistedMemoryLimit);

        public double PrunePersistedNodePortion { get; set; } = 1.0;
        public long PrunePersistedNodeMinimumTarget { get; set; } = long.MaxValue;

        public bool ShouldPruneEnabled { get; set; } = shouldPrune;

        public int? WithMemoryLimit { get; set; }
        public int? WithPersistedMemoryLimit { get; set; }

        public int TrackedPastKeyCount => trackedPastKeyCount;
    }
}
