// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Format/structural options for an HSST b-tree built by <see cref="HsstBuilder{TWriter}"/>.
/// Bundled into a single value so call sites read as a property bag rather than a wall of
/// named arguments. Sizing hints (e.g. <c>expectedKeyCount</c>) and the writer remain
/// separate parameters on the builder — they are not format options.
/// </summary>
public sealed record HsstBTreeOptions
{
    /// <summary>Default cap on entries per leaf b-tree node.</summary>
    public const int DefaultMaxLeafEntries = 256;

    /// <summary>Default minimum entries per leaf b-tree node — once reached, the
    /// builder may split early if the next entry would worsen the per-leaf encoding
    /// (max separator length grows, or common prefix shrinks).</summary>
    public const int DefaultMinLeafEntries = 256;

    /// <summary>Default cap on children per intermediate b-tree node (fan-out).</summary>
    public const int DefaultMaxIntermediateEntries = 256;

    /// <summary>Minimum length of separators stored in leaf nodes.</summary>
    public int MinSeparatorLength { get; init; } = 0;

    /// <summary>When true, append a file-level open-addressed hash index after the root node.</summary>
    public bool UseHashIndex { get; init; } = false;

    /// <summary>Target load factor for the file-level hash index. Must be in (0.1, 1.0].</summary>
    public double HashIndexTargetUtilization { get; init; } = 0.75;

    /// <summary>Maximum entries per leaf node before the builder splits.</summary>
    public int MaxLeafEntries { get; init; } = DefaultMaxLeafEntries;

    /// <summary>Minimum entries per leaf node — accumulation always reaches this
    /// before the dynamic-split heuristics (max-sep growth, common-prefix shrink)
    /// are allowed to fire. Set equal to <see cref="MaxLeafEntries"/> to disable
    /// the dynamic split.</summary>
    public int MinLeafEntries { get; init; } = DefaultMinLeafEntries;

    /// <summary>Maximum children per intermediate node (fan-out).</summary>
    public int MaxIntermediateEntries { get; init; } = DefaultMaxIntermediateEntries;

    /// <summary>Shared default instance — used when callers pass <c>null</c>.</summary>
    public static HsstBTreeOptions Default { get; } = new();
}
