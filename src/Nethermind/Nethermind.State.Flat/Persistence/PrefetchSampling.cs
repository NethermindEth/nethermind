// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// What recent prefetch decisions of one persistence found, per column, so the decision of a block costs as little as
/// the state of the database allows.
/// </summary>
/// <remarks>
/// A block is prefetched only when a sampled key misses the block cache and a timed read of it takes disk time. The
/// outcome is remembered: after a disk read, the next <see cref="RecheckInterval"/> misses are trusted to be disk reads
/// and send at once; after misses read from the OS page cache, the next <see cref="RecheckInterval"/> blocks skip the
/// decision; after a fully cached block, the next block probes fewer samples. Trusting a stale outcome only delays a
/// prefetch or sends one the reads did not need, it never changes what a read returns. Concurrent decisions may
/// consume the same count twice; that only rechecks one block earlier.
/// </remarks>
public sealed class PrefetchSampling
{
    /// <summary>How many blocks trust a disk or page-cache outcome before a block decides again.</summary>
    public const int RecheckInterval = 16;

    public Column Accounts { get; } = new();
    public Column Slots { get; } = new();

    public sealed class Column
    {
        private int _trustedDiskMisses;
        private int _trustedMemoryBlocks;
        private volatile bool _lastBlockCached;

        /// <summary>Whether the previous decided block of this column found every probed sample in the block cache.</summary>
        public bool LastBlockCached => _lastBlockCached;

        /// <summary>Whether a block-cache miss can be taken as a disk read without timing it; consumes one trust.</summary>
        public bool TrustMissAsDiskRead() => TryConsume(ref _trustedDiskMisses);

        /// <summary>Whether this block can skip the decision because recent misses were read from the page cache; consumes one trust.</summary>
        public bool TrustMissesInMemory() => TryConsume(ref _trustedMemoryBlocks);

        /// <summary>Records that a timed miss was read from disk.</summary>
        public void RecordDiskRead()
        {
            _lastBlockCached = false;
            Volatile.Write(ref _trustedMemoryBlocks, 0);
            Volatile.Write(ref _trustedDiskMisses, RecheckInterval);
        }

        /// <summary>Records a block that needs no prefetch: every miss, if <paramref name="missed"/>, was read at memory speed.</summary>
        public void RecordInMemory(bool missed)
        {
            _lastBlockCached = !missed;
            if (missed)
            {
                Volatile.Write(ref _trustedDiskMisses, 0);
                Volatile.Write(ref _trustedMemoryBlocks, RecheckInterval);
            }
        }

        private static bool TryConsume(ref int count)
        {
            if (Volatile.Read(ref count) <= 0) return false;
            Interlocked.Decrement(ref count);
            return true;
        }
    }
}
