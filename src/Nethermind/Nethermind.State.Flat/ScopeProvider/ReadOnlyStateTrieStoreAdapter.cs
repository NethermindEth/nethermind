// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal class ReadOnlyStateTrieStoreAdapter(ReadOnlySnapshotBundle bundle) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        bundle.TryFindStateNodes(path, hash, out TrieNode? node) ? node : new TrieNode(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => bundle.TryLoadStateRlp(path, hash, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new InvalidOperationException("Commit not supported");

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        address is null
            ? this
            : new ReadOnlyStorageTrieStoreAdapter(bundle, address); // Used in trie visitor and weird very edge case that cuts the whole thing to pieces
}

internal class ReadOnlyStorageTrieStoreAdapter(
    ReadOnlySnapshotBundle bundle,
    Hash256AsKey addressHash
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        bundle.TryFindStorageNodes(addressHash, path, hash, out TrieNode? node) ? node : new TrieNode(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => bundle.TryLoadStorageRlp(addressHash, in path, hash, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new InvalidOperationException("Commit not supported");
}
