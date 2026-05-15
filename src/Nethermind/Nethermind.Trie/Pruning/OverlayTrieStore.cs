// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Reads/writes to <paramref name="keyValueStore"/> first (archive-style); falls back to <paramref name="baseStore"/>
/// on miss. Reset is driven by the base db provider clearing the overlay diff.
/// </summary>
public class OverlayTrieStore(IKeyValueStoreWithBatching keyValueStore, IReadOnlyTrieStore baseStore)
    : WrappingTrieStore(baseStore), IScopedReadOnlyTraversalProvider
{
    private readonly INodeStorage _nodeStorage = new NodeStorage(keyValueStore);
    private readonly IReadOnlyTrieStore _baseStore = baseStore;

    public override byte[]? LoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        TryLoadRlp(address, in path, in hash, flags)
        ?? throw new MissingTrieNodeException("Missing RLP node", address, path, new Hash256(in hash));

    public override byte[]? TryLoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        _nodeStorage.Get(address, in path, hash, flags) ?? _baseStore.TryLoadRlp(address, in path, in hash, flags);

    public override TrieNode GetOrLoadNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        TryGetOverlayNode(address, in path, in hash, out TrieNode? node, flags)
            ? node
            : _baseStore.GetOrLoadNode(address, in path, in hash, flags);

    public override bool TryGetOrLoadNode(Hash256? address, in TreePath path, in ValueHash256 hash, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None)
    {
        if (TryGetOverlayNode(address, in path, in hash, out node, flags))
        {
            return true;
        }

        return _baseStore.TryGetOrLoadNode(address, in path, in hash, out node, flags);
    }

    public override bool HasRoot(Hash256 stateRoot) =>
        _nodeStorage.Get(null, TreePath.Empty, stateRoot) is not null || _baseStore.HasRoot(stateRoot);

    public override IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;

    public override ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) =>
        new RawScopedTrieStore.Committer(_nodeStorage, address, writeFlags);

    public ITrieNodeResolver? GetReadOnlyTraversalResolver(Hash256? address) =>
        new SharedOverlayTraversalResolver(
            this,
            address,
            _baseStore.GetTrieStore(address).AsReadOnlyTraversal());

    private bool TryGetOverlayNode(Hash256? address, in TreePath path, in ValueHash256 hash, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TrieNode? node, ReadFlags flags)
    {
        byte[]? rlp = _nodeStorage.Get(address, in path, hash, flags);
        if (rlp is null)
        {
            node = null;
            return false;
        }

        node = TrieNode.DecodeNode(in path, in hash, rlp);
        return true;
    }

    private sealed class SharedOverlayTraversalResolver(
        OverlayTrieStore fullTrieStore,
        Hash256? address,
        ITrieNodeResolver baseReadResolver) : ReadOnlyTraversalResolver(fullTrieStore, address)
    {
        public override TrieNode GetOrLoadNode(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
            fullTrieStore.TryGetOverlayNode(Address, in path, in hash, out TrieNode? node, flags)
                ? node
                : baseReadResolver.GetOrLoadNode(in path, in hash, flags);

        public override bool TryGetOrLoadNode(in TreePath path, in ValueHash256 hash, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None)
        {
            if (fullTrieStore.TryGetOverlayNode(Address, in path, in hash, out node, flags))
            {
                return true;
            }

            return baseReadResolver.TryGetOrLoadNode(in path, in hash, out node, flags);
        }

        protected override ITrieNodeResolver WithAddress(Hash256? address1) =>
            new SharedOverlayTraversalResolver(
                fullTrieStore,
                address1,
                fullTrieStore._baseStore.GetTrieStore(address1).AsReadOnlyTraversal());
    }
}
