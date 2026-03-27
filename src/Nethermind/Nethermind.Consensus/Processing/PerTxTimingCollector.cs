// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Thread-static collector for per-transaction execution timing.
/// Enabled by <see cref="ProcessingStats"/> when SlowBlockPerTxThresholdMs >= 0.
/// The tx executor calls <see cref="Prepare"/> and <see cref="Record"/>;
/// ProcessingStats snapshots via <see cref="Snapshot"/> after block execution.
/// </summary>
public static class PerTxTimingCollector
{
    [ThreadStatic] private static bool _enabled;
    [ThreadStatic] private static long[]? _ticksPerTx;
    [ThreadStatic] private static int _count;

    /// <summary>Whether per-tx timing capture is active on this thread.</summary>
    public static bool IsEnabled => _enabled;

    /// <summary>Called by ProcessingStats to enable/disable capture on the block-processing thread.</summary>
    public static void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>Called by the tx executor before processing transactions.</summary>
    public static void Prepare(int txCount)
    {
        if (_ticksPerTx is null || _ticksPerTx.Length < txCount)
        {
            _ticksPerTx = new long[Math.Max(txCount, 256)];
        }
        _count = txCount;
    }

    /// <summary>Called by the tx executor after each transaction.</summary>
    public static void Record(int index, long ticks)
    {
        if (_ticksPerTx is not null && (uint)index < (uint)_ticksPerTx.Length)
        {
            _ticksPerTx[index] = ticks;
        }
    }

    /// <summary>
    /// Takes a snapshot of per-tx timing data. Returns null if no data was captured.
    /// The returned array is a copy safe to use on another thread.
    /// </summary>
    public static long[]? Snapshot()
    {
        if (!_enabled || _ticksPerTx is null || _count == 0) return null;
        long[] copy = new long[_count];
        Array.Copy(_ticksPerTx, copy, _count);
        return copy;
    }
}
