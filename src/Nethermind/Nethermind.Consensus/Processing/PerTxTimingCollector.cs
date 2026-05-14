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
/// list prepared on the block-processing thread; assumes a single block is
/// being timing-collected at any moment.
/// </summary>
public static class PerTxTimingCollector
{
    private static bool _enabled;
    // Backing storage is pooled via ArrayPoolList<long>; the underlying array is rented
    // from the shared pool and returned on Dispose. The 256-slot capacity floor keeps
    // small blocks in a single pool bucket to avoid churn.
    private static ArrayPoolList<long>? _ticksPerTx;

    /// <summary>Whether per-tx timing capture is active.</summary>
    public static bool IsEnabled => _enabled;

    /// <summary>Called by ProcessingStats to enable/disable capture.</summary>
    public static void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>Called by the tx executor before processing transactions.</summary>
    public static void Prepare(int txCount)
    {
        // Either Snapshot already handed off the previous list and nulled this reference,
        // or the previous block produced no snapshot (e.g. zero txs). In both cases dispose
        // the leftover (if any) so the pool reclaims its backing array.
        _ticksPerTx?.Dispose();
        _ticksPerTx = new ArrayPoolList<long>(capacity: Math.Max(txCount, 256), count: txCount);
    }

    /// <summary>Called by the tx executor after each transaction.</summary>
    public static void Record(int index, long ticks)
    {
        ArrayPoolList<long>? list = _ticksPerTx;
        if (list is null) return;
        Span<long> span = list.AsSpan();
        if ((uint)index < (uint)span.Length) span[index] = ticks;
    }

    /// <summary>
    /// Hands off the per-tx timing list to the caller, or returns null if no data was captured.
    /// The caller takes ownership and MUST dispose the list (returns its backing array to the
    /// shared pool). The internal reference is cleared so the next <see cref="Prepare"/> rents
    /// a fresh backing array.
    /// </summary>
    public static ArrayPoolList<long>? Snapshot()
    {
        ArrayPoolList<long>? handoff = _ticksPerTx;
        if (!_enabled || handoff is null || handoff.Count == 0) return null;
        _ticksPerTx = null;
        return handoff;
    }
}
