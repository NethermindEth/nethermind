// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
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

        /// <summary>
        /// Legacy placeholder contract. Retained transitionally so callers that still depend
        /// on it compile while we migrate to <see cref="GetOrLoadNode"/>. Removed at end of
        /// Phase B follow-up.
        /// </summary>
        TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, in ValueHash256 hash);

        /// <summary>
        /// Returns a fully resolved <see cref="TrieNode"/>: cache hit if the node is cached
        /// with RLP, otherwise resolves via the legacy <see cref="FindCachedOrUnknown"/> +
        /// <see cref="TrieNode.ResolveNode"/> pair, but never publishes the resulting Unknown
        /// placeholder to callers - they receive a typed node.
        /// </summary>
        TrieNode GetOrLoadNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None)
        {
            if (TryGetCachedNode(address, in path, in hash, out TrieNode? cached))
            {
                return cached;
            }

            TrieNode node = FindCachedOrUnknown(address, in path, in hash);
            // Build a tiny per-call resolver that satisfies ITrieNodeResolver for ResolveNode.
            // (TrieStore directly implements this via dirty cache; minimal wrappers route
            // through their underlying scoped resolver.) Here we re-enter through the
            // address-aware LoadRlp path embedded in the placeholder's keccak.
            TreePath pathLocal = path;
            ScopedAdapter adapter = new(this, address);
            TrieNode.ResolveNode(ref node, adapter, in pathLocal, flags);
            return node;
        }

        /// <summary>
        /// Try-style sibling of <see cref="GetOrLoadNode"/>; returns <c>false</c> when RLP cannot
        /// be loaded or decoded, leaving <paramref name="node"/> <see langword="null"/>.
        /// </summary>
        bool TryGetOrLoadNode(Hash256? address, in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None)
        {
            if (TryGetCachedNode(address, in path, in hash, out node))
            {
                return true;
            }

            TrieNode candidate = FindCachedOrUnknown(address, in path, in hash);
            ScopedAdapter adapter = new(this, address);
            TreePath pathCopy = path;
            if (!TrieNode.TryResolveNode(ref candidate, adapter, ref pathCopy, flags))
            {
                node = null;
                return false;
            }
            node = candidate;
            return true;
        }

        private sealed class ScopedAdapter(IScopableTrieStore inner, Hash256? address) : ITrieNodeResolver
        {
            public TrieNode FindCachedOrUnknown(in TreePath path, in ValueHash256 hash) =>
                inner.FindCachedOrUnknown(address, in path, in hash);

            public byte[]? LoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
                inner.LoadRlp(address, in path, in hash, flags);

            public byte[]? TryLoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
                inner.TryLoadRlp(address, in path, in hash, flags);

            public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? storageAddress) =>
                new ScopedAdapter(inner, storageAddress);

            public INodeStorage.KeyScheme Scheme => inner.Scheme;
        }

        /// <summary>
        /// Cache-only lookup. Returns <c>true</c> with the cached resolved node when one
        /// exists. Never allocates a placeholder, never loads RLP. Default returns <c>false</c>;
        /// stores that maintain a node cache override.
        /// </summary>
        bool TryGetCachedNode(Hash256? address, in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            node = null;
            return false;
        }

        byte[]? LoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None);
        byte[]? TryLoadRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None);
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
