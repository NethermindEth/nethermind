// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class PreCachedTrieStore : ITrieStore
{
    private readonly ITrieStore _inner;
    private readonly ConcurrentDictionary<NodeKey, byte[]?> _preBlockCache;
    private readonly Func<NodeKey, byte[]> _loadRlp;
    private readonly Func<NodeKey, byte[]> _tryLoadRlp;

    public PreCachedTrieStore(ITrieStore inner,
        ConcurrentDictionary<NodeKey, byte[]?> preBlockCache)
    {
        _inner = inner;
        _preBlockCache = preBlockCache;

        // Capture the delegate once for default path to avoid the allocation of the lambda per call
        _loadRlp = (NodeKey key) => _inner.LoadRlp(key.Address, in key.Path, key.Hash, flags: ReadFlags.None);
        _tryLoadRlp = (NodeKey key) => _inner.TryLoadRlp(key.Address, in key.Path, key.Hash, flags: ReadFlags.None);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }

    public void CommitNode(long blockNumber, Hash256? address, in NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
    {
        _inner.CommitNode(blockNumber, address, in nodeCommitInfo, writeFlags);
    }

    public void FinishBlockCommit(TrieType trieType, long blockNumber, Hash256? address, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        _inner.FinishBlockCommit(trieType, blockNumber, address, root, writeFlags);
        _preBlockCache.Clear();
    }

    public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = _preBlockCache.GetOrAdd(new(address, in path, in keccak),
            key => _inner.TryLoadRlp(key.Address, in key.Path, key.Hash));

        return rlp is not null;
    }

    public IReadOnlyTrieStore AsReadOnly(INodeStorage? keyValueStore = null) => _inner.AsReadOnly(keyValueStore);

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _inner.ReorgBoundaryReached += value;
        remove => _inner.ReorgBoundaryReached -= value;
    }

    public IReadOnlyKeyValueStore TrieNodeRlpStore => _inner.TrieNodeRlpStore;

    public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] rlp)
    {
        _preBlockCache[new(address, in path, in keccak)] = rlp;
        _inner.Set(address, in path, in keccak, rlp);
    }

    public bool HasRoot(Hash256 stateRoot) => _inner.HasRoot(stateRoot);

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash) => _inner.FindCachedOrUnknown(address, in path, hash);

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        _preBlockCache.GetOrAdd(new(address, in path, hash),
            flags == ReadFlags.None ? _loadRlp :
            key => _inner.LoadRlp(key.Address, in key.Path, key.Hash, flags));

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        _preBlockCache.GetOrAdd(new(address, in path, hash),
            flags == ReadFlags.None ? _tryLoadRlp :
            key => _inner.TryLoadRlp(key.Address, in key.Path, key.Hash, flags));

    public INodeStorage.KeyScheme Scheme => _inner.Scheme;
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
