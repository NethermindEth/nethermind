// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal sealed class StateTrieStoreAdapter(
    SnapshotBundle bundle,
    ConcurrencyController concurrencyQuota,
    ITrieNodeReadObserver? readObserver = null
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = bundle.FindStateNodeOrUnknown(path, hash);
        if (node.Keccak != hash) throw new NodeHashMismatchException($"Node hash mismatch. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}");
        if (readObserver is { IsActive: true } observer && node.NodeType != NodeType.Unknown && node.FullRlp.IsNotNull)
            observer.OnTrieNodeRead(hash, node.FullRlp.ToArray()!);
        return node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = bundle.TryLoadStateRlp(path, hash, flags);
        if (rlp is not null && readObserver is { IsActive: true } observer) observer.OnTrieNodeRead(hash, rlp);
        return rlp;
    }

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new Committer(bundle, concurrencyQuota);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address is null) return this;
        return new StorageTrieStoreAdapter(bundle, concurrencyQuota, address, readObserver);
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
    SnapshotBundle bundle
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);
        return node.Keccak != hash ? throw new NodeHashMismatchException($"Node hash mismatch. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}") : node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        bundle.TryLoadStateRlp(path, hash, flags);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address is null) return this;
        return new StorageTrieStoreWarmerAdapter(bundle, address);
    }
}

internal sealed class StorageTrieStoreAdapter(
    SnapshotBundle bundle,
    ConcurrencyController concurrencyQuota,
    Hash256AsKey addressHash,
    ITrieNodeReadObserver? readObserver = null
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = bundle.FindStorageNodeOrUnknown(addressHash, path, hash);
        if (node.Keccak != hash) throw new NodeHashMismatchException($"Node hash mismatch. Address {addressHash.Value}. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}");
        if (readObserver is { IsActive: true } observer && node.NodeType != NodeType.Unknown && node.FullRlp.IsNotNull)
            observer.OnTrieNodeRead(hash, node.FullRlp.ToArray()!);
        return node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = bundle.TryLoadStorageRlp(addressHash, in path, hash, flags);
        if (rlp is not null && readObserver is { IsActive: true } observer) observer.OnTrieNodeRead(hash, rlp);
        return rlp;
    }

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new Committer(bundle, addressHash, concurrencyQuota);

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
    Hash256AsKey addressHash
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = bundle.FindStorageNodeOrUnknownTrieWarmer(addressHash, path, hash);
        return node.Keccak != hash ? throw new NodeHashMismatchException($"Node hash mismatch. Address {addressHash.Value}. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}") : node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        bundle.TryLoadStorageRlp(addressHash, in path, hash, flags);
}
