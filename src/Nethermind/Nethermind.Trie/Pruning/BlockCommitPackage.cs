// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Trie.Pruning
{
    public class BlockCommitSet(long blockNumber)
    {
        public long BlockNumber { get; } = blockNumber;

        public TrieNode? Root { get; private set; }

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

    }
}
