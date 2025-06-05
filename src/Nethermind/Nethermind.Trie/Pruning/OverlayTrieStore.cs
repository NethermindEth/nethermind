// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// OverlayTrieStore works by reading and writing to the passed in keyValueStore first as if it is an archive node.
/// If a node is missing, then it will try to find from the base store.
/// On reset the base db provider is expected to clear any diff which causes this overlay trie store to no longer
/// see overlayed keys.
/// </summary>
public class OverlayTrieStore(IKeyValueStoreWithBatching keyValueStore, IReadOnlyTrieStore baseStore) : ITrieStore
{
    private readonly INodeStorage _nodeStorage = new NodeStorage(keyValueStore);

    public void Dispose()
    {
        baseStore.Dispose();
    }

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        // We always return Unknown even if baseStore return unknown, like archive node.
        return baseStore.FindCachedOrUnknown(address, in path, hash);
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = TryLoadRlp(address, in path, hash, flags);
        if (rlp is null) throw new MissingTrieNodeException("Missing RLP node", address, path, hash);
        return rlp;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => _nodeStorage.Get(address, in path, hash, flags) ?? baseStore.TryLoadRlp(address, in path, hash, flags);

    public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak) => _nodeStorage.Get(address, in path, in keccak) is not null || baseStore.IsPersisted(address, in path, in keccak);

    public bool HasRoot(Hash256 stateRoot) => _nodeStorage.Get(null, TreePath.Empty, stateRoot) is not null || baseStore.HasRoot(stateRoot);

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public INodeStorage.KeyScheme Scheme => baseStore.Scheme;

    public IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;

    // Write directly to _nodeStorage, which goes to db provider.
    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => new RawScopedTrieStore.Committer(_nodeStorage, address, writeFlags);
}
