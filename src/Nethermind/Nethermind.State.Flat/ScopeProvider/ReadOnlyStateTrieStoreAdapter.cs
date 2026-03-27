// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal class ReadOnlyStateTrieStoreAdapter(ReadOnlySnapshotBundle bundle) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        new TrieNode(NodeType.Unknown, hash);

    public override CappedArray<byte> TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (bundle.TryFindStateNodes(path, hash, out RefCountingTrieNode? node))
        {
            try { return new CappedArray<byte>(node.RlpToArray()); }
            finally { node.Dispose(); }
        }
        byte[] buffer = new byte[RefCountingTrieNode.MaxEthereumBranchRlpLength];
        int len = bundle.TryLoadStateRlpFromPersistence(path, hash, buffer, flags);
        return len > 0 ? new CappedArray<byte>(buffer, len) : default;
    }

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new InvalidOperationException("Commit not supported");

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        address is null
            ? this
            : new ReadOnlyStorageTrieStoreAdapter(bundle, address); // Used in trie visitor and weird very edge case that cuts the whole thing to pieces

    public IScopedTrieStore GetStorageTrieStore(Hash256 address) => new ReadOnlyStorageTrieStoreAdapter(bundle, address);
}

internal class ReadOnlyStorageTrieStoreAdapter(
    ReadOnlySnapshotBundle bundle,
    Hash256AsKey addressHash
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        new TrieNode(NodeType.Unknown, hash);

    public override CappedArray<byte> TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (bundle.TryFindStorageNodes(addressHash, path, hash, out RefCountingTrieNode? node))
        {
            try { return new CappedArray<byte>(node.RlpToArray()); }
            finally { node.Dispose(); }
        }
        byte[] buffer = new byte[RefCountingTrieNode.MaxEthereumBranchRlpLength];
        int len = bundle.TryLoadStorageRlpFromPersistence(addressHash, in path, hash, buffer, flags);
        return len > 0 ? new CappedArray<byte>(buffer, len) : default;
    }

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new InvalidOperationException("Commit not supported");
}
