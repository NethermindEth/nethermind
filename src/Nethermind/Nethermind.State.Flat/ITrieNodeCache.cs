// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public interface ITrieNodeCache
{
    bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node);

    /// <summary>Same as <see cref="TryGet(Hash256?, in TreePath, Hash256, out TrieNode?)"/>, for a caller that already
    /// has the shard and hash from <see cref="TrieNodeCache.GetShardAndHashCode"/>.</summary>
    /// <remarks><paramref name="shardIdx"/> and <paramref name="hashCode"/> must be what <see cref="TrieNodeCache.GetShardAndHashCode"/>
    /// returns for <paramref name="address"/> and <paramref name="path"/>; the cache uses them in place of the key.</remarks>
    bool TryGet(Hash256? address, in TreePath path, int shardIdx, int hashCode, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) =>
        TryGet(address, path, hash, out node);
    void Add(TransientResource transientResource);
    void Clear();
}
