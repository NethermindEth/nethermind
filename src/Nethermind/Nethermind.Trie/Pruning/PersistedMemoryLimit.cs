// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Trie.Pruning;

[DebuggerDisplay("{persistedMemoryLimit/(1024*1024)} MB")]
public class PersistedMemoryLimit(IPruningStrategy baseStrategy, long persistedMemoryLimit) : IPruningStrategy
{
    public bool DeleteObsoleteKeys => baseStrategy.DeleteObsoleteKeys;

    public bool ShouldPruneDirtyNode(TrieStoreState state) => baseStrategy.ShouldPruneDirtyNode(state);

    public bool ShouldPrunePersistedNode(TrieStoreState state) => (state.PersistedCacheMemory >= persistedMemoryLimit);
}
