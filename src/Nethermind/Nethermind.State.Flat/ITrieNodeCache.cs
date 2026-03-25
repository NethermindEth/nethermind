// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public interface ITrieNodeCache
{
    /// <summary>
    /// Returns a leased <see cref="RefCountingTrieNode"/> for the given path and hash.
    /// Caller must dispose the returned node to release the lease. Returns <c>null</c> on miss.
    /// </summary>
    RefCountingTrieNode? TryGet(Hash256? address, in TreePath path, Hash256 hash);
    void Add(TransientResource transientResource);
    void Clear();

    /// <summary>Per-shard pools for <see cref="TrieNodeCache.ChildCache"/> to use.</summary>
    RefCountingTrieNodePool[] ShardPools { get; }
}
