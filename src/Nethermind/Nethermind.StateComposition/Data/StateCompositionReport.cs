// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Always-current state composition report returned by <c>statecomp_get</c>.
/// Default value represents "no scan has completed yet" — consumers gate on
/// <see cref="LastScanMetadata"/>'s <see cref="ScanMetadata.IsComplete"/> flag
/// rather than nullable wrappers.
/// </summary>
public readonly record struct StateCompositionReport
{
    /// <summary>Live cumulative trie stats — initialized from scan, then updated by diffs.</summary>
    public CumulativeTrieStats TrieStats { get; init; }

    /// <summary>Per-depth trie node distribution. Default until the first scan completes.</summary>
    public TrieDepthDistribution TrieDistribution { get; init; }

    /// <summary>Block number these stats correspond to. 0 until the first scan completes.</summary>
    public long BlockNumber { get; init; }

    public int DiffsSinceBaseline { get; init; }

    /// <summary>Metadata from the last full scan; <see cref="ScanMetadata.IsComplete"/> false until a scan finishes.</summary>
    public ScanMetadata LastScanMetadata { get; init; }
}
