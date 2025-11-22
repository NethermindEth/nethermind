// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public record CachedResource(
    ConcurrentDictionary<TreePath, TrieNode> TrieWarmerLoadedNodes,
    ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> LoadedStorageNodes,
    ConcurrentDictionary<(AddressAsKey, UInt256?), bool> PrewarmedAddresses
)
{
    public void Clear()
    {
        TrieWarmerLoadedNodes.Clear();
        LoadedStorageNodes.Clear();
        PrewarmedAddresses.Clear();
    }
}
