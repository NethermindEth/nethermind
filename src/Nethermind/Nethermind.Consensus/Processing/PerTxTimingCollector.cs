// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Process-wide collector for per-transaction execution timing.
/// Enabled by <see cref="ProcessingStats"/> when SlowBlockPerTxThresholdMs >= 0.
/// The tx executor calls <see cref="Prepare"/> and <see cref="Record"/>;
/// ProcessingStats snapshots via <see cref="Snapshot"/> after block execution.
/// State is shared across threads so parallel tx workers can record into the
/// array prepared on the block-processing thread; assumes a single block is
/// being timing-collected at any moment.
/// </summary>
public static class PerTxTimingCollector
{
    private static bool _enabled;
    private static long[]? _ticksPerTx;
    private static int _count;

    /// <summary>Whether per-tx timing capture is active.</summary>
    public static bool IsEnabled => _enabled;

    /// <summary>Called by ProcessingStats to enable/disable capture.</summary>
    public static void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>Called by the tx executor before processing transactions.</summary>
    public static void Prepare(int txCount)
    {
        // _ticksPerTx is reused across blocks; only reallocates when capacity must grow.
        // The 256-slot floor amortizes the first few blocks before steady-state size is reached.
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
    /// Takes a snapshot of per-tx timing data, or null if no data was captured.
    /// The returned list rents its backing array from the shared pool and MUST be disposed
    /// when the caller is done (see <see cref="ArrayPoolList{T}.Dispose"/>).
    /// </summary>
    public static ArrayPoolList<long>? Snapshot()
    {
        if (!_enabled || _ticksPerTx is null || _count == 0) return null;

        ArrayPoolList<long> copy = new(capacity: _count, count: _count);
        _ticksPerTx.AsSpan(0, _count).CopyTo(copy.AsSpan());
        return copy;
    }
}
