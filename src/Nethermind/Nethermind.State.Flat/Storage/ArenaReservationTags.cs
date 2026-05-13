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
    /// <summary>Metadata reservation for a small-tier snapshot (To-From &lt; CompactSize).</summary>
    public const string BlobBackedSmall = "BlobBackedSmall";

    /// <summary>Metadata reservation for a large-tier snapshot (To-From &gt;= CompactSize).</summary>
    public const string BlobBackedLarge = "BlobBackedLarge";

    /// <summary>In-memory temp arena used during NWayMergeSnapshots (metadata merge).</summary>
    public const string TempLinkedConversion = "TempLinkedConversion";

    /// <summary>Tests / benchmarks creating reservations directly.</summary>
    public const string Test = "Test";
}
