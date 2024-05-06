// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class TrieStoreWithReadFlags : TrieNodeResolverWithReadFlags, IScopedTrieStore
{
    private IScopedTrieStore _baseImplementation;

    public TrieStoreWithReadFlags(IScopedTrieStore implementation, ReadFlags flags) : base(implementation, flags)
    {
        _baseImplementation = implementation;
    }

    public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
    {
        _baseImplementation.CommitNode(blockNumber, nodeCommitInfo, writeFlags);
    }

    public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        _baseImplementation.FinishBlockCommit(trieType, blockNumber, root, writeFlags);
    }

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        return _baseImplementation.IsPersisted(in path, in keccak);
    }

    public void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp)
    {
        _baseImplementation.Set(in path, in keccak, rlp);
    }
}
