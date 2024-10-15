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
    public interface ITrieStore : IDisposable, ITrieStoreInternal
    {
        IReadOnlyTrieStore AsReadOnly(INodeStorage? keyValueStore = null);

        event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        // Used for serving via hash
        IReadOnlyKeyValueStore TrieNodeRlpStore { get; }

        // Used by healing
        void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] rlp);

        bool HasRoot(Hash256 stateRoot);

        IScopedTrieStore GetTrieStore(Hash256? address);
        INodeStorage.KeyScheme Scheme { get; }

        IBlockCommitter BeginBlockCommit(long blockNumber);
    }

    /// <summary>
    /// These methods are to be used by ScopedTrieStore.
    /// It should be considered internal to TrieStore.
    /// It should be used directly, nor intercepted.
    /// </summary>
    public interface ITrieStoreInternal
    {
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

    public interface IBlockCommitter : IDisposable
    {
        ICommitter GetTrieCommitter(Hash256? address, TrieNode? root, WriteFlags writeFlags = WriteFlags.None);
        bool TryRequestConcurrencyQuota() => false;
        void ReturnConcurrencyQuota() { }
    }
}
