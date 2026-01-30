// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Cache for trie node RLP data used during flat state operations.
/// </summary>
public interface ITrieNodeCache
{
    /// <summary>
    /// Tries to get a cached trie node.
    /// </summary>
    /// <param name="address">The account address (null for state trie).</param>
    /// <param name="path">The tree path to the node.</param>
    /// <param name="hash">The hash of the node.</param>
    /// <param name="node">When successful, the cached trie node.</param>
    /// <returns>True if the node was found in cache; otherwise false.</returns>
    bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node);

    /// <summary>
    /// Tries to get cached RLP data for a trie node.
    /// </summary>
    /// <param name="address">The account address (null for state trie).</param>
    /// <param name="path">The tree path to the node.</param>
    /// <param name="hash">The hash of the node.</param>
    /// <param name="rlp">When successful, the ref-counted RLP data. Caller must dispose to release the lease.</param>
    /// <returns>True if the node was found in cache; otherwise false.</returns>
    bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out RefCounterTrieNodeRlp? rlp);

    /// <summary>
    /// Adds a transient resource to be tracked by the cache.
    /// </summary>
    /// <param name="transientResource">The transient resource to track.</param>
    void Add(TransientResource transientResource);

    /// <summary>
    /// Clears all cached entries.
    /// </summary>
    void Clear();
}
