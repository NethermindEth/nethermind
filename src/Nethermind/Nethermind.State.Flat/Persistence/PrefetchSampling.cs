// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// What recent prefetch decisions of one persistence found, per column, so a run of disk-cold blocks pays the timed
/// read of a sampled miss only once every <see cref="DiskRecheckInterval"/> blocks.
/// </summary>
/// <remarks>
/// A block whose sampled keys miss the block cache is prefetched only when a miss is read from disk, which a timed
/// read of that key tells. After a disk read the next misses are trusted to be disk reads too and send the prefetch at
/// once; a page-cache read is cheap, so no state is kept for it and every such block is timed. Concurrent decisions
/// may consume the same count twice; that only times one block earlier.
/// </remarks>
public sealed class PrefetchSampling
{
    /// <summary>How many disk-cold blocks in a row send the prefetch without a timed read before one is timed again.</summary>
    public const int DiskRecheckInterval = 16;

    public Column Accounts { get; } = new();
    public Column Slots { get; } = new();

    public sealed class Column
    {
        private int _trustedDiskMisses;

        /// <summary>Whether a block-cache miss can be taken as a disk read without timing it; consumes one trust.</summary>
        public bool TrustMissAsDiskRead()
        {
            if (Volatile.Read(ref _trustedDiskMisses) <= 0) return false;
            Interlocked.Decrement(ref _trustedDiskMisses);
            return true;
        }

        /// <summary>Records that a timed miss was read from disk.</summary>
        public void RecordDiskRead() => Volatile.Write(ref _trustedDiskMisses, DiskRecheckInterval);
    }
}
