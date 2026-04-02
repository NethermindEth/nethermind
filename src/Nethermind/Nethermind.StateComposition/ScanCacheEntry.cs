// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

/// <summary>
/// Bundles all scan outputs for a single block — stats, distribution, and metadata.
/// Persisted as JSON to survive node restarts.
/// </summary>
public readonly record struct ScanCacheEntry
{
    public StateCompositionStats Stats { get; init; }
    public TrieDepthDistribution Distribution { get; init; }
    public ScanMetadata Metadata { get; init; }
}
