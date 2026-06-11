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
        bundle.TryFindStateNodes(path, hash, out TrieNode? node) && node.Keccak == hash
            ? node
            : new TrieNode(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        // Flat trie nodes are addressed by path only; the requested node hash is not part of the key.
        // A trie restructure can leave a stale node at a path that is no longer the one referenced by
        // the parent, so verify the stored node actually hashes to the requested reference. Serving an
        // unverified node corrupts snap range proofs (the peer reconstructs a different state root and
        // rejects the whole range). Report a hash mismatch as missing, matching the hash-checking
        // production adapter (StateTrieStoreAdapter).
        byte[]? rlp = bundle.TryLoadStateRlp(path, hash, flags);
        return rlp is not null && ValueKeccak.Compute(rlp).ToCommitment() == hash ? rlp : null;
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
        bundle.TryFindStorageNodes(addressHash, path, hash, out TrieNode? node) && node.Keccak == hash
            ? node
            : new TrieNode(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        // See ReadOnlyStateTrieStoreAdapter.TryLoadRlp: verify the path-addressed flat node hashes to
        // the requested reference so a stale/orphaned storage node is reported missing rather than
        // silently served (which would corrupt snap storage-range proofs).
        byte[]? rlp = bundle.TryLoadStorageRlp(addressHash, in path, hash, flags);
        return rlp is not null && ValueKeccak.Compute(rlp).ToCommitment() == hash ? rlp : null;
    }

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => throw new InvalidOperationException("Commit not supported");
}
