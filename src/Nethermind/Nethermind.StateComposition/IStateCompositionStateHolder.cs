// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

/// <summary>
/// Single source of truth for baseline scan results and incremental diff tracking.
/// </summary>
public interface IStateCompositionStateHolder
{
    StateCompositionStats CurrentStats { get; }
    TrieDepthDistribution CurrentDistribution { get; }
    ScanMetadata? LastScanMetadata { get; }
    bool IsInitialized { get; }

    /// <summary>Live cumulative stats updated by diffs after baseline scan.</summary>
    CumulativeSizeStats? IncrementalStats { get; }

    /// <summary>Live per-depth distribution updated by diffs after baseline scan.</summary>
    CumulativeDepthStats CurrentDepthStats { get; }

    /// <summary>Block number the incremental stats correspond to.</summary>
    long IncrementalBlock { get; }

    /// <summary>Number of diffs applied since last full scan.</summary>
    int DiffsSinceBaseline { get; }

    /// <summary>State root of the last processed block (scan or diff).</summary>
    Hash256? LastProcessedStateRoot { get; }

    void SetBaseline(StateCompositionStats stats, TrieDepthDistribution dist);
    void MarkScanCompleted(long blockNumber, Hash256 stateRoot, TimeSpan duration);

    /// <summary>Initialize incremental tracking from a completed scan.</summary>
    void InitializeIncremental(CumulativeSizeStats baseline, long blockNumber, Hash256 stateRoot,
        TrieDepthDistribution? depthDistribution = null);

    /// <summary>Apply a diff result, advancing incremental stats to a new block.</summary>
    void UpdateIncremental(CumulativeSizeStats updated, long blockNumber, Hash256 stateRoot,
        DepthDelta? depthDelta = null);

    /// <summary>Restore incremental state from a persisted snapshot (warm restart).</summary>
    void RestoreFromSnapshot(StateCompositionSnapshot snapshot);
}
