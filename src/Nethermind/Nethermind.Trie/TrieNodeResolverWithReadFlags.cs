// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class TrieNodeResolverWithReadFlags : ITrieNodeResolver
{
    private readonly ITrieNodeResolver _baseResolver;
    private readonly ReadFlags _defaultFlags;

    public TrieNodeResolverWithReadFlags(ITrieNodeResolver baseResolver, ReadFlags defaultFlags)
    {
        _baseResolver = baseResolver;
        _defaultFlags = defaultFlags;
    }

    public TrieNode FindCachedOrUnknown(Hash256 hash)
    {
        return _baseResolver.FindCachedOrUnknown(hash);
    }

    public T LoadRlp<T, TDeserializer>(TDeserializer deserializer, Hash256 hash, ReadFlags flags = ReadFlags.None) where TDeserializer : ISpanDeserializer<T>
    {
        flags |= _defaultFlags;

        return _baseResolver.LoadRlp<T, TDeserializer>(deserializer, hash, flags);
    }
}
