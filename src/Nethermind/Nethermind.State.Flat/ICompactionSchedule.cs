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

    /// <summary>
    /// True when <paramref name="blockNumber"/>'s persisted-snapshot window
    /// (<see cref="GetPersistedSnapshotCompactSize"/>) is exactly <c>CompactSize</c> — a boundary
    /// whose only window is the CompactSized one, with no wider (<c>&gt;CompactSize</c>) merge to
    /// perform. Mutually exclusive with <see cref="IsLargeCompactionBoundary"/>; together they
    /// cover every persistence boundary.
    /// </summary>
    bool IsCompactSizeBoundary(ulong blockNumber);

    /// <summary>
    /// True when <paramref name="blockNumber"/>'s persisted-snapshot window
    /// (<see cref="GetPersistedSnapshotCompactSize"/>) is strictly larger than <c>CompactSize</c> —
    /// a boundary that carries a wider (<c>&gt;CompactSize</c>) merge on top of the CompactSized
    /// window. Mutually exclusive with <see cref="IsCompactSizeBoundary"/>; together they cover
    /// every persistence boundary.
    /// </summary>
    bool IsLargeCompactionBoundary(ulong blockNumber);

    /// <summary>
    /// The persisted-snapshot compaction tier for <paramref name="blockNumber"/> — the lowest
    /// power of 2 that divides <c>blockNumber + Offset</c>, capped at
    /// <c>PersistedSnapshotMaxCompactSize</c>. Unlike <see cref="GetCompactSize"/> the cap is
    /// <c>PersistedSnapshotMaxCompactSize</c> rather than <c>CompactSize</c>, so callers can act
    /// on the wider merge windows (2×, 4×, …) above the persistence boundary.
    /// </summary>
    ulong GetPersistedSnapshotCompactSize(ulong blockNumber);

    /// <summary>
    /// True when <paramref name="blockNumber"/> is a deferred state-root materialization boundary for the
    /// given <paramref name="commitBatchSize"/> — i.e. genesis (block 0), or a block where the trie must be
    /// materialized and its root verified. Uses the same per-instance offset as the persistence boundaries,
    /// so that (because <c>CompactSize</c> is an exact multiple of <paramref name="commitBatchSize"/>) every
    /// persistence boundary is guaranteed to also be a materialization boundary regardless of the offset.
    /// </summary>
    /// <remarks>
    /// Returns <c>true</c> for every block when <paramref name="commitBatchSize"/> &lt;= 1, preserving the
    /// per-block behavior (the trie is materialized and verified every block).
    /// </remarks>
    bool IsMaterializationBoundary(ulong blockNumber, ulong commitBatchSize);
}
