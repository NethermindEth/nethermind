// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Full traditional trie store that is used by WorldState.
    /// </summary>
    public interface ITrieStore : IDisposable
    {
        bool HasRoot(Hash256 stateRoot);

        IScopedTrieStore GetTrieStore(Hash256? address);
        INodeStorage.KeyScheme Scheme { get; }

        /// <summary>
        /// Begin a block commit for this block number. This call may be blocked if a memory pruning is currently happening.
        /// This call is required during block processing for memory pruning and reorg boundary to function.
        /// </summary>
        /// <param name="blockNumber"></param>
        /// <returns></returns>
        IBlockCommitter BeginBlockCommit(long blockNumber);
    }

    /// <summary>
    /// This is the main trie store. There should be only one instance per nethermind. Used by WorldStateManager.
    /// Probably should be separate from ITrieStore.
    /// </summary>
    public interface IFullTrieStore : ITrieStore
    {
        event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        // Used for serving via hash
        IReadOnlyKeyValueStore TrieNodeRlpStore { get; }

        IReadOnlyTrieStore AsReadOnly(INodeStorage? keyValueStore = null);
    }

    public interface IPruningTrieStore
    {
        public void PersistCache(CancellationToken cancellationToken);
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
