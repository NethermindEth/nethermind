// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class TrieNodeResolverWithReadFlags(ITrieNodeResolver baseResolver, ReadFlags defaultFlags) : ITrieNodeResolver, ITrieNodeResolverSource
{
    private readonly ITrieNodeResolver _baseResolver = baseResolver;
    private readonly ReadFlags _defaultFlags = defaultFlags;

    public TrieNode FindCachedOrUnknown(in TreePath treePath, in ValueHash256 hash) => _baseResolver.FindCachedOrUnknown(treePath, in hash);

    public byte[]? TryLoadRlp(in TreePath treePath, in ValueHash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (flags != ReadFlags.None)
        {
            return _baseResolver.TryLoadRlp(treePath, in hash, flags | _defaultFlags);
        }

        return _baseResolver.TryLoadRlp(treePath, in hash, _defaultFlags);
    }

    public byte[]? LoadRlp(in TreePath treePath, in ValueHash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (flags != ReadFlags.None)
        {
            return _baseResolver.LoadRlp(treePath, in hash, flags | _defaultFlags);
        }

        return _baseResolver.LoadRlp(treePath, in hash, _defaultFlags);
    }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => new TrieNodeResolverWithReadFlags(_baseResolver.GetStorageTrieNodeResolver(address), _defaultFlags);

    public INodeStorage.KeyScheme Scheme => _baseResolver.Scheme;

    public ITrieNodeResolver? GetReadOnlyTraversalResolver() =>
        _baseResolver is ITrieNodeResolverSource source
            && source.GetReadOnlyTraversalResolver() is ITrieNodeResolver readOnlyResolver
            ? new TrieNodeResolverWithReadFlags(readOnlyResolver, _defaultFlags)
            : null;
}
