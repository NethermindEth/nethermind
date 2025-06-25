// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Trie.Pruning
{
    public class NoPruning : IPruningStrategy
    {
        private NoPruning() { }

        public static NoPruning Instance { get; } = new();

        public bool DeleteObsoleteKeys => false;
        public bool ShouldPruneDirtyNode(TrieStoreState state) => false;

        public bool ShouldPrunePersistedNode(TrieStoreState state) => false;
    }
}
