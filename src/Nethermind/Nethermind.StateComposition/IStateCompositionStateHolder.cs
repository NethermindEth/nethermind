// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

/// <summary>
/// Single source of truth for initial scan results.
/// Phase 2 incremental tracking uses StateCompositionTracker (LRU cache)
/// instead of ApplyDelta — no delta/stale methods needed here.
/// </summary>
public interface IStateCompositionStateHolder
{
    StateCompositionStats CurrentStats { get; }
    TrieDepthDistribution CurrentDistribution { get; }
    ScanMetadata? LastScanMetadata { get; }
    bool IsInitialized { get; }
    bool IsScanning { get; }
    double ScanProgress { get; }
    void SetBaseline(StateCompositionStats stats, TrieDepthDistribution dist);
    void MarkScanStarted();
    void MarkScanCompleted(long blockNumber, Hash256 stateRoot, TimeSpan duration);
    void UpdateProgress(double progress);
}
