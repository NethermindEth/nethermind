// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class TrieStoreWithReadFlags : TrieNodeResolverWithReadFlags, ITrieStore
{
    private ITrieStore _baseImplementation;

    public TrieStoreWithReadFlags(ITrieStore baseImplementation, ReadFlags readFlags) : base(baseImplementation, readFlags)
    {
        _baseImplementation = baseImplementation;
    }

    public void Dispose()
    {
        _baseImplementation.Dispose();
    }

    public void OpenContext(long blockNumber, Hash256 keccak)
    {
        _baseImplementation.OpenContext(blockNumber, keccak);
    }

    public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
    {
        _baseImplementation.CommitNode(blockNumber, nodeCommitInfo, writeFlags);
    }

    public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        _baseImplementation.FinishBlockCommit(trieType, blockNumber, root, writeFlags);
    }

    public bool IsPersisted(in ValueHash256 keccak)
    {
        return _baseImplementation.IsPersisted(in keccak);
    }

    public IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore = null) =>
        _baseImplementation.AsReadOnly(keyValueStore);

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _baseImplementation.ReorgBoundaryReached += value;
        remove => _baseImplementation.ReorgBoundaryReached -= value;
    }

    public IReadOnlyKeyValueStore TrieNodeRlpStore => _baseImplementation.TrieNodeRlpStore;

    public void Set(in ValueHash256 hash, byte[] rlp)
    {
        _baseImplementation.Set(in hash, rlp);
    }

    public bool HasRoot(Hash256 stateRoot)
    {
        return _baseImplementation.HasRoot(stateRoot);
    }

    public void PersistNode(TrieNode trieNode, IWriteBatch? batch = null, bool withDelete = false,
        WriteFlags writeFlags = WriteFlags.None)
    {
        _baseImplementation.PersistNode(trieNode, batch, withDelete, writeFlags);
    }

    public void PersistNodeData(Span<byte> fullPath, int pathToNodeLength, byte[]? rlpData, IWriteBatch? keyValueStore = null,
        WriteFlags writeFlags = WriteFlags.None)
    {
        _baseImplementation.PersistNodeData(fullPath, pathToNodeLength, rlpData, keyValueStore, writeFlags);
    }

    public void ClearCache()
    {
        _baseImplementation.ClearCache();
    }

    public void MarkPrefixDeleted(long blockNumber, ReadOnlySpan<byte> keyPrefix)
    {
        _baseImplementation.MarkPrefixDeleted(blockNumber, keyPrefix);
    }

    public void DeleteByRange(Span<byte> startKey, Span<byte> endKey, IWriteBatch writeBatch = null)
    {
        _baseImplementation.DeleteByRange(startKey, endKey, writeBatch);
    }

    public bool CanAccessByPath()
    {
        return _baseImplementation.CanAccessByPath();
    }

    public bool ShouldResetObjectsOnRootChange()
    {
        return _baseImplementation.ShouldResetObjectsOnRootChange();
    }
}
