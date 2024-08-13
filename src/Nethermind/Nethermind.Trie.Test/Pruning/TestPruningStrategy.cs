// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Test.Pruning
{
    public class TestPruningStrategy : IPruningStrategy
    {
        private readonly bool _pruningEnabled;
        private readonly int _trackedPastKeyCount;

        public TestPruningStrategy(bool pruningEnabled, bool shouldPrune = false, int? maxDepth = null, int trackedPastKeyCount = 0)
        {
            _pruningEnabled = pruningEnabled;
            ShouldPruneEnabled = shouldPrune;
            _trackedPastKeyCount = trackedPastKeyCount;
            MaxDepth = maxDepth ?? (int)Reorganization.MaxDepth;
        }

        public bool PruningEnabled => _pruningEnabled;
        public int MaxDepth { get; set; }
        public bool ShouldPruneEnabled { get; set; }

        public int? WithMemoryLimit { get; set; }

        public bool ShouldPrune(in long currentMemory)
        {
            if (!_pruningEnabled) return false;
            if (ShouldPruneEnabled) return true;
            if (WithMemoryLimit is not null && currentMemory > WithMemoryLimit) return true;

            return false;
        }

        public int TrackedPastKeyCount => _trackedPastKeyCount;
    }
}
