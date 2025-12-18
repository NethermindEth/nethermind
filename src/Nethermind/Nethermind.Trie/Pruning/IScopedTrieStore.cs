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
public interface IScopedTrieStore : ITrieNodeResolver
{
    // Begins a commit to update the trie store. The `ICommitter` provide `CommitNode` to add node into.
    ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None);

    // Only used by snap provider, so ValueHash instead of Hash
    bool IsPersisted(in TreePath path, in ValueHash256 keccak);
}

public interface ICommitter : IDisposable
{
    /// <summary>
    /// Commit a trienode to the triestore at path. Returns potentially another trienode that should be merged
    /// with the patricia trie.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="node"></param>
    /// <returns></returns>
    TrieNode CommitNode(ref TreePath path, TrieNode node);

    bool TryRequestConcurrentQuota() => false;
    void ReturnConcurrencyQuota() { }
}
