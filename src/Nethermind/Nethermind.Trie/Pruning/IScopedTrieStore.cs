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
    /// <param name="path"></param>
    /// <param name="node"></param>
    /// <returns></returns>
    TrieNode CommitNode(ref TreePath path, TrieNode node);

    /// <summary>Prepares this committer for concurrent calls to <see cref="CommitNode"/>.</summary>
    /// <remarks>Call on the owning thread before dispatching any commit work.</remarks>
    /// <returns>Whether concurrent commits are supported.</returns>
    bool TryEnableParallelCommit() => false;
}
