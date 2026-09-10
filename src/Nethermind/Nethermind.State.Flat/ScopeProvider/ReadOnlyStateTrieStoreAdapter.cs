// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal class ReadOnlyStateTrieStoreAdapter(ReadOnlySnapshotBundle bundle, ClockCache<ValueHash256, byte[]>? nodeCache = null) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        bundle.TryFindStateNodes(path, hash, out TrieNode? node) ? node : new TrieNode(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (nodeCache is null) return bundle.TryLoadStateRlp(path, hash, flags);
        ValueHash256 key = hash.ValueHash256;
        if (nodeCache.TryGet(key, out byte[]? cached)) return cached;
        byte[]? rlp = bundle.TryLoadStateRlp(path, hash, flags);
        if (rlp is not null) nodeCache.Set(key, rlp);
        return rlp;
    }

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new InvalidOperationException("Commit not supported");

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        address is null
            ? this
            : new ReadOnlyStorageTrieStoreAdapter(bundle, address, nodeCache); // Used in trie visitor and weird very edge case that cuts the whole thing to pieces

    public IScopedTrieStore GetStorageTrieStore(Hash256 address) => new ReadOnlyStorageTrieStoreAdapter(bundle, address, nodeCache);
}

internal class ReadOnlyStorageTrieStoreAdapter(
    ReadOnlySnapshotBundle bundle,
    Hash256AsKey addressHash,
    ClockCache<ValueHash256, byte[]>? nodeCache = null
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        bundle.TryFindStorageNodes(addressHash, path, hash, out TrieNode? node) ? node : new TrieNode(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (nodeCache is null) return bundle.TryLoadStorageRlp(addressHash, in path, hash, flags);
        ValueHash256 key = hash.ValueHash256;
        if (nodeCache.TryGet(key, out byte[]? cached)) return cached;
        byte[]? rlp = bundle.TryLoadStorageRlp(addressHash, in path, hash, flags);
        if (rlp is not null) nodeCache.Set(key, rlp);
        return rlp;
    }

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new InvalidOperationException("Commit not supported");
}
