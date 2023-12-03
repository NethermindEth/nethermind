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

    public TrieNode FindCachedOrUnknown(in TreePath treePath, Hash256 hash)
    {
        return _baseResolver.FindCachedOrUnknown(treePath, hash);
    }

    public byte[]? LoadRlp(in TreePath treePath, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (flags != ReadFlags.None)
        {
            return _baseResolver.LoadRlp(treePath, hash, flags | _defaultFlags);
        }

        return _baseResolver.LoadRlp(treePath, hash, _defaultFlags);
    }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address)
    {
        return _baseResolver.GetStorageTrieNodeResolver(address);
    }

    public INodeStorage.KeyScheme Scheme => _baseResolver.Scheme;
}
