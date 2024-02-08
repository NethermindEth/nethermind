// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning;

public class OverlayTrieStore : TrieStore
{
    private readonly IReadOnlyTrieStore _store;

    public OverlayTrieStore(IKeyValueStoreWithBatching? keyValueStore, IReadOnlyTrieStore store, ILogManager? logManager) : base(keyValueStore, logManager)
    {
        _store = store;
    }




    public override bool IsPersisted(in ValueHash256 keccak)
    {
        var isPersisted = base.IsPersisted(in keccak);
        if (!isPersisted)
        {
            isPersisted = _store.IsPersisted(in keccak);
        }
        return isPersisted;
    }


    public override TrieNode FindCachedOrUnknown(Hash256? hash)
    {
        TrieNode findCachedOrUnknown = base.FindCachedOrUnknown(hash);
        if (findCachedOrUnknown.NodeType == NodeType.Unknown)
        {
            findCachedOrUnknown = _store.FindCachedOrUnknown(hash);
        }

        return findCachedOrUnknown;
    }

    public override byte[] LoadRlp(Hash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        var rlp = base.TryLoadRlp(keccak, readFlags);
        if (rlp != null)
        {
            return rlp;
        }

        return _store.LoadRlp(keccak, readFlags);
    }

    public override byte[]? TryLoadRlp(Hash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        var rlp = base.TryLoadRlp(keccak, readFlags);
        if (rlp != null)
        {
            return rlp;
        }

        return _store.TryLoadRlp(keccak, readFlags);
    }

    public override byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        var hash = base.GetByHash(key, flags);
        if (hash != null)
        {
            return hash;
        }

        return _store.GetByHash(key, flags);
    }
}
