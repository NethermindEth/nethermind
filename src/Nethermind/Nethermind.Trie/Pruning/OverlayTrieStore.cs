// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class OverlayTrieStore(IKeyValueStoreWithBatching keyValueStore, IReadOnlyTrieStore store) : ITrieStore
{
    private readonly INodeStorage _nodeStorage = new NodeStorage(keyValueStore);

    public void Dispose()
    {
        store.Dispose();
    }

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        return store.FindCachedOrUnknown(address, in path, hash);
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = TryLoadRlp(address, in path, hash, flags);
        if (rlp is null) throw new MissingTrieNodeException("Missing RLP node", address, path, hash);
        return rlp;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => store.TryLoadRlp(address, in path, hash, flags) ?? _nodeStorage.Get(address, in path, hash, flags);

    public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak) => store.IsPersisted(address, in path, in keccak) || _nodeStorage.Get(address, in path, in keccak) is not null;

    public bool HasRoot(Hash256 stateRoot) => store.HasRoot(stateRoot) || _nodeStorage.Get(null, TreePath.Empty, stateRoot) is not null;

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public INodeStorage.KeyScheme Scheme => store.Scheme;

    public IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;

    // Write directly to _nodeStorage, which goes to db provider.
    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => new RawScopedTrieStore.Committer(_nodeStorage, address, writeFlags);
}
