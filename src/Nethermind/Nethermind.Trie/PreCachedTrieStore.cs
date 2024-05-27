// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class PreCachedTrieStore(ITrieStore inner,
    NonBlocking.ConcurrentDictionary<NodeKey, byte[]?> preBlockCache)
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
        preBlockCache.Clear();
    }

    public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = preBlockCache.GetOrAdd(new(address, in path, in keccak),
            key => inner.TryLoadRlp(key.Address, in key.Path, key.Hash));

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
        preBlockCache[new(address, in path, in keccak)] = rlp;
        inner.Set(address, in path, in keccak, rlp);
    }

    public bool HasRoot(Hash256 stateRoot) => inner.HasRoot(stateRoot);

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash) => inner.FindCachedOrUnknown(address, in path, hash);

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        preBlockCache.GetOrAdd(new(address, in path, hash),
            key => inner.LoadRlp(key.Address, in key.Path, key.Hash, flags));

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        preBlockCache.GetOrAdd(new(address, in path, hash),
            key => inner.TryLoadRlp(key.Address, in key.Path, key.Hash, flags));

    public INodeStorage.KeyScheme Scheme => inner.Scheme;
}

public class NodeKey : IEquatable<NodeKey>
{
    public readonly Hash256? Address;
    public readonly TreePath Path;
    public readonly Hash256 Hash;

    public NodeKey(Hash256? address, in TreePath path, in ValueHash256 hash)
    {
        Address = address;
        Path = path;
        Hash = hash.ToCommitment();
    }

    public NodeKey(Hash256? address, in TreePath path, Hash256 hash)
    {
        Address = address;
        Path = path;
        Hash = hash;
    }

    public bool Equals(NodeKey? other) =>
        other is not null && Address == other.Address && Path.Equals(in other.Path) && Hash.Equals(other.Hash);

    public override bool Equals(object? obj) => obj is NodeKey key && Equals(key);

    public override int GetHashCode()
    {
        uint hashCode0 = (uint)Hash.GetHashCode();
        ulong hashCode1 = ((ulong)(uint)Path.GetHashCode() << 32) | (uint)(Address?.GetHashCode() ?? 1);
        return (int)BitOperations.Crc32C(hashCode0, hashCode1);
    }
}
