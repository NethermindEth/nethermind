// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class BlockCommitSet(long blockNumber) : IComparable<BlockCommitSet>
    {
        public long BlockNumber { get; } = blockNumber;

        public TrieNode? Root { get; private set; }
        // Allows callers (sorted-set range bounds) to fix the StateRoot used for comparisons
        // without having to construct an Unknown TrieNode placeholder solely for its keccak.
        // When set, this wins over Root.Keccak; in normal sealed sets it is null and Root drives.
        private Hash256? _stateRootOverride;
        public Hash256 StateRoot => _stateRootOverride ?? Root?.Keccak ?? Keccak.EmptyTreeHash;

        private bool _isSealed;

        /// <summary>
        /// A commit set is sealed once <see cref="Seal"/> has been called, regardless of whether the root is null.
        /// A null root is valid for an empty state trie (e.g., genesis blocks with no allocations).
        /// </summary>
        public bool IsSealed => _isSealed;

        public void Seal(TrieNode? root)
        {
            Root = root;
            _isSealed = true;
        }

        /// <summary>
        /// Seal this commit set as a comparison bound for sorted-set range queries. The
        /// supplied <paramref name="stateRoot"/> is used by <see cref="CompareTo"/> via
        /// <see cref="StateRoot"/> so callers can build min / max bounds without allocating
        /// an <see cref="NodeType.Unknown"/> <see cref="TrieNode"/> as a stand-in.
        /// </summary>
        internal void SealAsBound(Hash256 stateRoot)
        {
            _stateRootOverride = stateRoot;
            _isSealed = true;
        }

        public override string ToString() => $"{BlockNumber}({Root})";

        /// <summary>
        /// Prunes persisted branches of the current commit set root.
        /// </summary>
        public void Prune()
        {
            long start = Stopwatch.GetTimestamp();

            // We assume that the most recent package very likely resolved many persisted nodes and only replaced
            // some top level branches. Any of these persisted nodes are held in cache now so we just prune them here
            // to avoid the references still being held after we prune the cache.
            // We prune them here but just up to two levels deep which makes it a very lightweight operation.
            // Note that currently the TrieNode ResolveChild un-resolves any persisted child immediately which
            // may make this call unnecessary.
            Root?.PrunePersistedRecursively(2);
            Metrics.DeepPruningTime = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        public int CompareTo(BlockCommitSet? other)
        {
            if (ReferenceEquals(this, other)) return 0;
            if (other is null) return 1;
            int comp = BlockNumber.CompareTo(other.BlockNumber);
            if (comp != 0) return comp;
            return StateRoot.CompareTo(other.StateRoot);
        }
    }
}
