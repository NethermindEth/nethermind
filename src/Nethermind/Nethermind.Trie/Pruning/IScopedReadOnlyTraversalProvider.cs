// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Capability for a full <see cref="ITrieStore"/> that can hand out an address-scoped resolver
/// suitable for shared cached-node read-only traversal. Implemented by stores that opt in to
/// the <see cref="ITrieNodeResolverSource"/> fast path through their <see cref="ScopedTrieStore"/>
/// wrappers.
/// </summary>
public interface IScopedReadOnlyTraversalProvider
{
    ITrieNodeResolver? GetReadOnlyTraversalResolver(Hash256? address);
}
