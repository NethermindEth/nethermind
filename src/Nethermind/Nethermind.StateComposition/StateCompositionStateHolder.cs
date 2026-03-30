// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

/// <summary>
/// Thread-safe implementation of <see cref="IStateCompositionStateHolder"/>.
/// Stores baseline scan results and scan progress state.
/// </summary>
public sealed class StateCompositionStateHolder : IStateCompositionStateHolder
{
    private readonly object _lock = new();

    private StateCompositionStats _currentStats;
    private TrieDepthDistribution _currentDistribution;
    private ScanMetadata? _lastScanMetadata;
    private volatile bool _isInitialized;
    private volatile bool _isScanning;
    private double _scanProgress;
    private long _baselineBlock;
    private long _headBlock;

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

    public bool IsInitialized => _isInitialized;
    public bool IsScanning => _isScanning;

    public double ScanProgress
    {
        get { lock (_lock) return _scanProgress; }
    }

    /// <summary>
    /// Number of blocks processed since last baseline scan.
    /// Used by getCachedStats to indicate staleness.
    /// </summary>
    public long BlocksSinceBaseline => Math.Max(0, _headBlock - _baselineBlock);

    public void SetBaseline(StateCompositionStats stats, TrieDepthDistribution dist)
    {
        lock (_lock)
        {
            _currentStats = stats;
            _currentDistribution = dist;
            _baselineBlock = stats.BlockNumber;
            _isInitialized = true;
        }
    }

    public void MarkScanStarted()
    {
        lock (_lock)
        {
            _isScanning = true;
            _scanProgress = 0.0;
        }
    }

    public void MarkScanCompleted(long blockNumber, Hash256 stateRoot, TimeSpan duration)
    {
        lock (_lock)
        {
            _isScanning = false;
            _scanProgress = 1.0;
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

    public void UpdateProgress(double progress)
    {
        lock (_lock)
        {
            _scanProgress = Math.Clamp(progress, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Track head block for staleness indicator.
    /// Called externally when new blocks are processed.
    /// </summary>
    public void UpdateHeadBlock(long blockNumber)
    {
        Interlocked.Exchange(ref _headBlock, blockNumber);
    }
}
