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

    public TrieNode FindCachedOrUnknown(Hash256 hash) =>
        _baseResolver.FindCachedOrUnknown(hash);

    public byte[]? TryLoadRlp(Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        flags != ReadFlags.None
            ? _baseResolver.TryLoadRlp(hash, flags | _defaultFlags)
            : _baseResolver.TryLoadRlp(hash, _defaultFlags);

    public byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
        flags != ReadFlags.None
            ? _baseResolver.GetByHash(key, flags | _defaultFlags)
            : _baseResolver.GetByHash(key, _defaultFlags);

    public byte[]? LoadRlp(Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        flags != ReadFlags.None
            ? _baseResolver.LoadRlp(hash, flags | _defaultFlags)
            : _baseResolver.LoadRlp(hash, _defaultFlags);
}
