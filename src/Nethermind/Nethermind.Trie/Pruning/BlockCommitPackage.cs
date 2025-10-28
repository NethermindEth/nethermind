// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class BlockCommitSet(long blockNumber): IComparable<BlockCommitSet>
    {
        public long BlockNumber { get; } = blockNumber;

        public TrieNode? Root { get; private set; }
        public Hash256 StateRoot => Root?.Keccak ?? Keccak.EmptyTreeHash;

        public bool IsSealed => Root is not null;

        public void Seal(TrieNode? root)
        {
            Root = root;
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
