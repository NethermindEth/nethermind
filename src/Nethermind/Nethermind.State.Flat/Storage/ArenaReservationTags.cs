// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Canonical tag values for <see cref="ArenaReservation"/>. Each reservation increments
/// its tag's count + bytes in <see cref="Metrics.ArenaReservationCountByTag"/> /
/// <see cref="Metrics.ArenaReservationBytesByTag"/> on construction and decrements on
/// <see cref="ArenaReservation.CleanUp"/>. Use these constants so we don't get typo
/// drift across call sites; new tags should be added here first.
/// </summary>
public static class ArenaReservationTags
{
    /// <summary>Base arena, Full snapshot (raw, not yet compacted to RocksDB).</summary>
    public const string FullBase = "FullBase";

    /// <summary>Compacted arena, Full snapshot at compactSize boundary (ready to persist to RocksDB).</summary>
    public const string FullPersistable = "FullPersistable";

    /// <summary>Compacted arena, Linked compacted snapshot produced by the compactor.</summary>
    public const string LinkedCompacted = "LinkedCompacted";

    /// <summary>In-memory temp arena used during NWayMergeSnapshots (Full→Linked conversion).</summary>
    public const string TempLinkedConversion = "TempLinkedConversion";

    /// <summary>Tests / benchmarks creating reservations directly.</summary>
    public const string Test = "Test";
}
