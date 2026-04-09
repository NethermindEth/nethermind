// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

/// <summary>
/// Thread-safe implementation of <see cref="IStateCompositionStateHolder"/>.
/// Stores baseline scan results and scan lifecycle state.
/// </summary>
public sealed class StateCompositionStateHolder : IStateCompositionStateHolder
{
    private readonly Lock _lock = new();

    private StateCompositionStats _currentStats;
    private TrieDepthDistribution _currentDistribution;
    private ScanMetadata? _lastScanMetadata;
    private bool _isInitialized;

    private CumulativeSizeStats? _incrementalStats;
    private readonly CumulativeDepthStats _currentDepthStats = new();
    private long _incrementalBlock;
    private int _diffsSinceBaseline;
    private Hash256? _lastProcessedStateRoot;

    public StateCompositionStats CurrentStats
    {
        get { lock (_lock) return _currentStats; }
    }

    public TrieDepthDistribution CurrentDistribution
    {
        get { lock (_lock) return _currentDistribution; }
    }

    public ScanMetadata? LastScanMetadata
    {
        get { lock (_lock) return _lastScanMetadata; }
    }

    public bool IsInitialized { get { lock (_lock) return _isInitialized; } }

    public CumulativeSizeStats? IncrementalStats
    {
        get { lock (_lock) return _incrementalStats; }
    }

    public CumulativeDepthStats CurrentDepthStats
    {
        get { lock (_lock) return _currentDepthStats; }
    }

    public long IncrementalBlock
    {
        get { lock (_lock) return _incrementalBlock; }
    }

    public int DiffsSinceBaseline
    {
        get { lock (_lock) return _diffsSinceBaseline; }
    }

    public Hash256? LastProcessedStateRoot
    {
        get { lock (_lock) return _lastProcessedStateRoot; }
    }

    public void SetBaseline(StateCompositionStats stats, TrieDepthDistribution dist)
    {
        lock (_lock)
        {
            _currentStats = stats;
            _currentDistribution = dist;
            _isInitialized = true;
        }
    }

    public void MarkScanCompleted(long blockNumber, Hash256 stateRoot, TimeSpan duration)
    {
        lock (_lock)
        {
            _lastScanMetadata = new ScanMetadata
            {
                BlockNumber = blockNumber,
                StateRoot = stateRoot,
                CompletedAt = DateTimeOffset.UtcNow,
                Duration = duration,
                IsComplete = true,
            };
        }
    }

    public void InitializeIncremental(CumulativeSizeStats baseline, long blockNumber, Hash256 stateRoot,
        TrieDepthDistribution? depthDistribution = null)
    {
        lock (_lock)
        {
            _incrementalStats = baseline;
            _incrementalBlock = blockNumber;
            _diffsSinceBaseline = 0;
            _lastProcessedStateRoot = stateRoot;
            _currentDepthStats.Reset();
            if (depthDistribution.HasValue)
                _currentDepthStats.SeedFromScan(depthDistribution.Value);
        }
    }

    public void UpdateIncremental(CumulativeSizeStats updated, long blockNumber, Hash256 stateRoot,
        DepthDelta? depthDelta = null)
    {
        lock (_lock)
        {
            _incrementalStats = updated;
            _incrementalBlock = blockNumber;
            _diffsSinceBaseline++;
            _lastProcessedStateRoot = stateRoot;
            if (depthDelta is not null)
                _currentDepthStats.ApplyDelta(depthDelta);
        }
    }

    public void RestoreFromSnapshot(StateCompositionSnapshot snapshot)
    {
        lock (_lock)
        {
            _incrementalStats = snapshot.Stats;
            _incrementalBlock = snapshot.BlockNumber;
            _diffsSinceBaseline = snapshot.DiffsSinceBaseline;
            _lastProcessedStateRoot = snapshot.StateRoot;
            // _isInitialized stays false — baseline scan data (TopN, distribution)
            // is not persisted. getCachedStats() returns incremental stats;
            // getTrieDistribution() requires a fresh scan.
            // TODO (Phase C): restore _currentDepthStats from snapshot when snapshot schema is extended.
        }
    }
}
