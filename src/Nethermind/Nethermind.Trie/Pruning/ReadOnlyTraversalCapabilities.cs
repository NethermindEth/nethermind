// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>Resolver capable of providing a shared read-only traversal resolver.</summary>
public interface ITrieNodeResolverSource
{
    ITrieNodeResolver? GetReadOnlyTraversalResolver();
}

/// <summary>Full-store capability to hand out an address-scoped shared read-only resolver.</summary>
public interface IScopedReadOnlyTraversalProvider
{
    ITrieNodeResolver? GetReadOnlyTraversalResolver(Hash256? address);
}

internal static class TrieNodeResolverExtensions
{
    public static ITrieNodeResolver AsReadOnlyTraversal(this ITrieNodeResolver self) =>
        self is ITrieNodeResolverSource source && source.GetReadOnlyTraversalResolver() is { } shared
            ? shared
            : self;
}
