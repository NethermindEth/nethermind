// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Provides scoped access to persisted trie nodes.
    /// </summary>
    public interface ITrieStore : IDisposable, IScopableTrieStore
    {
        bool HasRoot(Hash256 stateRoot);

        /// <summary>
        /// Checks whether the state root is available for the given block.
        /// </summary>
        bool HasRoot(Hash256 stateRoot, ulong blockNumber) => HasRoot(stateRoot);

        IDisposable BeginScope(BlockHeader? baseBlock);

        IScopedTrieStore GetTrieStore(Hash256? address);

        /// <summary>
        /// Begin a block commit for this block number.
        /// </summary>
        /// <param name="blockNumber"></param>
        /// <returns></returns>
        IBlockCommitter BeginBlockCommit(ulong blockNumber);
    }

    public interface IScopableTrieStore
    {
        ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags);
        TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash);
        byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
        byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
    }

    /// <summary>
    /// A block committer identifies the scope at which a commit for a block should happen.
    /// The commit started via <see cref="IScopedTrieStore.BeginCommit"/> which is called by <see cref="PatriciaTree.Commit"/>
    /// Depending on <see cref="TryRequestConcurrencyQuota"/>, multiple patricia trie commit may run at the same time.
    /// </summary>
    public interface IBlockCommitter : IDisposable
    {
        bool TryRequestConcurrencyQuota() => false;
        void ReturnConcurrencyQuota() { }
    }
}
