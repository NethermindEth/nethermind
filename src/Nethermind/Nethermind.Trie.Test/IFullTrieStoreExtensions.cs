// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Test;

internal static class IFullTrieStoreExtensions
{
    // Small utility to not having to double wrap
    public static ICommitter BeginStateBlockCommit(this ITrieStore trieStore, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        IBlockCommitter blockCommitter = trieStore.BeginBlockCommit(blockNumber);
        ICommitter stateTreeCommitter = trieStore.GetTrieStore(null).BeginCommit(root, writeFlags: writeFlags);
        return new CommitterWithBlockCommitter(blockCommitter, stateTreeCommitter);
    }

    public static void CommitPatriciaTrie(this ITrieStore trieStore, long blockNumber, PatriciaTree patriciaTree)
    {
        using (trieStore.BeginBlockCommit(blockNumber)) { patriciaTree.Commit(); }
    }

    private class CommitterWithBlockCommitter(IBlockCommitter blockCommitter, ICommitter baseCommitter) : ICommitter
    {
        public void Dispose()
        {
            baseCommitter.Dispose();
            blockCommitter.Dispose();
        }

        public void CommitNode(ref TreePath path, NodeCommitInfo nodeCommitInfo)
        {
            baseCommitter.CommitNode(ref path, nodeCommitInfo);
        }
    }
}
