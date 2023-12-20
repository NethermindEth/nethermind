// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Interface with PatriciaTrie. Its `scoped` as it have underlying storage address for storage trie. It basically
/// an adapter to the standard ITrieStore.
/// </summary>
public interface IScopedTrieStore : ITrieNodeResolver, IDisposable
{
    // TODO: Commit and FinishBlockCommit is unnecessary. Geth just compile the changes and return it in a batch,
    // which get committed in a single call.
    void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None);

    void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None);

    // Only used by snap provider, so ValueHash instead of Hash
    bool IsPersisted(in TreePath path, in ValueHash256 keccak);

    // Used for trie node recovery
    void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp);
}
