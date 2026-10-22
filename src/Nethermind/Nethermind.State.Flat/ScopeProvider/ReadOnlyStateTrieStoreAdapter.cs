// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal class ReadOnlyStateTrieStoreAdapter(ReadOnlySnapshotBundle bundle, ITrieNodeCache? trieNodeCache = null) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        if (trieNodeCache is not null && trieNodeCache.TryGet(null, path, hash, out TrieNode? node)) return node;
        return bundle.TryFindStateNodes(path, hash, out node) ? node : new TrieNode(NodeType.Unknown, hash);
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => bundle.TryLoadStateRlp(path, hash, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new InvalidOperationException("Commit not supported");

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        address is null
            ? this
            : new ReadOnlyStorageTrieStoreAdapter(bundle, address, trieNodeCache); // Used in trie visitor and weird very edge case that cuts the whole thing to pieces

    public IScopedTrieStore GetStorageTrieStore(Hash256 address) => new ReadOnlyStorageTrieStoreAdapter(bundle, address, trieNodeCache);
}

internal class ReadOnlyStorageTrieStoreAdapter(
    ReadOnlySnapshotBundle bundle,
    Hash256AsKey addressHash,
    ITrieNodeCache? trieNodeCache = null
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        if (trieNodeCache is not null && trieNodeCache.TryGet(addressHash, path, hash, out TrieNode? node)) return node;
        return bundle.TryFindStorageNodes(addressHash, path, hash, out node) ? node : new TrieNode(NodeType.Unknown, hash);
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => bundle.TryLoadStorageRlp(addressHash, in path, hash, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new InvalidOperationException("Commit not supported");
}
