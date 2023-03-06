// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Provides a hint for the purpose of <see cref="ITrieNodeResolver.FindCachedOrUnknown"/> getting found that
/// can be memoized further as a hint for caching the node or not.
/// </summary>
public enum LoadHint : byte
{
    /// <summary>
    /// No meaningful hint can be provided about the node.
    /// </summary>
    None = 0,

    /// <summary>
    /// The node is a child node of another node.
    /// </summary>
    StorageChildNode = 1,

    /// <summary>
    /// The node is of <see cref="TrieType.Storage"/> trie.
    /// </summary>
    StorageRoot = 2,

    /// <summary>
    /// The node is a root of <see cref="TrieType.State"/> trie.
    /// </summary>
    StateRoot = 3,
}
