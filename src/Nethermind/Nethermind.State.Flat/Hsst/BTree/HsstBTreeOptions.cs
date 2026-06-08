// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Format/structural options for an HSST b-tree built by <see cref="HsstBTreeBuilder{TWriter}"/>.
/// Bundled into a single value so call sites read as a property bag rather than a wall of
/// named arguments. Sizing hints (e.g. <c>expectedKeyCount</c>) and the writer remain
/// separate parameters on the builder — they are not format options.
/// </summary>
public sealed record HsstBTreeOptions
{
    /// <summary>Default cap on entries per leaf b-tree node.</summary>
    public const int DefaultMaxLeafEntries = 512;

    /// <summary>Default minimum entries per leaf b-tree node — once reached, the
    /// builder may split early if the next entry would worsen the per-leaf encoding
    /// (max separator length grows, or common prefix shrinks).</summary>
    public const int DefaultMinLeafEntries = 16;

    /// <summary>Hard upper bound on children per intermediate node — sanity cap
    /// only; the byte threshold (<see cref="MaxIntermediateBytes"/>) is the
    /// normal binding constraint.</summary>
    public const int DefaultMaxIntermediateEntries = 2048;

    /// <summary>Byte budget per intermediate node — accumulation stops when the
    /// next child would push the estimated node size over this threshold. Higher
    /// values flatten the tree (fewer levels = fewer cache misses per lookup) at
    /// the cost of a larger per-node binary search. Set to one 4 KiB page so each
    /// intermediate fits in a single page-aligned pin window.</summary>
    public const int DefaultMaxIntermediateBytes = 4096;

    /// <summary>Default minimum children per intermediate node — once reached,
    /// the builder may split early if the next child would worsen the per-node
    /// encoding (max separator length grows, value slot widens) or push the
    /// node across a 4 KiB page boundary.</summary>
    public const int DefaultMinIntermediateChildren = 16;

    /// <summary>Default minimum estimated byte length per intermediate node —
    /// once reached, the dynamic-split heuristics are allowed to fire. 0 disables
    /// the byte-length gate (only <see cref="DefaultMinIntermediateChildren"/>
    /// gates).</summary>
    public const int DefaultMinIntermediateBytes = 0;

    /// <summary>Maximum entries per leaf node before the builder splits.</summary>
    public int MaxLeafEntries { get; init; } = DefaultMaxLeafEntries;

    /// <summary>Minimum entries per leaf node — accumulation always reaches this
    /// before the dynamic-split heuristics (max-sep growth, common-prefix shrink)
    /// are allowed to fire. Set equal to <see cref="MaxLeafEntries"/> to disable
    /// the dynamic split.</summary>
    public int MinLeafEntries { get; init; } = DefaultMinLeafEntries;

    /// <summary>Maximum children per intermediate node (fan-out). Hard upper bound
    /// that prevents pathological cases; <see cref="MaxIntermediateBytes"/> is the
    /// usual binding constraint.</summary>
    public int MaxIntermediateEntries { get; init; } = DefaultMaxIntermediateEntries;

    /// <summary>Byte budget for intermediate node size — the builder packs
    /// children until the next would push the estimated node bytes over this
    /// threshold (or the count cap is hit, whichever fires first). Higher values
    /// flatten the tree at the cost of larger per-node binary search.</summary>
    public int MaxIntermediateBytes { get; init; } = DefaultMaxIntermediateBytes;

    /// <summary>Minimum children per intermediate node — accumulation always
    /// reaches this before the dynamic-split heuristics (max-sep growth, value-slot
    /// widening, 4 KiB page-crossing) are allowed to fire. Set equal to
    /// <see cref="MaxIntermediateEntries"/> to disable the dynamic split.</summary>
    public int MinIntermediateChildren { get; init; } = DefaultMinIntermediateChildren;

    /// <summary>Minimum estimated byte length per intermediate node — the
    /// committed node must also have reached this size before the dynamic-split
    /// heuristics are allowed to fire (in addition to <see cref="MinIntermediateChildren"/>).
    /// Useful for skinny separators where the child-count floor is reached well
    /// before the node is large enough to benefit from a split. 0 disables the
    /// byte-length gate.</summary>
    public int MinIntermediateBytes { get; init; } = DefaultMinIntermediateBytes;

    /// <summary>Default per-partition key-bytes budget for the partitioned
    /// (<see cref="IndexType.PartitionedBTreeKeyFirst"/>) builder — once the running sum
    /// of entry key bytes reaches this, the partition is closed at the next group
    /// boundary and a fresh one starts. 4 MiB.</summary>
    public const long DefaultPartitionThresholdBytes = 4L * 1024 * 1024;

    /// <summary>Hard cap on a single partition's data section for the partitioned
    /// builder. The per-partition hashtable stores each entry as a <c>u24</c> forward
    /// distance from the data-section start, so the data section (entries + inline
    /// leaves) must stay under 16 MiB; the builder closes a partition once its data
    /// span reaches this. The inner index sits after the data section and is not
    /// addressed by the hashtable, so it does not count. A correctness bound, not a
    /// tuning knob.</summary>
    public const long DefaultPartitionMaxSpanBytes = 16L * 1024 * 1024;

    /// <summary>Default minimum partition key-bytes below which the partitioned
    /// builder skips the per-partition hashtable entirely — a one- or two-level
    /// B-tree already reaches the entry, so a hashtable would not help. 4 KiB.</summary>
    public const int DefaultHashtableMinBytes = 4 * 1024;

    /// <summary>Per-partition key-bytes budget for the partitioned builder; a partition
    /// is closed once the running sum of its entry key bytes reaches this.</summary>
    public long PartitionThresholdBytes { get; init; } = DefaultPartitionThresholdBytes;

    /// <summary>Hard cap on a single partition's on-disk span (see
    /// <see cref="DefaultPartitionMaxSpanBytes"/>); the builder closes a partition before
    /// it can exceed this regardless of <see cref="PartitionThresholdBytes"/>.</summary>
    public long PartitionMaxSpanBytes { get; init; } = DefaultPartitionMaxSpanBytes;

    /// <summary>Minimum partition key-bytes below which no per-partition hashtable is
    /// written (the inner B-tree alone is used).</summary>
    public int HashtableMinBytes { get; init; } = DefaultHashtableMinBytes;

    /// <summary>Shared default instance — used when callers pass <c>null</c>.</summary>
    public static HsstBTreeOptions Default { get; } = new();
}
