// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

public interface ICompactionSchedule
{
    /// <summary>
    /// Compact-size tier (power of 2, capped at <c>CompactSize</c>) that would be triggered
    /// at <paramref name="blockNumber"/>. Considers the per-instance offset so that nodes do
    /// not compact in lockstep. Returns 1 when no compaction should run (block 0 or compaction
    /// disabled).
    /// </summary>
    ulong GetCompactSize(ulong blockNumber);

    /// <summary>
    /// The next block strictly greater than <paramref name="from"/>'s block number at which a full-size
    /// compaction (and hence a persistence boundary) will occur. Returns <see cref="ulong.MaxValue"/>
    /// when compaction is disabled. <see cref="StateId.PreGenesis"/> is treated as the slot before genesis,
    /// so the boundary is anchored at block 0 instead of colliding with the <see cref="ulong.MaxValue"/>
    /// "no further boundary" sentinel.
    /// </summary>
    ulong NextFullCompactionAfter(in StateId from);
}
