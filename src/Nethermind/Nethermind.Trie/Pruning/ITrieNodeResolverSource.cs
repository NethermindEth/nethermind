// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Optional capability for trie resolvers that can provide a resolver for shared read-only traversal.
/// </summary>
public interface ITrieNodeResolverSource
{
    /// <summary>
    /// Returns a resolver that may share immutable cached trie nodes for synchronous read-only traversal,
    /// or <see langword="null"/> when the wrapped resolver cannot preserve that behavior.
    /// </summary>
    ITrieNodeResolver? GetReadOnlyTraversalResolver();
}
