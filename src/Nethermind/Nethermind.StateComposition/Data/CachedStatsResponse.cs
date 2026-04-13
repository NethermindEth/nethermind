// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Always-current state composition stats.
/// <see cref="CurrentStats"/> is null before the first scan completes.
/// After the initial scan, stats are kept current via incremental diffs on each new block.
/// </summary>
public readonly record struct CachedStatsResponse
{
    /// <summary>Live cumulative stats — initialized from scan, then updated by diffs.</summary>
    public CumulativeSizeStats? CurrentStats { get; init; }

    /// <summary>Block number these stats correspond to.</summary>
    public long? BlockNumber { get; init; }

    /// <summary>Number of diffs applied since the last full scan (0 = fresh from scan).</summary>
    public int DiffsSinceLastScan { get; init; }

    /// <summary>Metadata from the last full scan (block, time, duration).</summary>
    public ScanMetadata? LastScanMetadata { get; init; }
}
