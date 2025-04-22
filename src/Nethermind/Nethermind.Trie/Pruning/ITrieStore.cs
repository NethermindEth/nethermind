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
    public interface ITrieStore : IDisposable, ITrieStoreInternal, IStoreWithReorgBoundary
    {
        IReadOnlyTrieStore AsReadOnly(INodeStorage? keyValueStore = null);

        // Used for serving via hash
        IReadOnlyKeyValueStore TrieNodeRlpStore { get; }

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
    /// These methods are to be used by ScopedTrieStore.
    /// It should be considered internal to TrieStore.
    /// It should not be used directly, nor intercepted.
    /// </summary>
    public interface ITrieStoreInternal
    {
        // Used by healing
        void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] rlp);
        ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags);
        TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash);
        byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
        byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
        bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak);
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
