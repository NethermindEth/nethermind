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
    int GetCompactSize(long blockNumber);

    /// <summary>
    /// The next block strictly greater than <paramref name="from"/> at which a full-size
    /// compaction (and hence a persistence boundary) will occur. Returns <see cref="long.MaxValue"/>
    /// when compaction is disabled.
    /// </summary>
    long NextFullCompactionAfter(long from);

    /// <summary>
    /// True if <paramref name="blockNumber"/> sits exactly on a full <c>CompactSize</c>-wide
    /// window — i.e. a persistence boundary — with the per-instance offset applied
    /// transparently.
    /// </summary>
    bool IsFullCompactionBoundary(long blockNumber);

    /// <summary>
    /// Uncapped alignment tier — the lowest power of 2 that divides
    /// <c>blockNumber + Offset</c>. Unlike <see cref="GetCompactSize"/> this is NOT capped at
    /// <c>CompactSize</c>, so callers can identify and act on hierarchical-merge windows
    /// (2×, 4×, …) above the persistence boundary. Callers apply their own caps
    /// (e.g. <c>PersistedSnapshotMaxCompactSize</c>) on top.
    /// </summary>
    long GetHierarchicalCompactSize(long blockNumber);

    /// <summary>
    /// True if <paramref name="blockNumber"/> aligns to a tier strictly larger than
    /// <c>CompactSize</c> — i.e. the block hits a hierarchical-merge boundary above the
    /// persistence boundary. Equivalent to
    /// <c>GetHierarchicalCompactSize(blockNumber) &gt; CompactSize</c>.
    /// </summary>
    bool IsHierarchicalBoundary(long blockNumber);
}
