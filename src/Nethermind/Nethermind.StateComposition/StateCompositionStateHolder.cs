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
    private volatile bool _isInitialized;
    private volatile bool _isScanning;

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

    public void SetBaseline(StateCompositionStats stats, TrieDepthDistribution dist)
    {
        lock (_lock)
        {
            _currentStats = stats;
            _currentDistribution = dist;
            _isInitialized = true;
        }
    }

    public void MarkScanStarted()
    {
        _isScanning = true;
    }

    public void MarkScanCompleted(long blockNumber, Hash256 stateRoot, TimeSpan duration)
    {
        lock (_lock)
        {
            _isScanning = false;
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
}
