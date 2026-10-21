// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// What recent prefetch decisions of one persistence found, per column, so the decision of a block costs as little as
/// the state of the database allows.
/// </summary>
/// <remarks>
/// A block is prefetched only when a sampled key misses the block cache and a timed read of it takes disk time. The
/// outcome is remembered:
/// <list type="bullet">
/// <item>after a disk read, the next <see cref="RecheckInterval"/> blocks with a miss send at once without a timed read;</item>
/// <item>after misses read from the OS page cache, the next <see cref="MemoryRecheckInterval"/> blocks probe one sample
/// only, and a slow read of it turns the outcome to disk at once;</item>
/// <item>after <see cref="CachedBlocksForRun"/> blocks in a row whose every sample is cached, the next
/// <see cref="CachedRunBlocks"/> blocks probe fewer samples, which a partly cold workload rarely starts.</item>
/// </list>
/// Trusting a stale outcome only delays a prefetch or sends one the reads did not need, it never changes what a read
/// returns. Concurrent decisions may consume or overwrite the same count; that only rechecks one block earlier or later.
/// </remarks>
public sealed class PrefetchSampling
{
    /// <summary>How many blocks with a block-cache miss trust a disk outcome before one is timed again.</summary>
    public const int RecheckInterval = 16;

    /// <summary>How many blocks probe a single sample after misses read from the page cache.</summary>
    public const int MemoryRecheckInterval = 32;

    /// <summary>How many blocks in a row must have every sample cached before probing fewer.</summary>
    public const int CachedBlocksForRun = 2;

    /// <summary>How many blocks probe fewer samples after a fully cached streak.</summary>
    public const int CachedRunBlocks = 4;

    public Column Accounts { get; } = new();
    public Column Slots { get; } = new();

    public sealed class Column
    {
        private int _trustedDiskMisses;
        private int _trustedMemoryBlocks;
        private int _fullyCachedStreak;
        private int _cachedRunBlocks;

        /// <summary>Whether this block may probe fewer samples after a fully cached streak; consumes one block of the run.</summary>
        public bool TryProbeFewer() => TryConsume(ref _cachedRunBlocks);

        /// <summary>Whether a block-cache miss can be taken as a disk read without timing it; consumes one trust.</summary>
        public bool TrustMissAsDiskRead() => TryConsume(ref _trustedDiskMisses);

        /// <summary>Whether this block probes a single sample because recent misses were read from the page cache; consumes one trust.</summary>
        public bool TrustMissesInMemory() => TryConsume(ref _trustedMemoryBlocks);

        /// <summary>Records that a timed miss was read from disk.</summary>
        public void RecordDiskRead()
        {
            Volatile.Write(ref _fullyCachedStreak, 0);
            Volatile.Write(ref _cachedRunBlocks, 0);
            Volatile.Write(ref _trustedMemoryBlocks, 0);
            Volatile.Write(ref _trustedDiskMisses, RecheckInterval);
        }

        /// <summary>Records a block whose timed misses were all read at memory speed.</summary>
        public void RecordPageCacheReads()
        {
            Volatile.Write(ref _fullyCachedStreak, 0);
            Volatile.Write(ref _cachedRunBlocks, 0);
            Volatile.Write(ref _trustedDiskMisses, 0);
            Volatile.Write(ref _trustedMemoryBlocks, MemoryRecheckInterval);
        }

        /// <summary>Records a block with no timed miss; <paramref name="fullyCached"/> when all its samples were probed and found.</summary>
        public void RecordNoTimedMiss(bool fullyCached)
        {
            if (!fullyCached)
            {
                Volatile.Write(ref _fullyCachedStreak, 0);
                return;
            }

            if (Interlocked.Increment(ref _fullyCachedStreak) >= CachedBlocksForRun)
            {
                Volatile.Write(ref _fullyCachedStreak, 0);
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
