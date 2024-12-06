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

    internal override TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256? hash, bool isReadOnly)
    {
        ArgumentNullException.ThrowIfNull(hash);

        TrieNode node = base.FindCachedOrUnknown(address, in path, hash, isReadOnly);
        return node.NodeType == NodeType.Unknown
            ? store.FindCachedOrUnknown(address, in path, hash) // no need to pass isReadOnly - IReadOnlyTrieStore overrides it as true
            : node;
    }

    public override byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        base.TryLoadRlp(address, in path, hash, flags) ?? store.LoadRlp(address, in path, hash, flags);

    public override byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        base.TryLoadRlp(address, in path, hash, flags) ?? store.TryLoadRlp(address, in path, hash, flags);

    protected override void VerifyNewCommitSet(long blockNumber)
    {
        // Skip checks, as override can be applied using the same block number or without a state root
    }

    public void ResetOverrides() => ClearCache();
}
