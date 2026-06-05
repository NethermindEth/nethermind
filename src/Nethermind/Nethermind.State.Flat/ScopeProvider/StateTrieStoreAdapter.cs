// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal sealed class StateTrieStoreAdapter(
    SnapshotBundle bundle,
    ConcurrencyController concurrencyQuota,
    NodeStorageCache? nodeStorageCache = null
) : AbstractMinimalTrieStore
{
    private static readonly SeqlockCache<NodeKey, byte[]>.ValueFactory<StateTrieStoreAdapter> LoadStateRlp =
        static (in NodeKey key, StateTrieStoreAdapter adapter) => adapter._bundle.TryLoadStateRlp(key.Path, key.Hash, ReadFlags.None);

    private readonly SnapshotBundle _bundle = bundle;

    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = _bundle.FindStateNodeOrUnknown(path, hash);
        return node.Keccak != hash ? throw new NodeHashMismatchException($"Node hash mismatch. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}") : node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        flags == ReadFlags.None && nodeStorageCache is not null
            ? nodeStorageCache.GetOrAdd(new NodeKey(null, path, hash), this, LoadStateRlp)
            : _bundle.TryLoadStateRlp(path, hash, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new Committer(_bundle, concurrencyQuota);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address is null) return this;
        return new StorageTrieStoreAdapter(_bundle, concurrencyQuota, address, nodeStorageCache);
    }

    private class Committer(SnapshotBundle bundle, ConcurrencyController concurrencyQuota) : AbstractMinimalCommitter(concurrencyQuota)
    {
        public override TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            bundle.SetStateNode(path, node);
            return node;
        }
    }
}

internal sealed class StateTrieStoreWarmerAdapter(
    SnapshotBundle bundle,
    NodeStorageCache? nodeStorageCache = null
) : AbstractMinimalTrieStore
{
    private static readonly SeqlockCache<NodeKey, byte[]>.ValueFactory<StateTrieStoreWarmerAdapter> LoadStateRlp =
        static (in NodeKey key, StateTrieStoreWarmerAdapter adapter) => adapter._bundle.TryLoadStateRlp(key.Path, key.Hash, ReadFlags.None);

    private readonly SnapshotBundle _bundle = bundle;

    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = _bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);
        return node.Keccak != hash ? throw new NodeHashMismatchException($"Node hash mismatch. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}") : node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        flags == ReadFlags.None && nodeStorageCache is not null
            ? nodeStorageCache.GetOrAdd(new NodeKey(null, path, hash), this, LoadStateRlp)
            : _bundle.TryLoadStateRlp(path, hash, flags);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address is null) return this;
        return new StorageTrieStoreWarmerAdapter(_bundle, address, nodeStorageCache);
    }
}

internal sealed class StorageTrieStoreAdapter(
    SnapshotBundle bundle,
    ConcurrencyController concurrencyQuota,
    Hash256AsKey addressHash,
    NodeStorageCache? nodeStorageCache = null
) : AbstractMinimalTrieStore
{
    private static readonly SeqlockCache<NodeKey, byte[]>.ValueFactory<StorageTrieStoreAdapter> LoadStorageRlp =
        static (in NodeKey key, StorageTrieStoreAdapter adapter) => adapter._bundle.TryLoadStorageRlp(adapter._addressHash, key.Path, key.Hash, ReadFlags.None);

    private readonly SnapshotBundle _bundle = bundle;
    private readonly Hash256AsKey _addressHash = addressHash;

    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = _bundle.FindStorageNodeOrUnknown(_addressHash, path, hash);
        return node.Keccak != hash ? throw new NodeHashMismatchException($"Node hash mismatch. Address {_addressHash.Value}. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}") : node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        flags == ReadFlags.None && nodeStorageCache is not null
            ? nodeStorageCache.GetOrAdd(new NodeKey(_addressHash.Value, path, hash), this, LoadStorageRlp)
            : _bundle.TryLoadStorageRlp(_addressHash, in path, hash, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new Committer(_bundle, _addressHash, concurrencyQuota);

    private class Committer(SnapshotBundle bundle, Hash256AsKey addressHash, ConcurrencyController concurrencyQuota) : AbstractMinimalCommitter(concurrencyQuota)
    {
        public override TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            bundle.SetStorageNode(addressHash, path, node);
            return node;
        }
    }
}

internal sealed class StorageTrieStoreWarmerAdapter(
    SnapshotBundle bundle,
    Hash256AsKey addressHash,
    NodeStorageCache? nodeStorageCache = null
) : AbstractMinimalTrieStore
{
    private static readonly SeqlockCache<NodeKey, byte[]>.ValueFactory<StorageTrieStoreWarmerAdapter> LoadStorageRlp =
        static (in NodeKey key, StorageTrieStoreWarmerAdapter adapter) => adapter._bundle.TryLoadStorageRlp(adapter._addressHash, key.Path, key.Hash, ReadFlags.None);

    private readonly SnapshotBundle _bundle = bundle;
    private readonly Hash256AsKey _addressHash = addressHash;

    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = _bundle.FindStorageNodeOrUnknownTrieWarmer(_addressHash, path, hash);
        return node.Keccak != hash ? throw new NodeHashMismatchException($"Node hash mismatch. Address {_addressHash.Value}. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}") : node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        flags == ReadFlags.None && nodeStorageCache is not null
            ? nodeStorageCache.GetOrAdd(new NodeKey(_addressHash.Value, path, hash), this, LoadStorageRlp)
            : _bundle.TryLoadStorageRlp(_addressHash, in path, hash, flags);
}
