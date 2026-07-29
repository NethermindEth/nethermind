// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Metric;

namespace Nethermind.State.Flat;

/// <summary>
/// A snapshot's tier in the two-tier snapshot DAG, spanning the in-memory and persisted tiers.
/// Used as the parameter that selects which store a snapshot operation targets, as the parent-edge
/// classification driving the backward graph walk, and as the on-disk catalog discriminator (only
/// the four <c>Persisted*</c> values are ever serialized — in-memory snapshots have no catalog entry).
/// </summary>
/// <remarks>
/// The numeric order is NOT a priority order: traversal priority is expressed by explicit arrays in
/// <c>SnapshotRepository</c>, decoupled from these values. The order is chosen only so that
/// <c>tier &gt;= PersistedBase</c> is exactly "is persisted". Values fit in a single byte and are
/// cast to/from <see langword="byte"/> at the catalog serialization boundary.
/// </remarks>
public enum SnapshotTier
{
    /// <summary>In-memory base — narrow in-RAM hop, no disk read.</summary>
    InMemoryBase,

    /// <summary>In-memory compacted — widest in-RAM hop, no disk read.</summary>
    InMemoryCompacted,

    /// <summary>Persisted base — sub-<c>CompactSize</c>, narrowest persisted hop. Owns a contiguous blob region.</summary>
    PersistedBase,

    /// <summary>Persisted small compacted — sub-<c>CompactSize</c> intermediate merges. References base blob arenas.</summary>
    PersistedSmallCompacted,

    /// <summary>The <c>CompactSize</c>-wide snapshot written to RocksDB.</summary>
    PersistedCompactSized,

    /// <summary>Persisted large compacted — a &gt;<c>CompactSize</c> merge produced at a large-compaction
    /// boundary. The widest persisted skip-pointer. References base blob arenas.</summary>
    PersistedLargeCompacted,
}

public static class SnapshotTierExtensions
{
    public static bool IsPersisted(this SnapshotTier tier) => tier >= SnapshotTier.PersistedBase;

    /// <summary>The metric "tier" label (<c>base</c>/<c>smallcompacted</c>/<c>compactsized</c>/<c>largecompacted</c>) for a persisted
    /// <paramref name="tier"/>. Throws for in-memory tiers, which have no persisted-snapshot metrics.</summary>
    public static string MetricTierLabel(this SnapshotTier tier) => tier switch
    {
        SnapshotTier.PersistedBase => "base",
        SnapshotTier.PersistedSmallCompacted => "smallcompacted",
        SnapshotTier.PersistedCompactSized => "compactsized",
        SnapshotTier.PersistedLargeCompacted => "largecompacted",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a persisted tier."),
    };

    /// <summary>Guards the in-memory-only operations: throws when <paramref name="tier"/> is persisted.</summary>
    public static void EnsureInMemory(this SnapshotTier tier)
    {
        if (tier.IsPersisted())
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Only in-memory tiers are valid here.");
    }
}

/// <summary>Metric key for the per-(<c>tier</c>, <c>size</c>) persisted-snapshot gauges. <c>Size</c> is the
/// snapshot's block span (<c>To - From</c>) — i.e. its compact size.</summary>
public readonly record struct PersistedSnapshotLabel(string Tier, long Size) : IMetricLabels
{
    public string[] Labels => [Tier, Size.ToString()];
}

/// <summary>Metric key for the per-compact-size persisted-snapshot compaction histograms. <c>Size</c>
/// is the actual compacted block span rounded up to the next power of two.</summary>
public readonly record struct CompactSizeLabel(int Size) : IMetricLabels
{
    public string[] Labels => [$"size{Size}"];
}
