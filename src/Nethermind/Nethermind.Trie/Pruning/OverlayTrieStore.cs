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

    public override TrieNode FindCachedOrUnknown(Hash256? hash) => _store.FindCachedOrUnknown(hash);

    public override byte[] LoadRlp(Hash256 keccak, ReadFlags readFlags = ReadFlags.None) =>
        _store.TryLoadRlp(keccak, readFlags) ?? base.LoadRlp(keccak, readFlags);

    public override byte[]? TryLoadRlp(Hash256 keccak, ReadFlags readFlags = ReadFlags.None) =>
        _store.TryLoadRlp(keccak, readFlags) ?? base.TryLoadRlp(keccak, readFlags);

    public override byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
        _store.GetByHash(key, flags) ?? base.GetByHash(key, flags);
}
