// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning;

public class OverlayTrieStore(IKeyValueStoreWithBatching? keyValueStore, IReadOnlyTrieStore store, ILogManager? logManager) : TrieStore(keyValueStore, logManager)
{
    public override bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak) =>
        base.IsPersisted(address, in path, in keccak) || store.IsPersisted(address, in path, in keccak);

    public override TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256? hash)
    {
        TrieNode node = base.FindCachedOrUnknown(address, in path, hash);
        return node.NodeType == NodeType.Unknown ? store.FindCachedOrUnknown(address, in path, hash) : node;
    }

    public override byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        base.TryLoadRlp(address, in path, hash, flags) ?? store.LoadRlp(address, in path, hash, flags);

    public override byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        base.TryLoadRlp(address, in path, hash, flags) ?? store.TryLoadRlp(address, in path, hash, flags);

    //public override byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
    //    base.GetByHash(key, flags) ?? store.GetByHash(key, flags);
}
