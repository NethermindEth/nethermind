// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class TrieNodeResolverWithReadFlags: ITrieNodeResolver
{
    private ITrieStore _baseResolver;
    private ReadFlags _flags;

    public TrieNodeResolverWithReadFlags(ITrieStore baseResolver, ReadFlags flags)
    {
        _baseResolver = baseResolver;
        _flags = flags;
    }

    public TrieNode FindCachedOrUnknown(Keccak hash)
    {
        return _baseResolver.FindCachedOrUnknown(hash);
    }

    public byte[]? LoadRlp(Keccak hash)
    {
        return _baseResolver.Get(hash.Bytes, _flags);
    }
}
