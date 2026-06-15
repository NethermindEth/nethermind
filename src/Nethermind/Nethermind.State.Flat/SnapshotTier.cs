// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat;

/// <summary>
/// A snapshot's tier in the two-tier snapshot DAG, spanning the in-memory and persisted tiers.
/// Used as the parameter that selects which store a snapshot operation targets, as the parent-edge
/// classification driving the backward graph walk, and as the on-disk catalog discriminator (only
/// the three <c>Persisted*</c> values are ever serialized — in-memory snapshots have no catalog entry).
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

    /// <summary>Persisted compacted — &gt;<c>CompactSize</c> merges plus the <c>CompactSize</c> persistable. References base blob arenas.</summary>
    PersistedCompacted,

    /// <summary>The <c>CompactSize</c>-wide persistable snapshot written to RocksDB.</summary>
    PersistedPersistable,
}

public static class SnapshotTierExtensions
{
    /// <summary>Whether <paramref name="tier"/> is one of the persisted tiers (vs in-memory).</summary>
    public static bool IsPersisted(this SnapshotTier tier) => tier >= SnapshotTier.PersistedBase;

    /// <summary>Guards the in-memory-only operations: throws when <paramref name="tier"/> is persisted.</summary>
    public static void EnsureInMemory(this SnapshotTier tier)
    {
        if (tier.IsPersisted())
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Only in-memory tiers are valid here.");
    }
}
