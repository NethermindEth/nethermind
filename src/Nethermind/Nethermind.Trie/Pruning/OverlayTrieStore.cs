// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// OverlayTrieStore works by reading and writing to the passed in keyValueStore first as if it is an archive node.
/// If a node is missing, then it will try to find from the base store.
/// On reset the base db provider is expected to clear any diff which causes this overlay trie store to no longer
/// see overlaid keys.
/// </summary>
public class OverlayTrieStore(IKeyValueStoreWithBatching keyValueStore, IReadOnlyTrieStore baseStore)
    : ITrieStore, IScopedReadOnlyTraversalProvider
{
    private readonly INodeStorage _nodeStorage = new NodeStorage(keyValueStore);
    private readonly IReadOnlyTrieStore _baseStore = baseStore;

    public void Dispose() => _baseStore.Dispose();

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, in ValueHash256 hash) =>
        // We always return Unknown even if baseStore return unknown, like archive node.
        _baseStore.FindCachedOrUnknown(address, in path, in hash);

    public byte[]? LoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
        TryLoadRlp(address, in path, in hash, flags)
        ?? throw new MissingTrieNodeException("Missing RLP node", address, path, new Hash256(in hash));

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) => _nodeStorage.Get(address, in path, hash, flags) ?? _baseStore.TryLoadRlp(address, in path, in hash, flags);

    public bool HasRoot(Hash256 stateRoot) => _nodeStorage.Get(null, TreePath.Empty, stateRoot) is not null || _baseStore.HasRoot(stateRoot);

    public IDisposable BeginScope(BlockHeader? baseBlock) => _baseStore.BeginScope(baseBlock);

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public INodeStorage.KeyScheme Scheme => _baseStore.Scheme;

    public IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;

    // Write directly to _nodeStorage, which goes to db provider.
    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => new RawScopedTrieStore.Committer(_nodeStorage, address, writeFlags);

    public ITrieNodeResolver? GetReadOnlyTraversalResolver(Hash256? address) =>
        new SharedOverlayTraversalResolver(
            this,
            address,
            _baseStore.GetTrieStore(address).AsReadOnlyTraversal());

    private sealed class SharedOverlayTraversalResolver(
        OverlayTrieStore fullTrieStore,
        Hash256? address,
        ITrieNodeResolver baseReadResolver) : ReadOnlyTraversalResolverBase(fullTrieStore, address)
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, in ValueHash256 hash) =>
            baseReadResolver.FindCachedOrUnknown(path, in hash);

        protected override ITrieNodeResolver WithAddress(Hash256? address1) =>
            new SharedOverlayTraversalResolver(
                fullTrieStore,
                address1,
                fullTrieStore._baseStore.GetTrieStore(address1).AsReadOnlyTraversal());
    }
}
