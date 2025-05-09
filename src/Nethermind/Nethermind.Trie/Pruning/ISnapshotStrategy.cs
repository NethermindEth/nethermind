// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public interface IPruningStrategy
    {
        bool PruningEnabled { get; }
        bool ShouldPruneDirtyNode(in long dirtyNodeMemory);
        bool ShouldPrunePersistedNode(in long persistedNodeMemory);
    }
}
