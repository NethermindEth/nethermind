// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class PreCachedTrieStore(ITrieStore inner,
    NonBlocking.ConcurrentDictionary<(Hash256? address, TreePath path, Hash256 hash), byte[]?> preBlockCache)
    : ITrieStore
{
    public void Dispose()
    {
        inner.Dispose();
    }

    public void CommitNode(long blockNumber, Hash256? address, in NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
    {
        inner.CommitNode(blockNumber, address, in nodeCommitInfo, writeFlags);
    }

    public void FinishBlockCommit(TrieType trieType, long blockNumber, Hash256? address, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        inner.FinishBlockCommit(trieType, blockNumber, address, root, writeFlags);
    }

    public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp =preBlockCache.GetOrAdd((address, path, new Hash256(keccak)),
            key => inner.TryLoadRlp(key.address, in key.path, key.hash));

        return rlp is not null;
    }

    public IReadOnlyTrieStore AsReadOnly(INodeStorage? keyValueStore = null) => inner.AsReadOnly(keyValueStore);

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => inner.ReorgBoundaryReached += value;
        remove => inner.ReorgBoundaryReached -= value;
    }

    public IReadOnlyKeyValueStore TrieNodeRlpStore => inner.TrieNodeRlpStore;

    public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] rlp)
    {
        preBlockCache[(address, path, new Hash256(keccak))] = rlp;
        inner.Set(address, in path, in keccak, rlp);
    }

    public bool HasRoot(Hash256 stateRoot) => inner.HasRoot(stateRoot);

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash) => inner.FindCachedOrUnknown(address, in path, hash);

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        preBlockCache.GetOrAdd((address, path, hash),
            key => inner.LoadRlp(key.address, key.path, key.hash, flags));

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        preBlockCache.GetOrAdd((address, path, hash),
            key => inner.TryLoadRlp(key.address, key.path, key.hash, flags));

    public INodeStorage.KeyScheme Scheme => inner.Scheme;
}
