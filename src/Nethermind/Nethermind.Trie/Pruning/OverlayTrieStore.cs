// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning;

public class OverlayTrieStore(ITrieStore baseTrieStore, IReadOnlyTrieStore store) : ITrieStore
{
    public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak) =>
        baseTrieStore.IsPersisted(address, in path, in keccak) || store.IsPersisted(address, in path, in keccak);

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        TrieNode node = baseTrieStore.FindCachedOrUnknown(address, in path, hash);
        return node.NodeType == NodeType.Unknown
            ? store.FindCachedOrUnknown(address, in path, hash) // no need to pass isReadOnly - IReadOnlyTrieStore overrides it as true
            : node;
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        baseTrieStore.TryLoadRlp(address, in path, hash, flags) ?? store.LoadRlp(address, in path, hash, flags);

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        baseTrieStore.TryLoadRlp(address, in path, hash, flags) ?? store.TryLoadRlp(address, in path, hash, flags);

    public void Dispose()
    {
        baseTrieStore.Dispose();
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] rlp)
    {
        baseTrieStore.Set(address, in path, in keccak, rlp);
    }

    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags)
    {
        return baseTrieStore.BeginCommit(address, root, writeFlags);
    }

    public IReadOnlyTrieStore AsReadOnly(INodeStorage? keyValueStore = null)
    {
        return baseTrieStore.AsReadOnly(keyValueStore);
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => baseTrieStore.ReorgBoundaryReached += value;
        remove => baseTrieStore.ReorgBoundaryReached -= value;
    }

    public IReadOnlyKeyValueStore TrieNodeRlpStore => baseTrieStore.TrieNodeRlpStore;

    public bool HasRoot(Hash256 stateRoot)
    {
        return baseTrieStore.HasRoot(stateRoot);
    }

    public IScopedTrieStore GetTrieStore(Hash256? address)
    {
        return baseTrieStore.GetTrieStore(address);
    }

    public INodeStorage.KeyScheme Scheme => baseTrieStore.Scheme;

    public IBlockCommitter BeginBlockCommit(long blockNumber)
    {
        return baseTrieStore.BeginBlockCommit(blockNumber);
    }
}
