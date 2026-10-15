// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;

namespace Nethermind.State.Flat;

public interface ITrieNodeCache
{
    /// <summary>Finds the cached record of the node at <paramref name="path"/> whose hash is <paramref name="hash"/>.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record);
    void Add(TransientResource transientResource);
    void Clear();
}
