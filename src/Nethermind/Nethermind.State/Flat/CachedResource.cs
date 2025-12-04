// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public record CachedResource(
    ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> LoadedNodes
)
{
    public void Clear()
    {
        LoadedNodes.Clear();
    }
}
