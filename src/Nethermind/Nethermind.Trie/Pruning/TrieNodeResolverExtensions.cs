// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

internal static class TrieNodeResolverExtensions
{
    public static ITrieNodeResolver AsReadOnlyTraversal(this ITrieNodeResolver self) =>
        self is ITrieNodeResolverSource source && source.GetReadOnlyTraversalResolver() is { } shared
            ? shared
            : self;
}
