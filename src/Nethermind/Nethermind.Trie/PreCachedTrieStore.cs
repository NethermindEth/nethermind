// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public sealed class PreCachedTrieStore : ITrieStore
{
    private readonly ITrieStore _inner;
    private readonly NodeStorageCache _preBlockCache;
    private readonly SeqlockCache<NodeKey, byte[]>.ValueFactory _loadRlp;
    private readonly SeqlockCache<NodeKey, byte[]>.ValueFactory _tryLoadRlp;

    public PreCachedTrieStore(ITrieStore inner, NodeStorageCache cache)
    {
        _inner = inner;
        _preBlockCache = cache;

        // Capture the delegate once for default path to avoid the allocation of the lambda per call
        _loadRlp = (in NodeKey key) => _inner.LoadRlp(key.Address, in key.Path, key.Hash, flags: ReadFlags.None);
        _tryLoadRlp = (in NodeKey key) => _inner.TryLoadRlp(key.Address, in key.Path, key.Hash, flags: ReadFlags.None);
    }

    public void Dispose() => _inner.Dispose();

    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => _inner.BeginCommit(address, root, writeFlags);

    public IBlockCommitter BeginBlockCommit(long blockNumber) => _inner.BeginBlockCommit(blockNumber);

    public bool HasRoot(Hash256 stateRoot) => _inner.HasRoot(stateRoot);

    public bool HasRoot(Hash256 stateRoot, long blockNumber) => _inner.HasRoot(stateRoot, blockNumber);

    public IDisposable BeginScope(BlockHeader? baseBlock) => _inner.BeginScope(baseBlock);

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash) => _inner.FindCachedOrUnknown(address, in path, hash);

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        _preBlockCache.GetOrAdd(new(address, in path, hash),
            flags == ReadFlags.None ? _loadRlp :
            (in NodeKey key) => _inner.LoadRlp(key.Address, in key.Path, key.Hash, flags));

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        _preBlockCache.GetOrAdd(new(address, in path, hash),
            flags == ReadFlags.None ? _tryLoadRlp :
            (in key) => _inner.TryLoadRlp(key.Address, in key.Path, key.Hash, flags));

    public INodeStorage.KeyScheme Scheme => _inner.Scheme;
}

public readonly struct NodeKey : IEquatable<NodeKey>, IHash64bit<NodeKey>
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

    public bool Equals(NodeKey other) => Equals(in other);

    public override bool Equals(object? obj) => obj is NodeKey key && Equals(key);

    public bool Equals(in NodeKey other) =>
        Address == other.Address && Path.Equals(in other.Path) && Hash.Equals(other.Hash);

    public override int GetHashCode()
    {
        uint hashCode0 = (uint)Hash.GetHashCode();
        ulong hashCode1 = ((ulong)(uint)Path.GetHashCode() << 32) | (uint)(Address?.GetHashCode() ?? 1);
        return (int)BitOperations.Crc32C(hashCode0, hashCode1);
    }

    public long GetHashCode64()
    {
        long hashCode0 = Address is null ? 1L : SpanExtensions.FastHash64For32Bytes(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in Address.ValueHash256)));
        long hashCode1 = SpanExtensions.FastHash64For32Bytes(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in Hash.ValueHash256)));
        long hashCode2 = SpanExtensions.FastHash64For32Bytes(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in Path.Path)));

        // Rotations spaced by 64/3 ensure way 0 (bits 0-13) and way 1 (bits 42-55)
        // sample non-overlapping 14-bit windows from each input
        return hashCode1 + (long)BitOperations.RotateLeft((ulong)hashCode0, 21) + (long)BitOperations.RotateLeft((ulong)hashCode2, 42);
    }
}
