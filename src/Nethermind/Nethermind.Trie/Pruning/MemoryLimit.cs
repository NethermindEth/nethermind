// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;

namespace Nethermind.Trie.Pruning
{
    [DebuggerDisplay("{dirtyMemoryLimit/(1024*1024)} MB")]
    public class MemoryLimit(long dirtyMemoryLimit) : IPruningStrategy
    {
        public bool DeleteObsoleteKeys => true;
        public bool ShouldPruneDirtyNode(TrieStoreState state) => state.DirtyCacheMemory >= dirtyMemoryLimit;
        public bool ShouldPrunePersistedNode(TrieStoreState state) => false;
    }
}
