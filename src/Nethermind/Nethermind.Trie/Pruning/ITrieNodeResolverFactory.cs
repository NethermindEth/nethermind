// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Factory interface for creating scoped trie node resolvers.
/// Used to get a child resolver for a specific storage address from a parent resolver.
/// </summary>
public interface ITrieNodeResolverFactory
{
    /// <summary>
    /// Gets another trie node resolver for another trie. Used for tree traversal.
    /// If address is null, returns the state trie.
    /// </summary>
    /// <param name="address">Storage address (null for state trie).</param>
    /// <returns>Trie node resolver for the requested scope.</returns>
    ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address);
}