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
    public const int DefaultMaxLeafEntries = 512;

    /// <summary>Default minimum entries per leaf b-tree node — once reached, the
    /// builder may split early if the next entry would worsen the per-leaf encoding
    /// (max separator length grows, or common prefix shrinks).</summary>
    public const int DefaultMinLeafEntries = 16;

    /// <summary>Hard upper bound on children per intermediate node — sanity cap
    /// only; the byte threshold (<see cref="MaxIntermediateBytes"/>) is the
    /// normal binding constraint.</summary>
    public const int DefaultMaxIntermediateEntries = 1024;

    /// <summary>Byte budget per intermediate node — accumulation stops when the
    /// next child would push the estimated node size over this threshold. Higher
    /// values flatten the tree (fewer levels = fewer cache misses per lookup) at
    /// the cost of a larger per-node binary search.</summary>
    public const int DefaultMaxIntermediateBytes = 2048;

    /// <summary>Minimum length of separators stored in leaf nodes.</summary>
    public int MinSeparatorLength { get; init; } = 0;

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

    /// <summary>Shared default instance — used when callers pass <c>null</c>.</summary>
    public static HsstBTreeOptions Default { get; } = new();
}
