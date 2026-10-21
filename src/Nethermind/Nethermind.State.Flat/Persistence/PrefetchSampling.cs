// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// What recent prefetch decisions of one persistence found, per column, so the decision of a block costs as little as
/// the state of the database allows.
/// </summary>
/// <remarks>
/// A block is prefetched only when a sampled key misses the block cache and a timed read of it takes disk time. The
/// outcome is remembered: after a disk read, the next <see cref="RecheckInterval"/> misses are trusted to be disk reads
/// and send at once; after misses read from the OS page cache, the next <see cref="MemoryRecheckInterval"/> blocks skip
/// the decision; after a block whose every sample is cached, the next <see cref="CachedRunBlocks"/> blocks probe fewer
/// samples, and a partly cold workload rarely starts such a run. Trusting a stale outcome only delays a
/// prefetch or sends one the reads did not need, it never changes what a read returns. Concurrent decisions may
/// consume the same count twice; that only rechecks one block earlier.
/// </remarks>
public sealed class PrefetchSampling
{
    /// <summary>How many block-cache misses are trusted as disk reads before one is timed again.</summary>
    public const int RecheckInterval = 16;

    /// <summary>How many blocks skip the decision after misses read from the page cache.</summary>
    public const int MemoryRecheckInterval = 32;

    /// <summary>How many blocks probe fewer samples after a block found fully cached by all its samples.</summary>
    public const int CachedRunBlocks = 4;

    public Column Accounts { get; } = new();
    public Column Slots { get; } = new();

    public sealed class Column
    {
        private int _trustedDiskMisses;
        private int _trustedMemoryBlocks;
        private int _cachedRunBlocks;

        /// <summary>
        /// Whether this block may probe fewer samples, because a recent block found all of its samples in the block
        /// cache; consumes one of the <see cref="CachedRunBlocks"/> blocks.
        /// </summary>
        public bool TryProbeFewer() => TryConsume(ref _cachedRunBlocks);

        /// <summary>Whether a block-cache miss can be taken as a disk read without timing it; consumes one trust.</summary>
        public bool TrustMissAsDiskRead() => TryConsume(ref _trustedDiskMisses);

        /// <summary>Whether this block can skip the decision because recent misses were read from the page cache; consumes one trust.</summary>
        public bool TrustMissesInMemory() => TryConsume(ref _trustedMemoryBlocks);

        /// <summary>Records that a timed miss was read from disk.</summary>
        public void RecordDiskRead()
        {
            Volatile.Write(ref _cachedRunBlocks, 0);
            Volatile.Write(ref _trustedMemoryBlocks, 0);
            Volatile.Write(ref _trustedDiskMisses, RecheckInterval);
        }

        /// <summary>
        /// Records a block that needs no prefetch: every miss, if <paramref name="missed"/>, was read at memory speed.
        /// A block with no miss among all its samples, <paramref name="allSamples"/>, starts a run of cheaper blocks.
        /// </summary>
        public void RecordInMemory(bool missed, bool allSamples)
        {
            if (missed)
            {
                Volatile.Write(ref _cachedRunBlocks, 0);
                Volatile.Write(ref _trustedDiskMisses, 0);
                Volatile.Write(ref _trustedMemoryBlocks, MemoryRecheckInterval);
            }
            else if (allSamples)
            {
                Volatile.Write(ref _cachedRunBlocks, CachedRunBlocks);
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
