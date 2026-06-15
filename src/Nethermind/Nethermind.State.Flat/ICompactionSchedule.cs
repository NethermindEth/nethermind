// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>
/// A half-open block window <c>(StartBlock, StartBlock + Size]</c> selected for compaction,
/// together with its power-of-2 <see cref="Size"/>.
/// </summary>
public readonly record struct CompactionWindow(long StartBlock, int Size);

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
    /// The persisted-snapshot compaction tier for <paramref name="blockNumber"/> — the lowest
    /// power of 2 that divides <c>blockNumber + Offset</c>, capped at
    /// <c>PersistedSnapshotMaxCompactSize</c>. Unlike <see cref="GetCompactSize"/> the cap is
    /// <c>PersistedSnapshotMaxCompactSize</c> rather than <c>CompactSize</c>, so callers can act
    /// on the wider merge windows (2×, 4×, …) above the persistence boundary.
    /// </summary>
    long GetPersistedSnapshotCompactSize(long blockNumber);

    /// <summary>
    /// The persisted-snapshot (non-persistable) compaction window for <paramref name="blockNumber"/>,
    /// or <c>null</c> when there is nothing to merge — a single-snapshot window or the
    /// <c>CompactSize</c>-wide window reserved for <see cref="GetPersistableCompactionWindow"/>.
    /// </summary>
    /// <remarks>
    /// The window size is <see cref="GetPersistedSnapshotCompactSize"/> (already capped at the
    /// persisted-snapshot max compact size). The start is <c>blockNumber - Size</c>: the alignment
    /// lives in offset-shifted space, but the window's left edge must be the raw block number, so
    /// <c>((b-1)/size)*size</c> would only be correct when the offset is 0.
    /// </remarks>
    CompactionWindow? GetPersistedSnapshotCompactionWindow(long blockNumber);

    /// <summary>
    /// The <c>CompactSize</c>-wide persistable window ending at the boundary block
    /// <paramref name="blockNumber"/> — the window <c>PersistenceManager</c> writes to RocksDB.
    /// Callers must first confirm the block is a boundary via <see cref="IsFullCompactionBoundary"/>.
    /// </summary>
    CompactionWindow GetPersistableCompactionWindow(long blockNumber);

    /// <summary>
    /// True if a produced window of <paramref name="windowSize"/> is a sub-<c>CompactSize</c>
    /// intermediate (strictly smaller than the persistable window), as opposed to the persistable
    /// window or a wider hierarchical merge.
    /// </summary>
    bool IsIntermediateWindow(int windowSize);
}
