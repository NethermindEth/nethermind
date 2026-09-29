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
    ConcurrencyController concurrencyQuota
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = bundle.FindStateNodeOrUnknown(path, hash);
        return node.Keccak != hash ? throw new NodeHashMismatchException($"Node hash mismatch. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}") : node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        bundle.TryLoadStateRlp(path, hash, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new Committer(bundle, concurrencyQuota);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address is null) return this;
        return new StorageTrieStoreAdapter(bundle, concurrencyQuota, address);
    }

    private class Committer(SnapshotBundle bundle, ConcurrencyController concurrencyQuota) : AbstractMinimalCommitter(concurrencyQuota)
    {
        protected override void WriteNode(in TreePath path, TrieNode node) => bundle.SetStateNode(path, node);

        protected override void PublishNodes(IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers) =>
            bundle.PublishStateNodes(buffers);
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
        GetMatchingRlp(bundle.TryLoadStateRlp(path, hash, flags), hash);

    /// <summary>Returns <paramref name="rlp"/> only when it hashes to <paramref name="hash"/>, else <c>null</c>.</summary>
    /// <remarks>
    /// The persistence read behind both warmer adapters is keyed by path alone, so it can answer with another
    /// version of the node at that path. <see cref="TrieNode"/> stores whatever bytes it is handed under the
    /// requested hash, and reader-side guards compare against that claimed hash, so these two <c>TryLoadRlp</c>
    /// overrides are what stops foreign bytes entering a node in the first place; a warmer node is checked again
    /// before promotion into the shared cache, because it can be rewritten after this point. A mismatch is
    /// staleness, so it reads as a miss.
    /// </remarks>
    internal static byte[]? GetMatchingRlp(byte[]? rlp, Hash256 hash) =>
        rlp is null || ValueKeccak.Compute(rlp) == hash ? rlp : null;

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address is null) return this;
        return new StorageTrieStoreWarmerAdapter(bundle, address);
    }
}

internal sealed class StorageTrieStoreAdapter(
    SnapshotBundle bundle,
    ConcurrencyController concurrencyQuota,
    Hash256AsKey addressHash
) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        TrieNode node = bundle.FindStorageNodeOrUnknown(addressHash, path, hash);
        return node.Keccak != hash ? throw new NodeHashMismatchException($"Node hash mismatch. Address {addressHash.Value}. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}") : node;
    }

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        bundle.TryLoadStorageRlp(addressHash, in path, hash, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new Committer(bundle, addressHash, concurrencyQuota);

    private class Committer(
        SnapshotBundle bundle,
        Hash256AsKey addressHash,
        ConcurrencyController concurrencyQuota) : AbstractMinimalCommitter(concurrencyQuota)
    {
        private readonly AddressStorageNodeDictionary.AddressNodes _nodes = bundle.GetStorageNodeDestination(addressHash);

        protected override void WriteNode(in TreePath path, TrieNode node) =>
            bundle.SetStorageNode(_nodes, addressHash, path, node);

        protected override void PublishNodes(IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers) =>
            bundle.PublishStorageNodes(_nodes, addressHash, buffers);
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
        StateTrieStoreWarmerAdapter.GetMatchingRlp(bundle.TryLoadStorageRlp(addressHash, in path, hash, flags), hash);
}
