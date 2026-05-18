// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Interface with PatriciaTrie. Its `scoped` as it have underlying storage address for storage trie. It basically
/// an adapter to the standard ITrieStore.
/// </summary>
public interface IScopedTrieStore : ITrieNodeResolver
{
    // Begins a commit to update the trie store. The `ICommitter` provide `CommitNode` to add node into.
    ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None);
}

public interface ICommitter : IDisposable
{
    /// <summary>
    /// Commit a trienode to the triestore at path. Returns potentially another trienode that should be merged
    /// with the patricia trie.
    /// </summary>
    /// <remarks>
    /// Implementations must be safe under concurrent calls from multiple threads. PatriciaTree's parallel
    /// commit paths (both <see cref="PatriciaTree.Commit"/>'s quota-based dispatch and
    /// <see cref="PatriciaTree.BulkSetAndCommit"/>'s 16-way Parallel.For) can invoke CommitNode concurrently
    /// whenever <see cref="TryRequestConcurrentQuota"/> has granted quota. Production
    /// <c>TrieStore.BlockCommitter</c> synchronizes internally; custom implementations that wrap a plain
    /// <c>IWriteBatch</c> or dictionary must add their own thread safety.
    /// </remarks>
    /// <param name="path"></param>
    /// <param name="node"></param>
    /// <returns></returns>
    TrieNode CommitNode(ref TreePath path, TrieNode node);

    bool TryRequestConcurrentQuota() => false;
    void ReturnConcurrencyQuota() { }
}
