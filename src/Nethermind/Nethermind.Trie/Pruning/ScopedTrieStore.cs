// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class ScopedTrieStore : IScopedTrieStore
{
    private ITrieStore _trieStoreImplementation;
    private readonly Hash256? _address;

    public ScopedTrieStore(ITrieStore fullTrieStore, Hash256? address)
    {
        _trieStoreImplementation = fullTrieStore;
        _address = address;
    }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        return _trieStoreImplementation.FindCachedOrUnknown(_address, path, hash);
    }

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        return _trieStoreImplementation.LoadRlp(_address, path, hash, flags);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        return _trieStoreImplementation.TryLoadRlp(_address, path, hash, flags);
    }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address == _address) return this;
        return new ScopedTrieStore(_trieStoreImplementation, address);
    }

    public INodeStorage.KeyScheme Scheme => _trieStoreImplementation.Scheme;

    public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
    {
        _trieStoreImplementation.CommitNode(blockNumber, _address, nodeCommitInfo, writeFlags);
    }

    public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        _trieStoreImplementation.FinishBlockCommit(trieType, blockNumber, _address, root, writeFlags);
    }

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        return _trieStoreImplementation.IsPersisted(_address, path, in keccak);
    }

    public void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp)
    {
        _trieStoreImplementation.Set(_address, path, keccak, rlp);
    }
}
