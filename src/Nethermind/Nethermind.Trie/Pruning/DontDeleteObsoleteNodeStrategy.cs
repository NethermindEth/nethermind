// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public class DontDeleteObsoleteNodeStrategy(IPruningStrategy baseStrategy) : IPruningStrategy
{
    // Its this thing that prevent archive node from deleting old nodes, combined with `Persist.EveryBlock`.
    public bool DeleteObsoleteKeys => false;

    public bool ShouldPruneDirtyNode(TrieStoreState state) => baseStrategy.ShouldPruneDirtyNode(state);

    public bool ShouldPrunePersistedNode(TrieStoreState state) => baseStrategy.ShouldPrunePersistedNode(state);
}
