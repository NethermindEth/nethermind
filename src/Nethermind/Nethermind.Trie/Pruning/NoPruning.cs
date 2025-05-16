// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Trie.Pruning
{
    public class NoPruning : IPruningStrategy
    {
        private NoPruning() { }

        public static NoPruning Instance { get; } = new();

        public bool PruningEnabled => false;
        public bool ShouldPruneDirtyNode(in long dirtyNodeMemory) => false;

        public bool ShouldPrunePersistedNode(in long persistedNodeMemory) => false;
    }
}
