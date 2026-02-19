// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Noop factory that throws when storage resolver is requested.
/// Used as default when caller doesn't need storage traversal.
/// </summary>
public class NullTrieNodeResolverFactory : ITrieNodeResolverFactory
{
    public static readonly NullTrieNodeResolverFactory Instance = new();

    private NullTrieNodeResolverFactory() { }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        return NullTrieNodeResolver.Instance;
    }
}