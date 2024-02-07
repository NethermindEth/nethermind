// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class TrieNodeResolverWithReadFlags : ITrieNodeResolver
{
    private readonly ITrieStore _baseResolver;
    private readonly ReadFlags _defaultFlags;

    public TrieNodeResolverWithReadFlags(ITrieStore baseResolver, ReadFlags defaultFlags)
    {
        _baseResolver = baseResolver;
        _defaultFlags = defaultFlags;
    }

    public TrieNodeResolverCapability Capability => _baseResolver.Capability;

    public bool IsPersisted(Hash256 hash, byte[] nodePathNibbles)
    {
        return _baseResolver.IsPersisted(hash, nodePathNibbles);
    }

    public TrieNode FindCachedOrUnknown(Hash256 hash)
    {
        return _baseResolver.FindCachedOrUnknown(hash);
    }

    public TrieNode FindCachedOrUnknown(Hash256 hash, Span<byte> nodePath, Span<byte> storagePrefix)
    {
        return _baseResolver.FindCachedOrUnknown(hash, nodePath, storagePrefix);
    }

    public TrieNode? FindCachedOrUnknown(Span<byte> nodePath, byte[] storagePrefix, Hash256 rootHash)
    {
        return _baseResolver.FindCachedOrUnknown(nodePath, storagePrefix, rootHash);
    }

    public byte[]? TryLoadRlp(Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (flags != ReadFlags.None)
        {
            return _baseResolver.TryLoadRlp(hash, flags | _defaultFlags);
        }

        return _baseResolver.TryLoadRlp(hash, _defaultFlags);
    }

    public byte[]? LoadRlp(Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (flags != ReadFlags.None)
        {
            return _baseResolver.LoadRlp(hash, flags | _defaultFlags);
        }

        return _baseResolver.LoadRlp(hash, _defaultFlags);
    }

    public byte[]? LoadRlp(Span<byte> nodePath, Hash256 rootHash = null)
    {
        return _baseResolver.LoadRlp(nodePath, rootHash);
    }

    public byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore)
    {
        return _baseResolver.TryLoadRlp(path, keyValueStore);
    }
}
