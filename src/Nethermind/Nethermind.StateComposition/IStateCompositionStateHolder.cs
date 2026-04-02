// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

/// <summary>
/// Multi-block cache for scan results, persisted to disk.
/// </summary>
public interface IStateCompositionStateHolder
{
    /// <summary>
    /// Get cached scan for a specific block. Pass null to get the most recent scan.
    /// </summary>
    ScanCacheEntry? GetScan(long? blockNumber);

    /// <summary>Whether any scan has been completed and cached.</summary>
    bool HasAnyScan { get; }

    /// <summary>Whether a scan exists for the given block number.</summary>
    bool HasScan(long blockNumber);

    /// <summary>List metadata for all cached scans, ordered by block number ascending.</summary>
    IReadOnlyList<ScanMetadata> ListScans();

    /// <summary>
    /// Store a completed scan result. Persists to disk.
    /// </summary>
    void StoreScan(long blockNumber, Hash256 stateRoot, TimeSpan duration,
                   StateCompositionStats stats, TrieDepthDistribution dist);
}
