// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning;

public class OverlayTrieStore(IKeyValueStoreWithBatching? keyValueStore, IReadOnlyTrieStore store, ILogManager? logManager) : TrieStore(keyValueStore, logManager)
{
    public override bool IsPersisted(in ValueHash256 keccak) =>
        base.IsPersisted(in keccak) || store.IsPersisted(in keccak);

    public override TrieNode FindCachedOrUnknown(Hash256 hash)
    {
        TrieNode node = base.FindCachedOrUnknown(hash);
        return node.NodeType == NodeType.Unknown ? store.FindCachedOrUnknown(hash) : node;
    }

    public override byte[]? LoadRlp(Hash256 keccak, ReadFlags readFlags = ReadFlags.None) =>
        base.TryLoadRlp(keccak, readFlags) ?? store.LoadRlp(keccak, readFlags);

    public override byte[]? TryLoadRlp(Hash256 keccak, ReadFlags readFlags = ReadFlags.None) =>
        base.TryLoadRlp(keccak, readFlags) ?? store.TryLoadRlp(keccak, readFlags);

    public override byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
        base.GetByHash(key, flags) ?? store.GetByHash(key, flags);
}
