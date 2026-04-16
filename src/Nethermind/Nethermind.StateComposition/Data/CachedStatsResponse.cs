// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Always-current state composition stats. Default value represents "no scan
/// has completed yet" — consumers gate on <see cref="LastScanMetadata"/>'s
/// <see cref="ScanMetadata.IsComplete"/> flag rather than nullable wrappers.
/// </summary>
public readonly record struct CachedStatsResponse
{
    /// <summary>Live cumulative stats — initialized from scan, then updated by diffs.</summary>
    public CumulativeSizeStats CurrentStats { get; init; }

    /// <summary>Per-depth trie node distribution. Default until the first scan completes.</summary>
    public TrieDepthDistribution TrieDistribution { get; init; }

    /// <summary>Block number these stats correspond to. 0 until the first scan completes.</summary>
    public long BlockNumber { get; init; }

    public int DiffsSinceLastScan { get; init; }

    /// <summary>Metadata from the last full scan; <see cref="ScanMetadata.IsComplete"/> false until a scan finishes.</summary>
    public ScanMetadata LastScanMetadata { get; init; }
}
