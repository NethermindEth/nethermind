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

    public IReadOnlyTrieStore AsReadOnly() =>
        _baseImplementation.AsReadOnly();

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
}
