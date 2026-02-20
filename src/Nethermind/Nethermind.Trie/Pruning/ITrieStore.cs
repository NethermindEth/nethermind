// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Full traditional trie store.
    /// </summary>
    public interface ITrieStore : IDisposable, IScopableTrieStore
    {
        bool HasRoot(Hash256 stateRoot);

        /// <summary>
        /// Checks if the state root exists and the state for the given block number is still available
        /// (i.e., not partially pruned). Implementations that perform pruning should reject blocks
        /// whose state may have been partially pruned.
        /// </summary>
        bool HasRoot(Hash256 stateRoot, long blockNumber) => HasRoot(stateRoot);

        IDisposable BeginScope(BlockHeader? baseBlock);

        IScopedTrieStore GetTrieStore(Hash256? address);

        /// <summary>
        /// Begin a block commit for this block number. This call may be blocked if a memory pruning is currently happening.
        /// This call is required during block processing for memory pruning and reorg boundary to function.
        /// </summary>
        /// <param name="blockNumber"></param>
        /// <returns></returns>
        IBlockCommitter BeginBlockCommit(long blockNumber);
    }

    public interface IScopableTrieStore
    {
        ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags);
        TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash);
        byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
        byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
        bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak);
        INodeStorage.KeyScheme Scheme { get; }
    }

    public interface IPruningTrieStore : ITrieStore
    {
        public void PersistCache(CancellationToken cancellationToken);

        IReadOnlyTrieStore AsReadOnly();

        event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        // Used for serving via hash
        IReadOnlyKeyValueStore TrieNodeRlpStore { get; }

        // Acquire lock, then persist and flush cache.
        // Used for full pruning operation that change underlying node storage.
        TrieStore.StableLockScope PrepareStableState(CancellationToken cancellationToken);
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
